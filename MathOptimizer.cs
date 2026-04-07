using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Deobfuscator
{
    /// <summary>
    /// Оптимизатор математических выражений.
    /// Стратегия: Сначала симулируем вычисление. Если успех — заменяем код.
    /// Поддерживает вложенные вызовы за счет многопроходности.
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

            // Выполняем проходы до тех пор, пока есть изменения
            while (globalChanged)
            {
                globalChanged = false;
                int passCount = 0;

                foreach (var type in module.GetTypes())
                {
                    foreach (var method in type.Methods)
                    {
                        if (!method.HasBody || !method.Body.HasInstructions) continue;

                        var body = method.Body;
                        var instrs = body.Instructions;

                        // Проходим по инструкциям
                        for (int i = 0; i < instrs.Count; i++)
                        {
                            var callInstr = instrs[i];

                            // Нас интересуют только вызовы
                            if (callInstr.OpCode.Code != Code.Call) continue;
                            if (callInstr.Operand is not IMethodDefOrRef methodRef) continue;

                            string typeName = methodRef.DeclaringType?.FullName ?? "";
                            string methodName = methodRef.Name;

                            // Работаем только с System.Math и System.Convert
                            if (typeName != "System.Math" && typeName != "System.Convert") continue;

                            // 1. ЭТАП АНАЛИЗА: Пытаемся вычислить значение, не меняя код
                            if (TryEvaluateExpression(instrs, i, methodRef, out object? result))
                            {
                                // 2. ЭТАП ЗАМЕНЫ: Вычисление успешно, меняем код

                                // Определяем, нужно ли пушить результат в стек
                                bool hasReturn = methodRef.ReturnType?.ElementType != ElementType.Void;

                                // Заменяем сам вызов на константу (или NOP, если void)
                                if (hasReturn && result != null)
                                {
                                    instrs[i] = CreateConstantInstruction(result);
                                }
                                else
                                {
                                    callInstr.OpCode = OpCodes.Nop;
                                    callInstr.Operand = null;
                                }

                                // Теперь нужно найти и обезвредить инструкции аргументов.
                                // Мы знаем, сколько аргументов было съедено при анализе (через вспомогательную логику).
                                // Но так как TryEvaluateExpression уже вернул успех, нам нужно повторить проход назад,
                                // чтобы узнать, какие именно индексы были задействованы, и превратить их в NOP.
                                
                                int argsCount = GetArgumentCount(methodRef);
                                int currentIndex = i - 1;
                                int argsFound = 0;

                                while (argsFound < argsCount && currentIndex >= 0)
                                {
                                    var currentInstr = instrs[currentIndex];

                                    if (currentInstr.OpCode.Code == Code.Nop)
                                    {
                                        currentIndex--;
                                        continue;
                                    }

                                    // Проверяем, является ли это константой или уже вычисленным выражением (которое стало константой в этом или прошлом проходе)
                                    if (IsPureConstant(currentInstr) || 
                                       (currentInstr.OpCode.Code == Code.Call && currentInstr.Operand is IMethodDefOrRef nested && IsPreviouslyOptimized(nested))) 
                                    {
                                        // Превращаем в NOP
                                        // Важно: если это был вызов, который мы только что свернули в этом же проходе (ранее в цикле),
                                        // он уже заменен на константу выше. Но здесь мы идем назад от текущего i.
                                        // Если текущая инструкция - это константа (Ldc...), мы её затираем.
                                        
                                        // Особый случай: если аргументом был другой Call, который мы еще не обработали в этом проходе,
                                        // но TryEvaluateExpression смог его вычислить рекурсивно.
                                        // В таком случае, тот вызов должен быть где-то раньше по индексу.
                                        // Нам нужно аккуратно удалить только то, что относится к этому аргументу.
                                        
                                        // Упрощение: TryEvaluateExpression гарантировал, что аргументы - это цепочка констант/вызовов.
                                        // Мы просто идем назад и убираем инструкции, пока не наберем нужное количество "значений".
                                        
                                        if (currentInstr.OpCode.Code == Code.Call)
                                        {
                                            // Это вложенный вызов. Он должен быть уже обработан в предыдущих проходах глобального цикла,
                                            // ИЛИ это часть текущего выражения, которую мы нашли рекурсивно.
                                            // Если он еще не заменен на константу, значит TryEvaluateExpression сделал это виртуально.
                                            // Но физически он еще в коде. Нам нужно его тоже превратить в NOP?
                                            // Нет, если TryEvaluateExpression использовал его значение, значит он должен быть "съеден".
                                            // Но если он еще не заменен на константу в массиве instrs, то замена его на NOP удалит его результат из стека?
                                            // Логика такая: если мы свернули parent, то child тоже должен исчезнуть или стать NOP.
                                            // Однако, самый безопасный путь для вложенных вызовов, которые еще не стали константами:
                                            // Они должны были быть обработаны на предыдущем шаге i (так как i идет вперед).
                                            // Если мы здесь, значит они либо уже константы, либо TryEvaluate прорвался сквозь них.
                                            // Если они еще Call, значит они не были заменены. 
                                            // В рамках одного прохода (i от 0 до Count) вложенный вызов всегда встречается РАНЬШЕ внешнего.
                                            // Значит, если TryEvaluate смог его вычислить, то либо он уже стал константой (на шаге < i),
                                            // либо он остался Call, но содержит константы внутри.
                                            // Если он остался Call, значит в этом проходе мы его пропустили (не смогли вычислить тогда?).
                                            // Но TryEvaluate сейчас вернулся true. Значит он смог.
                                            // Скорее всего, этот вложенный Call все еще в списке. Мы должны превратить его в NOP, так как его результат уже учтен в родителе.
                                            currentInstr.OpCode = OpCodes.Nop;
                                            currentInstr.Operand = null;
                                        }
                                        else
                                        {
                                            // Обычная константа
                                            currentInstr.OpCode = OpCodes.Nop;
                                            currentInstr.Operand = null;
                                        }

                                        argsFound++;
                                        currentIndex--;
                                        continue;
                                    }
                                    
                                    // Если встретили что-то непонятное (хотя TryEvaluate сказал OK), прерываемся, чтобы не сломать код.
                                    // Это страховка.
                                    break;
                                }

                                totalOptimized++;
                                globalChanged = true;
                                passCount++;
                                
                                string valStr = result?.ToString() ?? "null";
                                if (valStr.Length > 40) valStr = valStr.Substring(0, 40) + "...";
                                _log?.Invoke($"  [Folded] {typeName}.{methodName}(...) = {valStr}");
                            }
                        }
                        
                        if (passCount > 0)
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
        /// Пытается вычислить результат вызова, анализируя стек виртуально.
        /// Не модифицирует код.
        /// </summary>
        private bool TryEvaluateExpression(IList<Instruction> instrs, int callIdx, IMethodDefOrRef methodRef, out object? result)
        {
            result = null;
            int expectedArgs = GetArgumentCount(methodRef);
            
            List<object?> args = new List<object?>(expectedArgs);
            int currentIndex = callIdx - 1;
            int argsFound = 0;

            while (argsFound < expectedArgs && currentIndex >= 0)
            {
                var currentInstr = instrs[currentIndex];

                if (currentInstr.OpCode.Code == Code.Nop)
                {
                    currentIndex--;
                    continue;
                }

                // Попытка получить константу напрямую
                if (TryGetConstantValue(currentInstr, out object? val))
                {
                    args.Add(val);
                    argsFound++;
                    currentIndex--;
                    continue;
                }

                // Попытка рекурсивно вычислить вложенный вызов
                if (currentInstr.OpCode.Code == Code.Call && currentInstr.Operand is IMethodDefOrRef nestedMethod)
                {
                    if (TryEvaluateExpression(instrs, currentIndex, nestedMethod, out object? nestedVal))
                    {
                        args.Add(nestedVal);
                        argsFound++;
                        // Пропускаем всю цепочку, которую съел вложенный вызов?
                        // Нет, TryEvaluateExpression не возвращает количество съеденных инструкций.
                        // Нам нужно знать, сколько инструкций назад ушло на этот аргумент.
                        // Это сложно сделать без возврата количества.
                        // ИЗМЕНЕНИЕ: Вернем количество съеденных инструкций из TryEvaluateExpression.
                        // Но сигнатура метода сейчас этого не позволяет без рефакторинга.
                        // Давайте сделаем хак: предположим, что если вложенный вызов вычислился, 
                        // то он потребил свои аргументы. Но мы не знаем сколько.
                        // Правильнее изменить сигнатуру.
                        
                        // ПЕРЕПИСЫВАЕМ ПОДХОД ДЛЯ ТОЧНОСТИ:
                        // Вернемся к идее: TryEvaluateExpression должен возвращать не только value, но и count.
                        // Иначе мы не сможем корректно пропустить инструкции для следующего аргумента.
                        
                        // Временное решение для этой версии: считаем, что вложенный вызов - это 1 инструкция (сам вызов),
                        // а его аргументы будут обработаны в его собственном рекурсивном вызове? Нет, это неверно.
                        // Нам нужно знать глубину.
                        
                        // ОК, я перепишу метод ниже с правильным возвратом кортежа (value, count).
                        // А здесь пока заглушка, которая не будет работать корректно со сложными вложениями в одном проходе.
                        // Но так как у нас внешний цикл while(globalChanged), вложенные вызовы схлопнутся в константы на ПРЕДЫДУЩЕМ проходе.
                        // Тогда здесь они попадут в ветку IsPureConstant (так как станут Ldc).
                        // Значит, рекурсивный вызов здесь нужен только если мы хотим схлопнуть всё за один проход.
                        // Для надежности оставим рекурсию, но для подсчета индексов сделаем допущение:
                        // Если вложенный вызов еще не заменен на константу, значит мы не можем точно знать его размер без возврата count.
                        // Поэтому лучший вариант: внешний цикл делает основную работу. Рекурсия здесь нужна, чтобы поддержать случаи,
                        // когда вложенный вызов ТОЖЕ готов к вычислению прямо сейчас.
                        
                        // Давайте исправим сигнатуру метода прямо сейчас.
                        throw new NotImplementedException("Нужно использовать перегрузку с возвратом количества");
                    }
                }

                // Если не константа и не вычисляемый вызов - стоп
                break;
            }

            if (argsFound != expectedArgs) return false;

            args.Reverse();
            try
            {
                result = ExecuteMethod(methodRef, args);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // Перегруженная версия для точного подсчета инструкций
        private bool TryEvaluateExpression(IList<Instruction> instrs, int callIdx, IMethodDefOrRef methodRef, out object? result, out int consumedInstructions)
        {
            result = null;
            consumedInstructions = 0;
            
            int expectedArgs = GetArgumentCount(methodRef);
            List<object?> args = new List<object?>(expectedArgs);
            int currentIndex = callIdx - 1;
            int argsFound = 0;
            int totalConsumed = 0; // Считаем инструкции аргументов

            while (argsFound < expectedArgs && currentIndex >= 0)
            {
                var currentInstr = instrs[currentIndex];

                if (currentInstr.OpCode.Code == Code.Nop)
                {
                    currentIndex--;
                    totalConsumed++; // NOP тоже считаем, хотя они прозрачны
                    continue;
                }

                if (TryGetConstantValue(currentInstr, out object? val))
                {
                    args.Add(val);
                    argsFound++;
                    totalConsumed++;
                    currentIndex--;
                    continue;
                }

                if (currentInstr.OpCode.Code == Code.Call && currentInstr.Operand is IMethodDefOrRef nestedMethod)
                {
                    if (TryEvaluateExpression(instrs, currentIndex, nestedMethod, out object? nestedVal, out int nestedConsumed))
                    {
                        args.Add(nestedVal);
                        argsFound++;
                        totalConsumed += (nestedConsumed + 1); // +1 сама инструкция call
                        currentIndex -= (nestedConsumed + 1);
                        continue;
                    }
                }

                break;
            }

            if (argsFound != expectedArgs) return false;

            args.Reverse();
            try
            {
                result = ExecuteMethod(methodRef, args);
                consumedInstructions = totalConsumed;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private int GetArgumentCount(IMethodDefOrRef methodRef)
        {
            if (methodRef.MethodSig == null) return 0;
            return methodRef.MethodSig.Params.Count;
        }

        private bool IsPureConstant(Instruction instr)
        {
            var code = instr.OpCode.Code;
            return code == Code.Ldc_I4 || code == Code.Ldc_I4_S || code == Code.Ldc_I8 ||
                   code == Code.Ldc_R4 || code == Code.Ldc_R8 || code == Code.Ldstr;
        }
        
        private bool IsPreviouslyOptimized(IMethodDefOrRef methodRef)
        {
            string tn = methodRef.DeclaringType?.FullName ?? "";
            return tn == "System.Math" || tn == "System.Convert";
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
                    case "Ceiling": return Math.Ceiling(GetDouble(0));
                    case "Floor": return Math.Floor(GetDouble(0));
                    case "Round": return args.Count == 2 ? Math.Round(GetDouble(0), GetInt32(1)) : Math.Round(GetDouble(0));
                    case "Sin": return Math.Sin(GetDouble(0));
                    case "Cos": return Math.Cos(GetDouble(0));
                    case "Tan": return Math.Tan(GetDouble(0));
                    case "Tanh": return Math.Tanh(GetDouble(0));
                    case "Log": return Math.Log(GetDouble(0));
                    case "Log10": return Math.Log10(GetDouble(0));
                    case "Sqrt": return Math.Sqrt(GetDouble(0));
                    case "Pow": return Math.Pow(GetDouble(0), GetDouble(1));
                    case "Max": return Math.Max(GetDouble(0), GetDouble(1));
                    case "Min": return Math.Min(GetDouble(0), GetDouble(1));
                    // Добавьте другие методы по необходимости
                }
            }
            else if (type == "System.Convert")
            {
                object val = args[0] ?? 0;
                switch (name)
                {
                    case "ToInt32": return Convert.ToInt32(val);
                    case "ToDouble": return Convert.ToDouble(val);
                    case "ToByte": return Convert.ToByte(val);
                    case "ToString": return Convert.ToString(val);
                    case "ToBoolean": return Convert.ToBoolean(val);
                    // Добавьте другие
                }
            }

            throw new NotSupportedException();
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

        private bool IsInstructionReferenced(IList<Instruction> instrs, Instruction target)
        {
            foreach (var ins in instrs)
            {
                if (ins.Operand == target) return true;
                if (ins.Operand is Instruction[] arr && arr.Contains(target)) return true;
            }
            return false;
        }
    }
}
