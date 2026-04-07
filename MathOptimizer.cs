using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Deobfuscator
{
    /// <summary>
    /// Оптимизатор математических выражений с полной поддержкой арифметики и вложенности.
    /// Вычисляет выражения вида: Convert.ToInt32(5000.0 / 50.0) или Convert.ToInt32(999.0 + Math.Tanh(500.0))
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

                        // Идем с конца к началу, чтобы безопасно модифицировать список
                        for (int i = instrs.Count - 1; i >= 0; i--)
                        {
                            var instr = instrs[i];

                            // Нас интересуют вызовы методов (Call, Callvirt)
                            if (instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt) continue;
                            if (instr.Operand is not IMethodDefOrRef methodRef) continue;

                            string typeName = methodRef.DeclaringType?.FullName ?? "";
                            string methodName = methodRef.Name;

                            // Целевые классы: System.Math, System.Convert
                            if (typeName != "System.Math" && typeName != "System.Convert") continue;

                            // Пытаемся вычислить всё выражение, заканчивающееся на этой инструкции
                            if (TryEvaluateExpression(instrs, i, out object? result, out List<int> consumedIndices))
                            {
                                // Проверка безопасности: ни одна из используемых инструкций не должна быть целью перехода
                                bool isSafe = true;
                                foreach (var idx in consumedIndices)
                                {
                                    if (IsInstructionTarget(instrs, instrs[idx]))
                                    {
                                        isSafe = false;
                                        break;
                                    }
                                }

                                if (isSafe)
                                {
                                    // 1. Заменяем сам вызов на константу
                                    Instruction newInstr = CreateConstantInstruction(result);
                                    instrs[i] = newInstr;

                                    // 2. Превращаем аргументы и промежуточные вычисления в NOP
                                    // Сортируем индексы по убыванию, чтобы не сбить нумерацию при замене (хотя мы меняем OpCode, а не удаляем пока)
                                    foreach (var idx in consumedIndices.OrderByDescending(x => x))
                                    {
                                        if (idx != i) // Не трогаем саму инструкцию вызова, она уже заменена
                                        {
                                            instrs[idx].OpCode = OpCodes.Nop;
                                            instrs[idx].Operand = null;
                                        }
                                    }

                                    totalOptimized++;
                                    globalChanged = true;

                                    string valStr = result?.ToString() ?? "null";
                                    if (valStr.Length > 40) valStr = valStr.Substring(0, 40) + "...";
                                    _log?.Invoke($"[MathOpt] Folded {typeName}.{methodName}(...) -> {valStr}");
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

        /// <summary>
        /// Рекурсивно вычисляет значение выражения, заканчивающегося на index.
        /// Возвращает результат и список индексов инструкций, которые были использованы.
        /// </summary>
        private bool TryEvaluateExpression(IList<Instruction> instrs, int index, out object? result, out List<int> consumedIndices)
        {
            result = null;
            consumedIndices = new List<int>();

            var currentInstr = instrs[index];
            
            // Случай 1: Арифметическая операция (add, sub, mul, div, rem, and, or, xor, shl, shr)
            if (IsArithmeticOp(currentInstr.OpCode.Code))
            {
                // Нужно найти два операнда перед этой инструкцией
                if (TryFindOperandValue(instrs, index - 1, out object? val1, out List<int> indices1) &&
                    TryFindOperandValue(instrs, index - 1 - indices1.Count, out object? val2, out List<int> indices2))
                {
                    try
                    {
                        object? res = ExecuteArithmetic(currentInstr.OpCode.Code, val1, val2);
                        if (res != null)
                        {
                            result = res;
                            consumedIndices.AddRange(indices2);
                            consumedIndices.AddRange(indices1);
                            consumedIndices.Add(index);
                            return true;
                        }
                    }
                    catch { }
                }
                return false;
            }

            // Случай 2: Вызов метода (Math.*, Convert.*)
            if ((currentInstr.OpCode.Code == Code.Call || currentInstr.OpCode.Code == Code.Callvirt) && 
                currentInstr.Operand is IMethodDefOrRef methodRef)
            {
                string typeName = methodRef.DeclaringType?.FullName ?? "";
                if (typeName != "System.Math" && typeName != "System.Convert") return false;

                int argCount = methodRef.MethodSig?.Params.Count ?? 0;
                // Для instance методов (хотя Math/Convert статические) мог бы быть this, но здесь игнорируем
                
                List<object?> args = new List<object?>();
                List<int> allArgIndices = new List<int>();
                int currentIndex = index - 1;

                // Собираем аргументы справа налево (стек LIFO)
                for (int a = 0; a < argCount; a++)
                {
                    if (TryFindOperandValue(instrs, currentIndex, out object? argVal, out List<int> argIndices))
                    {
                        args.Add(argVal);
                        allArgIndices.AddRange(argIndices);
                        currentIndex -= argIndices.Count;
                    }
                    else
                    {
                        return false; // Не удалось вычислить аргумент
                    }
                }

                args.Reverse(); // Восстанавливаем порядок аргументов

                try
                {
                    result = ExecuteMethod(methodRef, args);
                    consumedIndices.AddRange(allArgIndices);
                    consumedIndices.Add(index);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            // Случай 3: Простая константа
            if (TryGetConstantValue(currentInstr, out object? constVal))
            {
                result = constVal;
                consumedIndices.Add(index);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Пытается найти значение операнда, начиная с указанной позиции назад.
        /// Учитывает вложенные выражения.
        /// </summary>
        private bool TryFindOperandValue(IList<Instruction> instrs, int startIndex, out object? value, out List<int> consumedIndices)
        {
            value = null;
            consumedIndices = new List<int>();

            if (startIndex < 0) return false;

            // Пропускаем NOP
            int i = startIndex;
            while (i >= 0 && instrs[i].OpCode.Code == Code.Nop)
            {
                i--;
            }
            if (i < 0) return false;

            // Пробуем вычислить выражение, корнем которого является эта инструкция
            if (TryEvaluateExpression(instrs, i, out object? res, out List<int> indices))
            {
                value = res;
                consumedIndices = indices;
                return true;
            }

            return false;
        }

        private bool IsArithmeticOp(Code code)
        {
            return code == Code.Add || code == Code.Add_Ovf || code == Code.Add_Ovf_Un ||
                   code == Code.Sub || code == Code.Sub_Ovf || code == Code.Sub_Ovf_Un ||
                   code == Code.Mul || code == Code.Mul_Ovf || code == Code.Mul_Ovf_Un ||
                   code == Code.Div || code == Code.Div_Un ||
                   code == Code.Rem || code == Code.Rem_Un ||
                   code == Code.And || code == Code.Or || code == Code.Xor ||
                   code == Code.Shl || code == Code.Shr || code == Code.Shr_Un;
        }

        private object? ExecuteArithmetic(Code op, object? v1, object? v2)
        {
            // Приводим к double для точности вычислений с плавающей точкой
            double d1 = Convert.ToDouble(v1 ?? 0);
            double d2 = Convert.ToDouble(v2 ?? 0);

            double res = op.Code switch
            {
                Code.Add => d1 + d2,
                Code.Sub => d1 - d2,
                Code.Mul => d1 * d2,
                Code.Div => d1 / d2,
                Code.Rem => d1 % d2,
                Code.And => (double)(Convert.ToInt64(d1) & Convert.ToInt64(d2)),
                Code.Or => (double)(Convert.ToInt64(d1) | Convert.ToInt64(d2)),
                Code.Xor => (double)(Convert.ToInt64(d1) ^ Convert.ToInt64(d2)),
                Code.Shl => (double)(Convert.ToInt64(d1) << (int)d2),
                Code.Shr => (double)(Convert.ToInt64(d1) >> (int)d2),
                _ => throw new NotSupportedException()
            };

            // Если исходные были целыми и операция целочисленная (битовая или сдвиг), возвращаем long/int
            if (op == Code.And || op == Code.Or || op == Code.Xor || op == Code.Shl || op == Code.Shr)
                return Convert.ToInt64(res);
            
            return res;
        }

        private object? ExecuteMethod(IMethodDefOrRef methodRef, List<object?> args)
        {
            string name = methodRef.Name;
            string type = methodRef.DeclaringType?.FullName ?? "";

            double GetDouble(int idx) => Convert.ToDouble(args[idx] ?? 0);
            float GetSingle(int idx) => Convert.ToSingle(args[idx] ?? 0);
            int GetInt32(int idx) => Convert.ToInt32(args[idx] ?? 0);
            long GetInt64(int idx) => Convert.ToInt64(args[idx] ?? 0);

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
                    case "Round": return args.Count == 2 ? Math.Round(GetDouble(0), GetInt32(1)) : Math.Round(GetDouble(0));
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

            throw new NotSupportedException();
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
                case Code.Ldc_I4_0: value = 0; return true;
                case Code.Ldc_I4_1: value = 1; return true;
                case Code.Ldc_I4_2: value = 2; return true;
                case Code.Ldc_I4_3: value = 3; return true;
                case Code.Ldc_I4_4: value = 4; return true;
                case Code.Ldc_I4_5: value = 5; return true;
                case Code.Ldc_I4_6: value = 6; return true;
                case Code.Ldc_I4_7: value = 7; return true;
                case Code.Ldc_I4_8: value = 8; return true;
                case Code.Ldc_I4_M1: value = -1; return true;
                default:
                    return false;
            }
        }

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

        private bool IsInstructionTarget(IList<Instruction> instrs, Instruction target)
        {
            foreach (var ins in instrs)
            {
                // Проверка прямого операнда
                if (ins.Operand == target) return true;
                
                // Проверка массива операндов (switch, try-catch blocks)
                if (ins.Operand is Instruction[] arr)
                {
                    if (arr.Contains(target)) return true;
                }
                
                // Проверка Exception Handlers
                if (ins.Operand is ExceptionHandler eh)
                {
                    if (eh.TryStart == target || eh.TryEnd == target || 
                        eh.HandlerStart == target || eh.HandlerEnd == target ||
                        eh.FilterStart == target) return true;
                }
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
                        if (!IsInstructionTarget(body.Instructions, body.Instructions[i]))
                        {
                            body.Instructions.RemoveAt(i);
                            changed = true;
                        }
                    }
                }
            }
            
            // Обновление оффсетов и пересчет ветвлений если нужно (dnlib обычно делает это сам при сохранении, но полезно для корректности в памяти)
            body.UpdateInstructionOffsets();
        }
    }
}
