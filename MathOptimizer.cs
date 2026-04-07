using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Deobfuscator
{
    /// <summary>
    /// Безопасная оптимизация математических выражений.
    /// Вычисляет значения заранее и заменяет цепочки инструкций на константы.
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

                        // Проходим с конца к началу
                        for (int i = instrs.Count - 1; i >= 0; i--)
                        {
                            var callInstr = instrs[i];

                            // Нас интересуют только вызовы
                            if (callInstr.OpCode.Code != Code.Call) continue;
                            if (callInstr.Operand is not IMethodDefOrRef methodRef) continue;

                            string typeName = methodRef.DeclaringType?.FullName ?? "";
                            string methodName = methodRef.Name;

                            // Работаем только с System.Math и System.Convert
                            if (typeName != "System.Math" && typeName != "System.Convert") continue;

                            // Пытаемся вычислить результат БЕЗ изменения кода
                            if (TrySafeEvaluate(instrs, i, methodRef, out object? result, out List<int> argIndices))
                            {
                                // Если успешно вычислили, применяем изменения
                                
                                // 1. Заменяем сам вызов на константу (или NOP, если void)
                                bool hasReturn = methodRef.ReturnType?.ElementType != ElementType.Void;
                                
                                if (hasReturn)
                                {
                                    var newInstr = CreateConstantInstruction(result);
                                    // Копируем расположение строки от старой инструкции, чтобы отладчик не сбился (опционально)
                                    newInstr.SequencePoint = callInstr.SequencePoint;
                                    instrs[i] = newInstr;
                                }
                                else
                                {
                                    callInstr.OpCode = OpCodes.Nop;
                                    callInstr.Operand = null;
                                }

                                // 2. Превращаем аргументы в NOP
                                // Мы идем в обратном порядке индексов аргументов, чтобы не сбить логику, 
                                // хотя порядок удаления здесь не критичен, так как мы просто затираем их.
                                foreach (int idx in argIndices)
                                {
                                    if (idx >= 0 && idx < instrs.Count)
                                    {
                                        // Двойная проверка: не стала ли инструкция уже NOP (на всякий случай)
                                        if (instrs[idx].OpCode.Code != Code.Nop)
                                        {
                                            instrs[idx].OpCode = OpCodes.Nop;
                                            instrs[idx].Operand = null;
                                        }
                                    }
                                }

                                totalOptimized++;
                                globalChanged = true;
                                
                                string valStr = result?.ToString() ?? "null";
                                if (valStr.Length > 40) valStr = valStr.Substring(0, 40) + "...";
                                _log?.Invoke($"[MathOpt] {typeName}.{methodName}(...) -> {valStr}");
                            }
                        }

                        if (globalChanged)
                        {
                            // Обновляем оффсеты перед следующим проходом или выходом
                            body.UpdateInstructionOffsets();
                        }
                    }
                }

                // Очистка NOP выполняется один раз после завершения всех циклов оптимизации метода,
                // чтобы не усложнять логику индексов внутри цикла.
                // Но так как у нас внешний while(globalChanged), сделаем очистку здесь для каждого прохода.
                if (totalOptimized > 0) 
                {
                     // Пересобираем методы для удаления NOP
                     foreach (var type in module.GetTypes())
                     {
                         foreach (var method in type.Methods)
                         {
                             if (method.HasBody) CleanupNops(method);
                         }
                     }
                }
            }

            return totalOptimized;
        }

        /// <summary>
        /// Пытается безопасно вычислить выражение.
        /// Возвращает true, если успешно, и заполняет result и список индексов аргументов.
        /// </summary>
        private bool TrySafeEvaluate(IList<Instruction> instrs, int callIndex, IMethodDefOrRef methodRef, out object? result, out List<int> argIndices)
        {
            result = null;
            argIndices = new List<int>();

            int expectedArgs = methodRef.MethodSig?.Params.Count ?? 0;
            
            // Стек для сбора аргументов (значения)
            // Нам нужно собрать expectedArgs аргументов.
            // В IL аргументы лежат перед вызовом: Arg1, Arg2, ..., ArgN, Call.
            // ArgN находится сразу перед Call (index - 1).
            
            List<object?> values = new List<object?>(expectedArgs);
            int currentIndex = callIndex - 1;
            int foundCount = 0;

            while (foundCount < expectedArgs && currentIndex >= 0)
            {
                var instr = instrs[currentIndex];

                // Пропускаем NOP, которые могли остаться от предыдущих шагов
                if (instr.OpCode.Code == Code.Nop)
                {
                    currentIndex--;
                    continue;
                }

                // ВАЖНО: Проверка безопасности.
                // Если на эту инструкцию есть ссылка (прыжок сюда), мы НЕ МОЖЕМ её удалять/заменять.
                // Иначе сломается поток управления и dnSpy упадет.
                if (IsInstructionReferenced(instrs, instr))
                {
                    return false; // Небезопасно оптимизировать
                }

                // Попытка получить константу
                if (TryGetConstantValue(instr, out object? val))
                {
                    values.Add(val);
                    argIndices.Add(currentIndex);
                    foundCount++;
                    currentIndex--;
                    continue;
                }

                // Попытка рекурсивного вычисления вложенного вызова
                if (instr.OpCode.Code == Code.Call && instr.Operand is IMethodDefOrRef nestedMethod)
                {
                    if (TrySafeEvaluate(instrs, currentIndex, nestedMethod, out object? nestedVal, out List<int> nestedArgs))
                    {
                        values.Add(nestedVal);
                        argIndices.Add(currentIndex); // Добавляем индекс самого вызова
                        argIndices.AddRange(nestedArgs); // Добавляем индексы его аргументов
                        
                        foundCount++;
                        // Пропускаем инструкции, съеденные вложенным вызовом
                        // Но нам нужно просто уменьшить currentIndex. 
                        // Так как мы идем по одному индексу за шаг, а рекурсия уже собрала свои индексы в nestedArgs,
                        // нам нужно просто продолжить цикл, ноcurrentIndex уже уменьшится на 1 в конце цикла.
                        // Проблема: нам нужно перепрыгнуть через все аргументы вложенного вызова.
                        // nestedArgs содержат индексы аргументов и самого вызова. Максимальный индекс там - currentIndex.
                        // Минимальный индекс - это самый первый аргумент.
                        // Нам нужно установить currentIndex = min(nestedArgs) - 1.
                        
                        if (nestedArgs.Count > 0)
                        {
                            int minIdx = nestedArgs.Min();
                            currentIndex = minIdx - 1;
                        }
                        else
                        {
                            currentIndex--; 
                        }
                        continue;
                    }
                }

                // Если встретили что-то непонятное (переменную, поле) или не смогли вычислить вложенный вызов
                return false;
            }

            if (foundCount != expectedArgs)
            {
                return false; // Не хватило аргументов
            }

            // Аргументы собраны в обратном порядке (последний аргумент добавлен первым в список values)
            values.Reverse();

            try
            {
                result = ExecuteMethod(methodRef, values);
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
                    case "Round":
                        if (args.Count == 2) return Math.Round(GetDouble(0), GetInt32(1));
                        return Math.Round(GetDouble(0));
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
            if (changed) body.UpdateInstructionOffsets();
        }
    }
}
