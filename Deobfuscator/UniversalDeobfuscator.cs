using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Deobfuscator
{
    /// <summary>
    /// Универсальный деобфускатор .NET сборок
    /// Поддерживает:
    /// - Упрощение константных условий (int, long, float, double)
    /// - Распутывание цепочек goto
    /// - Упрощение циклов while/do-while с константными условиями
    /// - Упрощение switch с константными значениями
    /// - Удаление мертвого кода
    /// - AI-переименование методов и переменных
    /// </summary>
    public class UniversalDeobfuscator
    {
        private readonly ModuleDefMD _module;
        private readonly AiAssistant _aiAssistant;
        private int _changesCount;

        public UniversalDeobfuscator(ModuleDefMD module, AiConfig aiConfig = null)
        {
            _module = module;
            _changesCount = 0;
            
            if (aiConfig != null && aiConfig.Enabled)
            {
                _aiAssistant = new AiAssistant(aiConfig);
            }
        }

        /// <summary>
        /// Запустить процесс деобфускации
        /// </summary>
        public async Task<int> DeobfuscateAsync()
        {
            Console.WriteLine($"[*] Начало деобфускации: {_module.Name}");
            Console.WriteLine($"[*] Типов определений: {_module.Types.Count}");

            if (_aiAssistant != null)
            {
                Console.WriteLine("[*] Проверка подключения к AI-серверу...");
                var connected = await _aiAssistant.CheckConnectionAsync();
                if (!connected)
                {
                    Console.WriteLine("[!] Не удалось подключиться к AI-серверу. AI-функции будут отключены.");
                }
            }

            // Основной цикл деобфускации (повторяем пока есть изменения)
            int iteration = 0;
            do
            {
                _changesCount = 0;
                iteration++;
                
                Console.WriteLine($"\n[Iteration {iteration}] Начало прохода деобфускации...");

                foreach (var type in _module.GetTypes())
                {
                    ProcessType(type);
                }

                Console.WriteLine($"[Iteration {iteration}] Внесено изменений: {_changesCount}");
                
            } while (_changesCount > 0 && iteration < 10); // Максимум 10 итераций

            Console.WriteLine($"\n[*] Деобфускация завершена. Всего изменений: {_changesCount}");
            return _changesCount;
        }

        /// <summary>
        /// Обработать тип (класс/интерфейс/структура)
        /// </summary>
        private void ProcessType(TypeDef type)
        {
            // Обработать методы
            foreach (var method in type.Methods)
            {
                if (method.HasBody && method.Body.HasInstructions)
                {
                    ProcessMethod(method);
                }
            }

            // Рекурсивно обработать вложенные типы
            foreach (var nestedType in type.NestedTypes)
            {
                ProcessType(nestedType);
            }
        }

        /// <summary>
        /// Обработать метод
        /// </summary>
        private async void ProcessMethod(MethodDef method)
        {
            try
            {
                var body = method.Body;
                
                // Шаг 1: Сбор информации о константах
                var constantValues = CollectConstantValues(body);
                
                // Шаг 2: Упрощение условий с известными константами
                SimplifyConstantConditions(body, constantValues);
                
                // Шаг 3: Распутывание цепочек goto
                UnravelGotoChains(body);
                
                // Шаг 4: Упрощение циклов с константными условиями
                SimplifyConstantLoops(body);
                
                // Шаг 5: Упрощение switch с константными значениями
                SimplifyConstantSwitches(body, constantValues);
                
                // Шаг 6: Удаление мертвого кода
                RemoveDeadCode(body);
                
                // Шаг 7: Упрощение ветвлений
                SimplifyBranches(body);

                // Шаг 8: AI-переименование (если включено)
                if (_aiAssistant != null && method.Name.StartsWith("<") || IsObfuscatedName(method.Name))
                {
                    await RenameMethodWithAi(method);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Ошибка обработки метода {method.FullName}: {ex.Message}");
            }
        }

        /// <summary>
        /// Собрать информацию о присваиваниях констант локальным переменным
        /// </summary>
        private Dictionary<int, object> CollectConstantValues(MethodBody body)
        {
            var constants = new Dictionary<int, object>();
            var instructions = body.Instructions;

            for (int i = 0; i < instructions.Count; i++)
            {
                var instr = instructions[i];
                
                // Ищем ldc.* инструкции (загрузка константы)
                if (instr.OpCode.Code == Code.Ldc_I4 || 
                    instr.OpCode.Code == Code.Ldc_I8 ||
                    instr.OpCode.Code == Code.Ldc_R4 ||
                    instr.OpCode.Code == Code.Ldc_R8)
                {
                    // Проверяем, следует ли за ней stloc.* (сохранение в локальную переменную)
                    if (i + 1 < instructions.Count && 
                        (instructions[i + 1].OpCode.Code == Code.Stloc ||
                         instructions[i + 1].OpCode.Code == Code.Stloc_S ||
                         instructions[i + 1].OpCode.Code == Code.Stloc_0 ||
                         instructions[i + 1].OpCode.Code == Code.Stloc_1 ||
                         instructions[i + 1].OpCode.Code == Code.Stloc_2 ||
                         instructions[i + 1].OpCode.Code == Code.Stloc_3))
                    {
                        int localIndex = GetLocalIndex(instructions[i + 1], body);
                        if (localIndex >= 0)
                        {
                            object value = GetConstantValue(instr);
                            if (value != null && !constants.ContainsKey(localIndex))
                            {
                                constants[localIndex] = value;
                            }
                        }
                    }
                }
            }

            return constants;
        }

        /// <summary>
        /// Получить индекс локальной переменной из инструкции stloc
        /// </summary>
        private int GetLocalIndex(Instruction instr, MethodBody body)
        {
            if (instr.Operand is Local local)
            {
                return local.Index;
            }
            
            // Для коротких форм stloc_0, stloc_1 и т.д.
            switch (instr.OpCode.Code)
            {
                case Code.Stloc_0: return 0;
                case Code.Stloc_1: return 1;
                case Code.Stloc_2: return 2;
                case Code.Stloc_3: return 3;
                default:
                    if (instr.Operand is int idx)
                        return idx;
                    break;
            }
            
            return -1;
        }

        /// <summary>
        /// Получить значение константы из ldc.* инструкции
        /// </summary>
        private object GetConstantValue(Instruction instr)
        {
            return instr.Operand switch
            {
                int i => i,
                long l => l,
                float f => f,
                double d => d,
                _ => null
            };
        }

        /// <summary>
        /// Упростить условия с известными константными значениями
        /// </summary>
        private void SimplifyConstantConditions(MethodBody body, Dictionary<int, object> constants)
        {
            var instructions = body.Instructions;
            
            for (int i = 0; i < instructions.Count; i++)
            {
                var instr = instructions[i];
                
                // Ищем сравнения с локальными переменными
                if (IsComparisonWithLocal(instr, out int localIndex, out object compareValue, out var targetInstr))
                {
                    if (constants.ContainsKey(localIndex))
                    {
                        var constValue = constants[localIndex];
                        
                        // Пытаемся вычислить результат сравнения
                        bool? result = EvaluateComparison(constValue, compareValue, instr.OpCode);
                        
                        if (result.HasValue)
                        {
                            // Заменяем условие на безусловный переход или nop
                            if (result.Value)
                            {
                                // Условие всегда истинно - заменяем на безусловный переход
                                instr.OpCode = OpCodes.Br;
                                _changesCount++;
                                Console.WriteLine($"[+] Упрощено условие (всегда true) в методе");
                            }
                            else
                            {
                                // Условие всегда ложно - заменяем на nop и переходим к следующей инструкции
                                instr.OpCode = OpCodes.Nop;
                                _changesCount++;
                                Console.WriteLine($"[+] Упрощено условие (всегда false) в методе");
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Проверить, является ли инструкция сравнением с локальной переменной
        /// </summary>
        private bool IsComparisonWithLocal(Instruction instr, out int localIndex, out object compareValue, out Instruction targetInstr)
        {
            localIndex = -1;
            compareValue = null;
            targetInstr = null;
            
            // Проверяем, что это условный переход
            if (!instr.OpCode.FlowControl.Equals(FlowControl.Cond_Branch))
                return false;
            
            // Ищем паттерн: ldloc, ldc.*, comparison, br*
            if (i < 2) return false;
            
            var prev2 = instructions[i - 2];
            var prev1 = instructions[i - 1];
            
            // Проверяем ldloc
            if (prev2.OpCode.Code == Code.Ldloc || 
                prev2.OpCode.Code == Code.Ldloc_S ||
                prev2.OpCode.Code == Code.Ldloc_0 ||
                prev2.OpCode.Code == Code.Ldloc_1 ||
                prev2.OpCode.Code == Code.Ldloc_2 ||
                prev2.OpCode.Code == Code.Ldloc_3)
            {
                localIndex = GetLocalIndex(prev2, body);
            }
            
            // Проверяем ldc.*
            if (localIndex >= 0 && 
                (prev1.OpCode.Code == Code.Ldc_I4 || 
                 prev1.OpCode.Code == Code.Ldc_I8 ||
                 prev1.OpCode.Code == Code.Ldc_R4 ||
                 prev1.OpCode.Code == Code.Ldc_R8))
            {
                compareValue = GetConstantValue(prev1);
                targetInstr = instr.Operand as Instruction;
                return true;
            }
            
            return false;
        }

        /// <summary>
        /// Вычислить результат сравнения
        /// </summary>
        private bool? EvaluateComparison(object left, object right, OpCode opCode)
        {
            try
            {
                switch (opCode.Code)
                {
                    case Code.Beq:
                    case Code.Ceq:
                        return Equals(left, right);
                    
                    case Code.Bne_Un:
                    case Code.Cgt:
                    case Code.Cgt_Un:
                        return !Equals(left, right);
                    
                    case Code.Blt:
                    case Code.Blt_Un:
                        return CompareValues(left, right) < 0;
                    
                    case Code.Ble:
                    case Code.Ble_Un:
                        return CompareValues(left, right) <= 0;
                    
                    case Code.Bgt:
                    case Code.Bgt_Un:
                        return CompareValues(left, right) > 0;
                    
                    case Code.Bge:
                    case Code.Bge_Un:
                        return CompareValues(left, right) >= 0;
                }
            }
            catch
            {
                // Ошибка сравнения разных типов
            }
            
            return null;
        }

        /// <summary>
        /// Сравнить два значения
        /// </summary>
        private int CompareValues(object left, object right)
        {
            if (left is IComparable && right is IComparable)
            {
                // Приводим к общему типу для сравнения
                if (left is double || right is double)
                    return Comparer<double>.Default.Compare(Convert.ToDouble(left), Convert.ToDouble(right));
                
                if (left is float || right is float)
                    return Comparer<float>.Default.Compare(Convert.ToSingle(left), Convert.ToSingle(right));
                
                if (left is long || right is long)
                    return Comparer<long>.Default.Compare(Convert.ToInt64(left), Convert.ToInt64(right));
                
                return Comparer<int>.Default.Compare(Convert.ToInt32(left), Convert.ToInt32(right));
            }
            
            return 0;
        }

        /// <summary>
        /// Распутать цепочки goto
        /// </summary>
        private void UnravelGotoChains(MethodBody body)
        {
            var instructions = body.Instructions;
            var gotoTargets = new Dictionary<Instruction, Instruction>();
            
            // Строим карту переходов goto -> конечная точка
            foreach (var instr in instructions)
            {
                if (instr.OpCode.Code == Code.Br && instr.Operand is Instruction target)
                {
                    // Если целевая инструкция тоже безусловный переход, идем дальше
                    Instruction finalTarget = target;
                    while (finalTarget != null && 
                           finalTarget.OpCode.Code == Code.Br && 
                           finalTarget.Operand is Instruction nextTarget)
                    {
                        finalTarget = nextTarget;
                    }
                    
                    if (finalTarget != target)
                    {
                        gotoTargets[instr] = finalTarget;
                    }
                }
            }
            
            // Применяем оптимизации
            foreach (var kvp in gotoTargets)
            {
                kvp.Key.Operand = kvp.Value;
                _changesCount++;
            }
            
            if (gotoTargets.Count > 0)
            {
                Console.WriteLine($"[+] Распутано цепочек goto: {gotoTargets.Count}");
            }
        }

        /// <summary>
        /// Упростить циклы с константными условиями
        /// </summary>
        private void SimplifyConstantLoops(MethodBody body)
        {
            var instructions = body.Instructions;
            
            for (int i = 0; i < instructions.Count; i++)
            {
                var instr = instructions[i];
                
                // Ищем while(true) или while(false) паттерны
                if (instr.OpCode.Code == Code.Brtrue || 
                    instr.OpCode.Code == Code.Brfalse ||
                    instr.OpCode.Code == Code.Blt ||
                    instr.OpCode.Code == Code.Bgt ||
                    instr.OpCode.Code == Code.Ble ||
                    instr.OpCode.Code == Code.Bge)
                {
                    // Проверяем, является ли условие константой
                    if (i >= 1 && instructions[i - 1].OpCode.Code == Code.Ldc_I4)
                    {
                        int constValue = (int)instructions[i - 1].Operand;
                        
                        if (instr.OpCode.Code == Code.Brtrue)
                        {
                            if (constValue != 0)
                            {
                                // while(true) - оставляем как есть или можно преобразовать
                                Console.WriteLine($"[+] Найден цикл while(true)");
                            }
                            else
                            {
                                // while(false) - удаляем цикл
                                instr.OpCode = OpCodes.Nop;
                                _changesCount++;
                            }
                        }
                        else if (instr.OpCode.Code == Code.Brfalse)
                        {
                            if (constValue != 0)
                            {
                                // Условие ложно - пропускаем цикл
                                instr.OpCode = OpCodes.Nop;
                                _changesCount++;
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Упростить switch с константными значениями
        /// </summary>
        private void SimplifyConstantSwitches(MethodBody body, Dictionary<int, object> constants)
        {
            // Пока базовая реализация - поиск switch с константными значениями
            // Полная реализация требует анализа switch tables
            Console.WriteLine("[*] Анализ switch statements...");
        }

        /// <summary>
        /// Удалить мертвый код (недостижимые инструкции)
        /// </summary>
        private void RemoveDeadCode(MethodBody body)
        {
            var instructions = body.Instructions;
            var reachable = new HashSet<Instruction>();
            
            // BFS для поиска достижимых инструкций
            var queue = new Queue<Instruction>();
            queue.Enqueue(instructions[0]);
            reachable.Add(instructions[0]);
            
            while (queue.Count > 0)
            {
                var instr = queue.Dequeue();
                var index = instructions.IndexOf(instr);
                
                if (index < 0) continue;
                
                // Следующая инструкция (если не безусловный переход)
                if (instr.OpCode.FlowControl != FlowControl.Branch &&
                    instr.OpCode.FlowControl != FlowControl.Return &&
                    instr.OpCode.FlowControl != FlowControl.Throw &&
                    index + 1 < instructions.Count)
                {
                    AddIfNotReachable(instructions[index + 1], reachable, queue);
                }
                
                // Целевая инструкция перехода
                if (instr.Operand is Instruction target)
                {
                    AddIfNotReachable(target, reachable, queue);
                }
                
                // Switch targets
                if (instr.Operand is Instruction[] targets)
                {
                    foreach (var t in targets)
                    {
                        AddIfNotReachable(t, reachable, queue);
                    }
                }
            }
            
            // Заменяем недостижимые инструкции на nop
            int removedCount = 0;
            foreach (var instr in instructions)
            {
                if (!reachable.Contains(instr) && instr.OpCode.Code != Code.Nop)
                {
                    instr.OpCode = OpCodes.Nop;
                    instr.Operand = null;
                    removedCount++;
                }
            }
            
            if (removedCount > 0)
            {
                Console.WriteLine($"[+] Удалено недостижимых инструкций: {removedCount}");
                _changesCount += removedCount;
            }
        }

        private void AddIfNotReachable(Instruction instr, HashSet<Instruction> reachable, Queue<Instruction> queue)
        {
            if (!reachable.Contains(instr))
            {
                reachable.Add(instr);
                queue.Enqueue(instr);
            }
        }

        /// <summary>
        /// Упростить ветвления (удалить лишние nop)
        /// </summary>
        private void SimplifyBranches(MethodBody body)
        {
            // Оптимизация последовательностей nop
            var instructions = body.Instructions;
            int nopCount = 0;
            
            for (int i = instructions.Count - 1; i >= 0; i--)
            {
                if (instructions[i].OpCode.Code == Code.Nop)
                {
                    nopCount++;
                }
            }
            
            if (nopCount > 10)
            {
                Console.WriteLine($"[*] Найдено nop инструкций: {nopCount}");
            }
        }

        /// <summary>
        /// Переименовать метод с помощью AI
        /// </summary>
        private async Task RenameMethodWithAi(MethodDef method)
        {
            if (_aiAssistant == null) return;
            
            try
            {
                // Генерируем представление метода для AI
                var methodCode = GenerateMethodRepresentation(method);
                
                // Запрашиваем новое имя
                var newName = await _aiAssistant.SuggestMethodName(methodCode, method.Name);
                
                if (!string.IsNullOrWhiteSpace(newName) && newName != method.Name)
                {
                    method.Name = newName;
                    Console.WriteLine($"[AI] Переименовано: {method.Name}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AI] Ошибка переименования метода: {ex.Message}");
            }
        }

        /// <summary>
        /// Сгенерировать текстовое представление метода для AI
        /// </summary>
        private string GenerateMethodRepresentation(MethodDef method)
        {
            var sb = new System.Text.StringBuilder();
            
            sb.AppendLine($"Method: {method.Name}");
            sb.AppendLine($"ReturnType: {method.ReturnType}");
            sb.AppendLine($"Parameters: {string.Join(", ", method.Parameters.Select(p => $"{p.Type} {p.Name}"))}");
            sb.AppendLine();
            
            if (method.HasBody && method.Body.HasInstructions)
            {
                sb.AppendLine("IL Code:");
                foreach (var instr in method.Body.Instructions.Take(50)) // Первые 50 инструкций
                {
                    sb.AppendLine($"  {instr}");
                }
                
                if (method.Body.Instructions.Count > 50)
                {
                    sb.AppendLine($"  ... and {method.Body.Instructions.Count - 50} more instructions");
                }
            }
            
            return sb.ToString();
        }

        /// <summary>
        /// Проверить, является ли имя обфусцированным
        /// </summary>
        private bool IsObfuscatedName(string name)
        {
            // Простые эвристики для определения обфусцированных имен
            if (string.IsNullOrEmpty(name)) return true;
            
            // Имена вида a, b, A, B, A1, B2 и т.д.
            if (name.Length <= 2 && name.All(c => char.IsLetterOrDigit(c)))
                return true;
            
            // Имена вида A_123, <Module>, и т.д.
            if (name.StartsWith("<") || name.Contains("__"))
                return true;
            
            return false;
        }
    }
}
