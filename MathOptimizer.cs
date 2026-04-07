using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Deobfuscator
{
    /// <summary>
    /// Отвечает за оптимизацию математических выражений и сворачивание констант.
    /// Использует рекурсивный анализ стека для обработки вложенных выражений.
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

            // Выполняем проходы до тех пор, пока есть изменения (для обработки цепочек)
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
                        
                        // Проходим с конца, чтобы безопасно удалять/заменять инструкции
                        for (int i = instrs.Count - 1; i >= 0; i--)
                        {
                            var instr = instrs[i];

                            // Нас интересуют только вызовы методов
                            if (instr.OpCode.Code != Code.Call) continue;

                            if (instr.Operand is not IMethodDefOrRef methodRef) continue;

                            string typeName = methodRef.DeclaringType?.FullName ?? "";
                            string methodName = methodRef.Name;

                            // Проверяем, является ли вызов методом Math или Convert
                            bool isMath = typeName == "System.Math";
                            bool isConvert = typeName == "System.Convert";

                            if (!isMath && !isConvert) continue;

                            // Пытаемся вычислить выражение, начиная с этой инструкции
                            if (TryEvaluateExpression(instrs, i, methodRef, out object? result, out int argsCount))
                            {
                                // Успешно вычислили! Теперь нужно заменить код.
                                
                                // 1. Вставляем константу с результатом (если она нужна в стеке)
                                // Если это void метод (редко для Math/Convert, но бывает), результат не пушим
                                bool hasReturn = methodRef.ReturnType?.ElementType != ElementType.Void;

                                Instruction newInstr;
                                if (hasReturn)
                                {
                                    newInstr = CreateConstantInstruction(result);
                                    // Заменяем текущую инструкцию call на константу
                                    instrs[i] = newInstr;
                                }
                                else
                                {
                                    // Если метода нет возврата, просто делаем NOP из call
                                    instrs[i].OpCode = OpCodes.Nop;
                                    instrs[i].Operand = null;
                                }

                                // 2. Удаляем аргументы, которые были до вызова
                                // Аргументы находятся в диапазоне [i - argsCount, i - 1]
                                // Удаляем их в обратном порядке, чтобы не сбить индексы, 
                                // но так как мы идем циклом сверху вниз, а удаляем снизу вверх относительно i,
                                // лучше просто удалить диапазон.
                                
                                int startIndex = i - argsCount;
                                if (startIndex >= 0)
                                {
                                    for (int k = 0; k < argsCount; k++)
                                    {
                                        // Важно: если аргумент был частью другого выражения, которое мы уже обработали,
                                        // он мог стать NOP. Но здесь мы удаляем всё, что было съедено как аргумент.
                                        // Так как TryEvaluateExpression проверил, что это константы/вычисления,
                                        // мы смело удаляем эти инструкции.
                                        if (startIndex < instrs.Count) 
                                        {
                                            var argInstr = instrs[startIndex];
                                            // Не удаляем, если на эту инструкцию есть ссылки (branches), хотя для констант это редкость
                                            if (!IsInstructionReferenced(instrs, argInstr))
                                            {
                                                argInstr.OpCode = OpCodes.Nop;
                                                argInstr.Operand = null;
                                            }
                                        }
                                    }
                                }

                                totalOptimized++;
                                globalChanged = true;
                                
                                string valStr = result?.ToString() ?? "null";
                                if (valStr.Length > 50) valStr = valStr.Substring(0, 50) + "...";
                                _log?.Invoke($"  [MathOpt] Folded {typeName}.{methodName}(...) -> {valStr}");
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
        /// Рекурсивно пытается вычислить значение выражения, заканчивающегося инструкцией callIdx.
        /// Возвращает true, если успешно, и заполняет result и consumedArgsCount.
        /// </summary>
        private bool TryEvaluateExpression(IList<Instruction> instrs, int callIdx, IMethodDefOrRef methodRef, out object? result, out int consumedArgsCount)
        {
            result = null;
            consumedArgsCount = 0;

            // Определяем количество аргументов метода
            int expectedArgs = 0;
            if (methodRef.MethodSig != null)
            {
                expectedArgs = methodRef.MethodSig.Params.Count;
                if (!methodRef.MethodSig.HasThis && !methodRef.DeclaringType.IsStatic) 
                {
                     // Виртуальные вызовы могут иметь скрытый this, но для static Math/Convert его нет.
                     // Для instance методов нужен еще 1 аргумент (this). Но Math и Convert - статические.
                }
            }
            
            // Стек аргументов (в порядке: первый аргумент ... последний аргумент)
            // В IL аргументы пушатся слева направо, последний оказывается на вершине стека.
            // Нам нужно идти назад от callIdx и собирать значения.
            List<object?> args = new List<object?>(expectedArgs);
            int currentIndex = callIdx - 1;
            int argsFound = 0;

            // Собираем аргументы
            while (argsFound < expectedArgs && currentIndex >= 0)
            {
                var currentInstr = instrs[currentIndex];
                
                // Пропускаем NOP и инструкции, которые не влияют на стек аргументов (например, dup, если они не часть логики, но для констант их нет)
                if (currentInstr.OpCode.Code == Code.Nop)
                {
                    currentIndex--;
                    continue;
                }

                // Пытаемся получить константное значение из текущей инструкции
                if (TryGetConstantValue(currentInstr, out object? val))
                {
                    args.Add(val);
                    argsFound++;
                    currentIndex--;
                    continue;
                }

                // Если это не простая константа, проверяем, не является ли это вызовом другого метода (вложенное выражение)
                if (currentInstr.OpCode.Code == Code.Call && currentInstr.Operand is IMethodDefOrRef nestedMethod)
                {
                    // Рекурсивный вызов для вычисления вложенного выражения
                    if (TryEvaluateExpression(instrs, currentIndex, nestedMethod, out object? nestedResult, out int nestedArgsCount))
                    {
                        args.Add(nestedResult);
                        argsFound++;
                        // Пропускаем инструкции, которые были съедены вложенным вызовом
                        currentIndex -= (nestedArgsCount + 1); // +1 сама инструкция call
                        continue;
                    }
                }

                // Если встретили что-то другое (переменную, поле, сложный поток), прерываем - вычислить нельзя
                break;
            }

            if (argsFound != expectedArgs)
            {
                return false; // Не хватило аргументов
            }

            // Аргументы собраны в обратном порядке (последний аргумент был добавлен первым в список при обходе назад)
            // Но мы шли назад от call, значит первый найденный (верх стека) - это последний аргумент метода.
            // Список args сейчас: [LastArg, SecondLastArg, ..., FirstArg]
            // Нужно развернуть для передачи в функцию
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
                // Можно добавить поддержку Ldc_Decimal, если потребуется
                default:
                    return false;
            }
        }

        private object? ExecuteMethod(IMethodDefOrRef methodRef, List<object?> args)
        {
            string name = methodRef.Name;
            string type = methodRef.DeclaringType?.FullName ?? "";

            // Helper to get arg as specific type
            double GetDouble(int idx) => Convert.ToDouble(args[idx] ?? 0);
            float GetSingle(int idx) => Convert.ToSingle(args[idx] ?? 0);
            int GetInt32(int idx) => Convert.ToInt32(args[idx] ?? 0);
            long GetInt64(int idx) => Convert.ToInt64(args[idx] ?? 0);
            string GetString(int idx) => args[idx]?.ToString() ?? "";

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
                    case "Max":
                        if (args[0] is double || args[1] is double) return Math.Max(GetDouble(0), GetDouble(1));
                        return Math.Max(GetInt32(0), GetInt32(1));
                    case "Min":
                        if (args[0] is double || args[1] is double) return Math.Min(GetDouble(0), GetDouble(1));
                        return Math.Min(GetInt32(0), GetInt32(1));
                    case "Pow": return Math.Pow(GetDouble(0), GetDouble(1));
                    case "Round":
                        if (args.Count == 2) return Math.Round(GetDouble(0), GetInt32(1));
                        return Math.Round(GetDouble(0));
                    case "Sign": return Math.Sign(GetDouble(0)); // Упрощенно для double
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
                    case "ToDateTime": return Convert.ToDateTime(val);
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

            throw new NotSupportedException($"Method {type}.{name} not supported for constant folding.");
        }

        private Instruction CreateConstantInstruction(object? value)
        {
            if (value is int i) return OpCodes.Ldc_I4.ToInstruction(i);
            if (value is long l) return OpCodes.Ldc_I8.ToInstruction(l);
            if (value is float f) return OpCodes.Ldc_R4.ToInstruction(f);
            if (value is double d) return OpCodes.Ldc_R8.ToInstruction(d);
            if (value is string s) return OpCodes.Ldstr.ToInstruction(s);
            if (value is bool b) return OpCodes.Ldc_I4.ToInstruction(b ? 1 : 0);
            
            // Fallback
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
            
            // Простое удаление NOP, на которые нет ссылок
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
    }
}
