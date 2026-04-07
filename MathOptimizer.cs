using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Deobfuscator
{
    /// <summary>
    /// Отвечает за оптимизацию математических выражений, логических операций и сворачивание констант.
    /// Обрабатывает вызовы Math/Convert и инструкции IL (add, sub, mul, div, xor, and, or, etc.).
    /// </summary>
    public class MathOptimizer
    {
        private readonly Action<string>? _log;

        public MathOptimizer(Action<string>? log = null)
        {
            _log = log;
        }

        public int Optimize(ModuleDef module)
        {
            int totalOptimized = 0;
            bool globalChanged = true;

            // Выполняем проходы до тех пор, пока есть изменения (для обработки цепочек и вложенности)
            while (globalChanged)
            {
                globalChanged = false;

                foreach (var type in module.GetTypes())
                {
                    foreach (var method in type.Methods)
                    {
                        if (!method.HasBody || !method.Body.HasInstructions) continue;

                        var body = method.Body;
                        var instrs = body.Instructions;
                        
                        // Проходим с конца, чтобы безопасно модифицировать список
                        for (int i = instrs.Count - 1; i >= 0; i--)
                        {
                            var instr = instrs[i];
                            var code = instr.OpCode.Code;

                            // 1. Обработка бинарных операций (Xor, Add, Sub, Mul, Div, And, Or, Rem, Shl, Shr)
                            if (IsBinaryOperator(code))
                            {
                                if (TryFoldBinaryOperation(instrs, i, code, out object? result))
                                {
                                    // Заменяем инструкцию операции на константу
                                    instrs[i] = CreateConstantInstruction(result);
                                    
                                    // Превращаем два операнда в NOP
                                    RemovePreviousInstructions(instrs, i, 2);
                                    
                                    totalOptimized++;
                                    globalChanged = true;
                                    _log?.Invoke($"  [Fold] {code} -> {result}");
                                    continue; // Переходим к следующей итерации цикла (индекс i уменьшится сам)
                                }
                            }

                            // 2. Обработка унарных операций (Neg, Not)
                            if (code == Code.Neg || code == Code.Not)
                            {
                                if (TryFoldUnaryOperation(instrs, i, code, out object? result))
                                {
                                    instrs[i] = CreateConstantInstruction(result);
                                    RemovePreviousInstructions(instrs, i, 1);
                                    
                                    totalOptimized++;
                                    globalChanged = true;
                                    _log?.Invoke($"  [Fold] {code} -> {result}");
                                    continue;
                                }
                            }

                            // 3. Обработка вызовов методов (Math, Convert)
                            if (code == Code.Call && instr.Operand is IMethodDefOrRef methodRef)
                            {
                                string typeName = methodRef.DeclaringType?.FullName ?? "";
                                string methodName = methodRef.Name;

                                if (typeName == "System.Math" || typeName == "System.Convert")
                                {
                                    if (TryEvaluateExpression(instrs, i, methodRef, out object? methodResult, out int argsCount))
                                    {
                                        bool hasReturn = methodRef.ReturnType?.ElementType != ElementType.Void;

                                        if (hasReturn)
                                        {
                                            instrs[i] = CreateConstantInstruction(methodResult);
                                        }
                                        else
                                        {
                                            instrs[i].OpCode = OpCodes.Nop;
                                            instrs[i].Operand = null;
                                        }

                                        RemovePreviousInstructions(instrs, i, argsCount);
                                        
                                        totalOptimized++;
                                        globalChanged = true;
                                        _log?.Invoke($"  [Call] {typeName}.{methodName}(...) -> {methodResult}");
                                    }
                                }
                            }
                        }

                        if (globalChanged)
                        {
                            CleanupNops(method);
                            body.UpdateInstructionOffsets();
                        }
                    }
                }
            }

            return totalOptimized;
        }

        #region Helper: Binary Operators

        private bool IsBinaryOperator(Code code)
        {
            return code == Code.Add || code == Code.Sub || code == Code.Mul || code == Code.Div ||
                   code == Code.Rem || code == Code.Xor || code == Code.And || code == Code.Or ||
                   code == Code.Shl || code == Code.Shr;
        }

        private bool TryFoldBinaryOperation(IList<Instruction> instrs, int opIndex, Code code, out object? result)
        {
            result = null;
            if (i < 2) return false; // Нужны минимум 2 предыдущие инструкции

            // Получаем значения двух операндов
            if (!TryGetConstantValue(instrs[opIndex - 2], out object? val1)) return false;
            if (!TryGetConstantValue(instrs[opIndex - 1], out object? val2)) return false;

            // Проверка на ссылки (ветвления)
            if (IsInstructionReferenced(instrs, instrs[opIndex - 2])) return false;
            if (IsInstructionReferenced(instrs, instrs[opIndex - 1])) return false;

            try
            {
                // Приводим к наиболее общему типу для вычислений (double или long для целых)
                // Для побитовых операций важны целые типы
                bool isBitwise = (code == Code.Xor || code == Code.And || code == Code.Or || code == Code.Shl || code == Code.Shr);
                
                if (isBitwise)
                {
                    long l1 = Convert.ToInt64(val1);
                    long l2 = Convert.ToInt64(val2);
                    long res = 0;
                    switch (code)
                    {
                        case Code.Xor: res = l1 ^ l2; break;
                        case Code.And: res = l1 & l2; break;
                        case Code.Or:  res = l1 | l2; break;
                        case Code.Shl: res = l1 << (int)l2; break;
                        case Code.Shr: res = l1 >> (int)l2; break;
                    }
                    // Если исходные были int, возвращаем int
                    if (val1 is int && val2 is int) result = (int)res;
                    else result = res;
                }
                else
                {
                    // Арифметика
                    if (val1 is double d1 || val2 is double d2 || code == Code.Div || code == Code.Rem)
                    {
                        double dd1 = Convert.ToDouble(val1);
                        double dd2 = Convert.ToDouble(val2);
                        double res = 0;
                        switch (code)
                        {
                            case Code.Add: res = dd1 + dd2; break;
                            case Code.Sub: res = dd1 - dd2; break;
                            case Code.Mul: res = dd1 * dd2; break;
                            case Code.Div: res = dd1 / dd2; break;
                            case Code.Rem: res = dd1 % dd2; break;
                        }
                        result = res;
                    }
                    else
                    {
                        long l1 = Convert.ToInt64(val1);
                        long l2 = Convert.ToInt64(val2);
                        long res = 0;
                        switch (code)
                        {
                            case Code.Add: res = l1 + l2; break;
                            case Code.Sub: res = l1 - l2; break;
                            case Code.Mul: res = l1 * l2; break;
                            // Div и Rem обработаны выше как double, но можно добавить integer division если нужно
                        }
                        result = res;
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Helper: Unary Operators

        private bool TryFoldUnaryOperation(IList<Instruction> instrs, int opIndex, Code code, out object? result)
        {
            result = null;
            if (opIndex < 1) return false;

            if (!TryGetConstantValue(instrs[opIndex - 1], out object? val1)) return false;
            if (IsInstructionReferenced(instrs, instrs[opIndex - 1])) return false;

            try
            {
                if (code == Code.Neg)
                {
                    if (val1 is double d) result = -d;
                    else if (val1 is float f) result = -f;
                    else if (val1 is long l) result = -l;
                    else if (val1 is int i) result = -i;
                    else result = -Convert.ToDouble(val1);
                    return true;
                }
                else if (code == Code.Not)
                {
                    long l = Convert.ToInt64(val1);
                    long res = ~l;
                    if (val1 is int) result = (int)res;
                    else result = res;
                    return true;
                }
            }
            catch { }
            return false;
        }

        #endregion

        #region Helper: Method Calls (Math/Convert)

        private bool TryEvaluateExpression(IList<Instruction> instrs, int callIdx, IMethodDefOrRef methodRef, out object? result, out int consumedArgsCount)
        {
            result = null;
            consumedArgsCount = 0;

            int expectedArgs = methodRef.MethodSig?.Params.Count ?? 0;
            
            List<object?> args = new List<object?>(expectedArgs);
            int currentIndex = callIdx - 1;
            int argsFound = 0;

            // Собираем аргументы в обратном порядке
            while (argsFound < expectedArgs && currentIndex >= 0)
            {
                var currentInstr = instrs[currentIndex];
                
                if (currentInstr.OpCode.Code == Code.Nop)
                {
                    currentIndex--;
                    continue;
                }

                if (TryGetConstantValue(currentInstr, out object? val))
                {
                    if (IsInstructionReferenced(instrs, currentInstr)) break; // Нельзя использовать, если есть ветвление
                    args.Add(val);
                    argsFound++;
                    currentIndex--;
                    continue;
                }

                // Рекурсия для вложенных вызовов не нужна здесь, т.к. мы идем снизу вверх и уже свернули всё, что могли, на предыдущих шагах цикла Optimize.
                // Но если вдруг встретился вызов, который еще не свернулся (редко), пробуем его вычислить рекурсивно? 
                // В данном подходе (один проход цикла по инструкциям) мы полагаемся на то, что глобальный цикл while(globalChanged) обработает вложенности на следующих итерациях.
                // Поэтому здесь требуем только константы.
                
                break; // Если не константа, значит не можем вычислить сейчас
            }

            if (argsFound != expectedArgs) return false;

            args.Reverse();

            try
            {
                result = ExecuteMethod(methodRef, args);
                consumedArgsCount = expectedArgs;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool TryGetConstantValue(Instruction instr, out object? value)
        {
            value = null;
            switch (instr.OpCode.Code)
            {
                case Code.Ldc_I4:
                case Code.Ldc_I4_S:
                    value = instr.Operand as int?;
                    return true;
                case Code.Ldc_I8:
                    value = instr.Operand as long?;
                    return true;
                case Code.Ldc_R4:
                    value = instr.Operand as float?;
                    return true;
                case Code.Ldc_R8:
                    value = instr.Operand as double?;
                    return true;
                case Code.Ldstr:
                    value = instr.Operand as string;
                    return true;
                default:
                    return false;
            }
        }

        private object? ExecuteMethod(IMethodDefOrRef methodRef, List<object?> args)
        {
            string name = methodRef.Name;
            string type = methodRef.DeclaringType?.FullName ?? "";

            double GetDouble(int idx) => Convert.ToDouble(args[idx] ?? 0);
            int GetInt32(int idx) => Convert.ToInt32(args[idx] ?? 0);

            if (type == "System.Math")
            {
                switch (name)
                {
                    case "Abs": return Math.Abs(GetDouble(0));
                    case "Acos": return Math.Acos(GetDouble(0));
                    case "Asin": return Math.Asin(GetDouble(0));
                    case "Atan": return Math.Atan(GetDouble(0));
                    case "Atan2": return Math.Atan2(GetDouble(0), GetDouble(1));
                    case "Ceiling": return Math.Ceiling(GetDouble(0));
                    case "Cos": return Math.Cos(GetDouble(0));
                    case "Cosh": return Math.Cosh(GetDouble(0));
                    case "Exp": return Math.Exp(GetDouble(0));
                    case "Floor": return Math.Floor(GetDouble(0));
                    case "Log": return Math.Log(GetDouble(0));
                    case "Log10": return Math.Log10(GetDouble(0));
                    case "Max": return Math.Max(GetDouble(0), GetDouble(1));
                    case "Min": return Math.Min(GetDouble(0), GetDouble(1));
                    case "Pow": return Math.Pow(GetDouble(0), GetDouble(1));
                    case "Round": return (args.Count == 2) ? Math.Round(GetDouble(0), GetInt32(1)) : Math.Round(GetDouble(0));
                    case "Sin": return Math.Sin(GetDouble(0));
                    case "Sinh": return Math.Sinh(GetDouble(0));
                    case "Sqrt": return Math.Sqrt(GetDouble(0));
                    case "Tan": return Math.Tan(GetDouble(0));
                    case "Tanh": return Math.Tanh(GetDouble(0));
                    case "Truncate": return Math.Truncate(GetDouble(0));
                }
            }
            else if (type == "System.Convert")
            {
                object val = args[0] ?? 0;
                switch (name)
                {
                    case "ToBoolean": return Convert.ToBoolean(val);
                    case "ToByte": return Convert.ToByte(val);
                    case "ToChar": return Convert.ToChar(val);
                    case "ToDecimal": return Convert.ToDecimal(val);
                    case "ToDouble": return Convert.ToDouble(val);
                    case "ToInt16": return Convert.ToInt16(val);
                    case "ToInt32": return Convert.ToInt32(val);
                    case "ToInt64": return Convert.ToInt64(val);
                    case "ToSByte": return Convert.ToSByte(val);
                    case "ToSingle": return Convert.ToSingle(val);
                    case "ToString": return Convert.ToString(val);
                    case "ToUInt16": return Convert.ToUInt16(val);
                    case "ToUInt32": return Convert.ToUInt32(val);
                    case "ToUInt64": return Convert.ToUInt64(val);
                }
            }

            throw new NotSupportedException($"Method {type}.{name} not supported.");
        }

        #endregion

        #region Utilities

        private Instruction CreateConstantInstruction(object? value)
        {
            if (value is int i) return OpCodes.Ldc_I4.ToInstruction(i);
            if (value is long l) return OpCodes.Ldc_I8.ToInstruction(l);
            if (value is float f) return OpCodes.Ldc_R4.ToInstruction(f);
            if (value is double d) return OpCodes.Ldc_R8.ToInstruction(d);
            if (value is string s) return OpCodes.Ldstr.ToInstruction(s);
            if (value is bool b) return OpCodes.Ldc_I4.ToInstruction(b ? 1 : 0);
            return OpCodes.Ldc_I4.ToInstruction(0);
        }

        private void RemovePreviousInstructions(IList<Instruction> instrs, int currentIndex, int count)
        {
            for (int k = 0; k < count; k++)
            {
                int idx = currentIndex - 1 - k;
                if (idx >= 0 && idx < instrs.Count)
                {
                    instrs[idx].OpCode = OpCodes.Nop;
                    instrs[idx].Operand = null;
                }
            }
        }

        private bool IsInstructionReferenced(IList<Instruction> instrs, Instruction target)
        {
            foreach (var ins in instrs)
            {
                if (ins.Operand == target) return true;
                if (ins.Operand is Instruction[] arr && arr.Contains(target)) return true;
            }
            return false;
        }

        private void CleanupNops(MethodDef method)
        {
            var body = method.Body;
            if (body == null) return;
            
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = body.Instructions.Count - 1; i >= 0; i--)
                {
                    if (body.Instructions[i].OpCode.Code == Code.Nop)
                    {
                        if (!IsInstructionReferenced(body.Instructions, body.Instructions[i]))
                        {
                            body.Instructions.RemoveAt(i);
                            changed = true;
                        }
                    }
                }
            }
        }
