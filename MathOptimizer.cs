using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Deobfuscator
{
    /// <summary>
    /// Отвечает за оптимизацию математических выражений и сворачивание констант.
    /// Использует симуляцию стека для вычисления вложенных выражений.
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
            int optimizedCount = 0;
            bool globalChanged = true;

            // Несколько проходов для обработки вложенных вызовов Convert.ToInt32()
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
                        bool changed = true;

                        while (changed)
                        {
                            changed = false;

                            for (int i = 0; i < instrs.Count; i++)
                            {
                                var instr = instrs[i];

                                // Ищем вызов метода (Call или Callvirt)
                                if (instr.OpCode.Code == Code.Call || instr.OpCode.Code == Code.Callvirt)
                                {
                                    if (instr.Operand is IMethodDefOrRef calledMethod)
                                    {
                                        string typeName = calledMethod.DeclaringType?.FullName ?? "";
                                        string methodName = calledMethod.Name;

                                        // Проверяем, является ли метод Math или Convert
                                        if (typeName == "System.Math" || typeName == "System.Convert")
                                        {
                                            // Пытаемся вычислить аргументы со стека, идя назад от текущей инструкции
                                            if (TryEvaluateArguments(instrs, i, calledMethod, out object? result, out int argsCount))
                                            {
                                                if (result != null)
                                                {
                                                    // Заменяем последовательность инструкций (аргументы + call) на одну константу
                                                    // Удаляем аргументы (они находятся перед вызовом)
                                                    for (int k = 0; k < argsCount; k++)
                                                    {
                                                        if (i - 1 >= 0)
                                                        {
                                                            instrs.RemoveAt(i - 1);
                                                            i--; // Сдвигаем индекс вызова назад
                                                        }
                                                    }

                                                    // Заменяем сам вызов на константу
                                                    SetConstantValue(instr, result);
                                                    
                                                    optimizedCount++;
                                                    changed = true;
                                                    globalChanged = true;
                                                    _log?.Invoke($"  Optimized {typeName}.{methodName}(...) -> {result}");
                                                }
                                            }
                                        }
                                    }
                                }
                            }

                            if (changed)
                            {
                                CleanupNops(method);
                                body.UpdateInstructionOffsets();
                            }
                        }
                    }
                }
            }

            return optimizedCount;
        }

        /// <summary>
        /// Пытается вычислить аргументы метода, анализируя стек инструкций перед вызовом.
        /// Возвращает результат вычисления и количество потребленных инструкций (аргументов).
        /// </summary>
        private bool TryEvaluateArguments(IList<Instruction> instrs, int callIndex, IMethodDefOrRef method, out object? result, out int argsCount)
        {
            result = null;
            argsCount = 0;

            // Определяем количество аргументов метода
            // Так как IMethodDefOrRef не имеет Properties.Parameters напрямую во всех версиях dnlib,
            // мы будем извлекать аргументы со стека, пока не упремся в не-константу или начало метода.
            // Для методов Math/Convert аргументы обычно 1 или 2.
            
            // Попробуем определить арность через сигнатуру, если доступна, иначе эвристика
            int expectedArgs = 0;
            if (method.MethodSig != null)
            {
                expectedArgs = method.MethodSig.Params.Count;
                if (!method.MethodSig.Static) expectedArgs++; // Если не статический, первый аргумент - this (но Math/Convert статические)
            }
            else
            {
                // Эвристика для известных методов, если сигнатура недоступна
                if (method.Name == "ToInt32" || method.Name == "ToDouble" || method.Name == "Log" || 
                    method.Name == "Sin" || method.Name == "Cos" || method.Name == "Tan" || 
                    method.Name == "Abs" || method.Name == "Round" || method.Name == "Ceiling" || 
                    method.Name == "Floor" || method.Name == "Log10" || method.Name == "Sqrt" ||
                    method.Name == "Tanh" || method.Name == "ToString")
                    expectedArgs = 1;
                else if (method.Name == "Pow" || method.Name == "Atan2" || method.Name == "Max" || method.Name == "Min")
                    expectedArgs = 2;
                else
                    expectedArgs = 1; // По умолчанию
            }

            List<object?> stack = new List<object?>();
            int currentIndex = callIndex - 1;
            int foundArgs = 0;

            // Идем назад, собирая константы
            while (foundArgs < expectedArgs && currentIndex >= 0)
            {
                var currentInstr = instrs[currentIndex];
                
                // Пропускаем NOP и инструкции преобразования типа (conv.i4 и т.д.), если они идут сразу после константы
                if (currentInstr.OpCode.Code == Code.Nop)
                {
                    currentIndex--;
                    continue;
                }

                // Если это инструкция преобразования (Conv_*), проверяем предыдущую
                if (IsConversion(currentInstr))
                {
                    currentIndex--;
                    continue;
                }

                object? val = GetConstantValue(currentInstr);
                if (val != null)
                {
                    stack.Add(val);
                    foundArgs++;
                    currentIndex--;
                }
                else
                {
                    // Если наткнулись на не-константу, значит вычислить нельзя
                    return false;
                }
            }

            if (foundArgs != expectedArgs)
                return false;

            // Стек собран в обратном порядке (последний аргумент первый в списке)
            stack.Reverse();
            argsCount = foundArgs;

            // Вычисляем результат
            try
            {
                string typeName = method.DeclaringType?.FullName ?? "";
                string methodName = method.Name;

                if (typeName == "System.Math")
                {
                    result = EvaluateMath(methodName, stack);
                }
                else if (typeName == "System.Convert")
                {
                    result = EvaluateConvert(methodName, stack);
                }
                
                return result != null;
            }
            catch
            {
                return false;
            }
        }

        private object? EvaluateMath(string methodName, List<object?> args)
        {
            if (args.Count == 0) return null;
            double d1 = Convert.ToDouble(args[0]);

            switch (methodName)
            {
                case "Sin": return Math.Sin(d1);
                case "Cos": return Math.Cos(d1);
                case "Tan": return Math.Tan(d1);
                case "Abs": return Math.Abs(d1);
                case "Sqrt": return Math.Sqrt(d1);
                case "Log": return Math.Log(d1);
                case "Log10": return Math.Log10(d1);
                case "Exp": return Math.Exp(d1);
                case "Ceiling": return Math.Ceiling(d1);
                case "Floor": return Math.Floor(d1);
                case "Round": return Math.Round(d1);
                case "Tanh": return Math.Tanh(d1);
                case "Sinh": return Math.Sinh(d1);
                case "Cosh": return Math.Cosh(d1);
                case "Asin": return Math.Asin(d1);
                case "Acos": return Math.Acos(d1);
                case "Atan": return Math.Atan(d1);
                case "Pow":
                    if (args.Count >= 2)
                    {
                        double d2 = Convert.ToDouble(args[1]);
                        return Math.Pow(d1, d2);
                    }
                    break;
                case "Atan2":
                    if (args.Count >= 2)
                    {
                        double d2 = Convert.ToDouble(args[1]);
                        return Math.Atan2(d1, d2);
                    }
                    break;
                case "Max":
                    if (args.Count >= 2)
                    {
                        double d2 = Convert.ToDouble(args[1]);
                        return Math.Max(d1, d2);
                    }
                    break;
                case "Min":
                    if (args.Count >= 2)
                    {
                        double d2 = Convert.ToDouble(args[1]);
                        return Math.Min(d1, d2);
                    }
                    break;
            }
            return null;
        }

        private object? EvaluateConvert(string methodName, List<object?> args)
        {
            if (args.Count == 0) return null;
            object val = args[0]!;

            try
            {
                switch (methodName)
                {
                    case "ToInt32": return Convert.ToInt32(val);
                    case "ToInt64": return Convert.ToInt64(val);
                    case "ToInt16": return Convert.ToInt16(val);
                    case "ToByte": return Convert.ToByte(val);
                    case "ToSByte": return Convert.ToSByte(val);
                    case "ToUInt32": return Convert.ToUInt32(val);
                    case "ToUInt16": return Convert.ToUInt16(val);
                    case "ToUInt64": return Convert.ToUInt64(val);
                    case "ToDouble": return Convert.ToDouble(val);
                    case "ToSingle": return Convert.ToSingle(val);
                    case "ToString": return Convert.ToString(val);
                    case "ToBoolean": return Convert.ToBoolean(val);
                }
            }
            catch { }
            return null;
        }

        #region Helper Methods

        private bool IsLdc(Instruction instr)
        {
            return instr.OpCode.Code == Code.Ldc_I4 || instr.OpCode.Code == Code.Ldc_I4_S ||
                   instr.OpCode.Code == Code.Ldc_I8 || instr.OpCode.Code == Code.Ldc_R4 ||
                   instr.OpCode.Code == Code.Ldc_R8 || instr.OpCode.Code == Code.Ldstr;
        }

        private bool IsConversion(Instruction instr)
        {
            return instr.OpCode.Code == Code.Conv_I4 || instr.OpCode.Code == Code.Conv_I ||
                   instr.OpCode.Code == Code.Conv_Ovf_I4 || instr.OpCode.Code == Code.Conv_Ovf_I4_Un ||
                   instr.OpCode.Code == Code.Conv_R4 || instr.OpCode.Code == Code.Conv_R8 ||
                   instr.OpCode.Code == Code.Conv_I8 || instr.OpCode.Code == Code.Conv_I2 ||
                   instr.OpCode.Code == Code.Conv_U4 || instr.OpCode.Code == Code.Conv_U8;
        }

        private object? GetConstantValue(Instruction instr)
        {
            switch (instr.OpCode.Code)
            {
                case Code.Ldc_I4:
                case Code.Ldc_I4_S:
                    return instr.Operand as int?;
                case Code.Ldc_I8:
                    return instr.Operand as long?;
                case Code.Ldc_R4:
                    return instr.Operand as float?;
                case Code.Ldc_R8:
                    return instr.Operand as double?;
                case Code.Ldstr:
                    return instr.Operand as string;
                // Поддержка констант, которые уже были вычислены ранее и лежат как объекты (если бы такое было возможно в IL, но здесь только примитивы)
                default:
                    return null;
            }
        }

        private void SetConstantValue(Instruction instr, object value)
        {
            if (value is int iVal)
            {
                instr.OpCode = OpCodes.Ldc_I4;
                instr.Operand = iVal;
            }
            else if (value is long lVal)
            {
                instr.OpCode = OpCodes.Ldc_I8;
                instr.Operand = lVal;
            }
            else if (value is double dVal)
            {
                instr.OpCode = OpCodes.Ldc_R8;
                instr.Operand = dVal;
            }
            else if (value is float fVal)
            {
                instr.OpCode = OpCodes.Ldc_R4;
                instr.Operand = fVal;
            }
            else if (value is string sVal)
            {
                instr.OpCode = OpCodes.Ldstr;
                instr.Operand = sVal;
            }
            else if (value is bool bVal)
            {
                instr.OpCode = OpCodes.Ldc_I4;
                instr.Operand = bVal ? 1 : 0;
            }
            else
            {
                // fallback
                instr.OpCode = OpCodes.Ldc_I4;
                instr.Operand = 0;
            }
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
                        bool isTarget = false;
                        foreach (var ins in body.Instructions)
                        {
                            if (ins.Operand == body.Instructions[i]) { isTarget = true; break; }
                            if (ins.Operand is Instruction[] arr && arr.Contains(body.Instructions[i])) { isTarget = true; break; }
                        }

                        if (!isTarget)
                        {
                            body.Instructions.RemoveAt(i);
                            changed = true;
                        }
                    }
                }
            }
        }

        #endregion
    }
}
