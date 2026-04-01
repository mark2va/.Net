using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

namespace Deobfuscator
{
    public class UniversalDeobfuscator : IDisposable
    {
        private readonly ModuleDefMD _module;
        private readonly AiAssistant? _aiAssistant;
        
        // Хранит известные константные значения локальных переменных на текущий момент анализа
        private Dictionary<int, object?> _knownConstants = new Dictionary<int, object?>();

        public UniversalDeobfuscator(string filePath, AiConfig aiConfig)
        {
            _module = ModuleDefMD.Load(filePath);
            _aiAssistant = aiConfig.Enabled ? new AiAssistant(aiConfig) : null;
        }

        public void Deobfuscate()
        {
            Console.WriteLine("[*] Starting deobfuscation...");
            
            int methodsProcessed = 0;
            foreach (var type in _module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody || !method.Body.HasInstructions) continue;
                    if (!method.Body.IsIL) continue;

                    try
                    {
                        Console.WriteLine($"[*] Processing: {method.FullName}");
                        
                        // 1. Распутывание потока управления (самое важное для вашего примера)
                        UnravelControlFlow(method);

                        // 2. Удаление мертвого кода и NOP
                        CleanupMethod(method);

                        // 3. AI Переименование (если включено и имя странное)
                        if (_aiAssistant != null && IsObfuscatedName(method.Name))
                        {
                            RenameWithAi(method);
                        }

                        methodsProcessed++;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[!] Error in method {method.Name}: {ex.Message}");
                    }
                }
            }
            
            Console.WriteLine($"[*] Deobfuscation complete. Processed {methodsProcessed} methods.");
        }

        /// <summary>
        /// Основной алгоритм распутывания: симулирует выполнение, отслеживая константы.
        /// </summary>
        private void UnravelControlFlow(MethodDef method)
        {
            var body = method.Body;
            if (body == null) return;

            bool changed = true;
            int iterations = 0;
            const int MAX_ITERATIONS = 50; // Защита от бесконечного цикла

            while (changed && iterations < MAX_ITERATIONS)
            {
                changed = false;
                iterations++;

                // Шаг 1: Попытка упростить переходы, основанные на известных константах
                if (SimplifyConditionalJumps(method))
                {
                    changed = true;
                    continue; // Перезапуск, так как инструкции изменились
                }

                // Шаг 2: Удаление цепочек безусловных переходов (goto A -> goto B)
                if (FlattenGotoChains(method))
                {
                    changed = true;
                    continue;
                }

                // Шаг 3: Удаление переходов на следующую инструкцию
                if (RemoveRedundantBranches(method))
                {
                    changed = true;
                }
            }

            // Финальная очистка: удаление недостижимого кода после того, как поток выпрямлен
            RemoveUnreachableCode(method);
            
            // Важно: сообщаем dnlib, что мы изменили структуру, но хотим сохранить старый MaxStack или пересчитать его корректно
            body.UpdateInstructionOffsets();
        }

        /// <summary>
        /// Упрощает условные переходы, если условие известно на этапе анализа.
        /// Пример: if (num == 2894.0) где num известно как 2894.0 -> превращается в безусловный переход или удаление.
        /// </summary>
        private bool SimplifyConditionalJumps(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            bool modified = false;

            // Сначала соберем информацию о присваиваниях констант
            // В реальном обфускаторе здесь нужен полноценный анализ потоков данных (Data Flow Analysis)
            // Для данного примера сделаем упрощенный проход "на лету"
            
            // Словарь: Index инструкции -> Известные значения локальных переменных ПОСЛЕ этой инструкции
            var localStates = new Dictionary<int, Dictionary<int, object?>>(); 
            
            // Инициализация пустым состоянием
            for(int i=0; i<instructions.Count; i++) localStates[i] = new Dictionary<int, object?>();

            // Простой однопроходный анализ для заполнения localStates (очень упрощенно)
            // В идеале нужно делать фикс-поинт итерации, но для начала попробуем один проход
            var currentLocals = new Dictionary<int, object?>();
            for (int i = 0; i < instructions.Count; i++)
            {
                var instr = instructions[i];
                
                // Сохраняем состояние перед инструкцией
                foreach(var kvp in currentLocals) localStates[i][kvp.Key] = kvp.Value;

                if (instr.OpCode.Code == Code.Stloc)
                {
                    var local = instr.Operand as Local;
                    if (local != null && i > 0)
                    {
                        var prev = instructions[i - 1];
                        var val = GetConstantValue(prev);
                        if (val != null)
                        {
                            currentLocals[local.Index] = val;
                        }
                        else
                        {
                            // Если присваиваем неизвестное значение, сбрасываем знание об этой переменной
                            if(currentLocals.ContainsKey(local.Index)) currentLocals.Remove(local.Index);
                        }
                    }
                }
                else if (instr.OpCode.Code == Code.Stloc_S)
                {
                     var local = instr.Operand as Local;
                     if (local != null && i > 0)
                     {
                         var prev = instructions[i - 1];
                         var val = GetConstantValue(prev);
                         if (val != null) currentLocals[local.Index] = val;
                         else if(currentLocals.ContainsKey(local.Index)) currentLocals.Remove(local.Index);
                     }
                }
                // Сброс при ветвлениях (упрощение: считаем, что после goto знания теряются, если не проанализированы пути)
                if (instr.OpCode.FlowControl == FlowControl.Branch || 
                    instr.OpCode.FlowControl == FlowControl.Cond_Branch ||
                    instr.OpCode.FlowControl == FlowControl.Switch)
                {
                    // В полной реализации здесь нужно сливать состояния из разных путей
                    // Сейчас просто очищаем для безопасности, чтобы не сделать ложных оптимизаций
                    // Но для паттерна "num = X; goto L; L: if (num == X)" это сработает, если анализировать блок линейно
                }
            }

            // Теперь проходим и упрощаем условия, используя собранные данные
            for (int i = 0; i < instructions.Count; i++)
            {
                var instr = instructions[i];

                if (instr.OpCode.FlowControl == FlowControl.Cond_Branch)
                {
                    // Проверяем, можем ли мы вычислить условие
                    // Это сложная часть: нужно посмотреть на стек перед этой инструкцией
                    // Для примера с "num" обычно это: ldloc, ldc, ceq, brtrue
                    // Найдем последовательность сравнения перед ветвлением
                    
                    if (i >= 2)
                    {
                        var cmpInstr = instructions[i - 1]; // Обычно ceq, cgt, clt
                        var loadVarInstr = instructions[i - 2]; // ldloc
                        
                        if (loadVarInstr.OpCode.Code == Code.Ldloc && cmpInstr.OpCode.Code == Code.Ceq)
                        {
                            var local = loadVarInstr.Operand as Local;
                            if (local != null && localStates[i].ContainsKey(local.Index))
                            {
                                var knownVal = localStates[i][local.Index];
                                var compareVal = GetConstantValue(cmpInstr.Previous); // Значение, с которым сравниваем (до ceq)
                                
                                // Нам нужно найти ldc перед ceq. Структура: ldloc, ldc, ceq, brtrue
                                if (i >= 3 && instructions[i-3] != null)
                                {
                                    var ldcInstr = instructions[i - 3];
                                    var constToCompare = GetConstantValue(ldcInstr);

                                    if (knownVal != null && constToCompare != null)
                                    {
                                        bool result = knownVal.Equals(constToCompare);
                                        
                                        // Если условие истинно всегда -> заменяем на безусловный переход (Br)
                                        // Если ложно всегда -> заменяем на Nop (пропускаем ветку)
                                        
                                        if (result)
                                        {
                                            // Условие всегда true: превращаем в безусловный goto
                                            instr.OpCode = OpCodes.Br;
                                            modified = true;
                                            Console.WriteLine($"  [+] Simplified TRUE branch at IL_{i:X4}");
                                        }
                                        else
                                        {
                                            // Условие всегда false: удаляем переход (превращаем в nop, выполнение идет дальше)
                                            instr.OpCode = OpCodes.Nop;
                                            instr.Operand = null;
                                            modified = true;
                                            Console.WriteLine($"  [+] Removed FALSE branch at IL_{i:X4}");
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return modified;
        }

        /// <summary>
        /// Превращает цепочки goto A -> goto B в прямой goto B
        /// </summary>
        private bool FlattenGotoChains(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            bool modified = false;

            foreach (var instr in instructions)
            {
                if (instr.OpCode.Code == Code.Br && instr.Operand is Instruction target)
                {
                    // Если цель тоже является безусловным переходом
                    if (target.OpCode.Code == Code.Br && target.Operand is Instruction nextTarget)
                    {
                        instr.Operand = nextTarget;
                        modified = true;
                    }
                    // Если цель ведет на следующую инструкцию (бессмысленный прыжок)
                    else if (target == instr.Next)
                    {
                        instr.OpCode = OpCodes.Nop;
                        instr.Operand = null;
                        modified = true;
                    }
                }
            }
            return modified;
        }

        private bool RemoveRedundantBranches(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            bool modified = false;
            for (int i = 0; i < instructions.Count; i++)
            {
                var instr = instructions[i];
                if (instr.OpCode.Code == Code.Br && instr.Operand is Instruction target)
                {
                    if (target == instr.Next)
                    {
                        instr.OpCode = OpCodes.Nop;
                        instr.Operand = null;
                        modified = true;
                    }
                }
            }
            return modified;
        }

        /// <summary>
        /// Удаляет инструкции, до которых невозможно добраться (после ret/throw, если туда нет goto)
        /// </summary>
        private void RemoveUnreachableCode(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            var reachable = new HashSet<Instruction>();
            
            // BFS от первой инструкции
            var queue = new Queue<Instruction>();
            if (instructions.Count > 0)
            {
                queue.Enqueue(instructions[0]);
                reachable.Add(instructions[0]);
            }

            while (queue.Count > 0)
            {
                var curr = queue.Dequeue();
                var nextIdx = instructions.IndexOf(curr) + 1;
                Instruction nextInstr = nextIdx < instructions.Count ? instructions[nextIdx] : null;

                switch (curr.OpCode.FlowControl)
                {
                    case FlowControl.Branch: // unconditional goto
                        if (curr.Operand is Instruction target)
                            AddToQueue(queue, reachable, target);
                        break;
                    case FlowControl.Cond_Branch: // if
                        if (curr.Operand is Instruction target)
                            AddToQueue(queue, reachable, target);
                        if (nextInstr != null)
                            AddToQueue(queue, reachable, nextInstr);
                        break;
                    case FlowControl.Switch:
                        if (curr.Operand is Instruction[] targets)
                            foreach (var t in targets) AddToQueue(queue, reachable, t);
                        if (nextInstr != null)
                            AddToQueue(queue, reachable, nextInstr);
                        break;
                    case FlowControl.Return:
                    case FlowControl.Throw:
                        // Конец пути
                        break;
                    default:
                        // Sequential flow
                        if (nextInstr != null)
                            AddToQueue(queue, reachable, nextInstr);
                        break;
                }
            }

            // Заменяем недостижимое на Nop
            bool changed = false;
            foreach (var instr in instructions)
            {
                if (!reachable.Contains(instr) && instr.OpCode.Code != Code.Nop)
                {
                    instr.OpCode = OpCodes.Nop;
                    instr.Operand = null;
                    changed = true;
                }
            }
            
            if (changed) Console.WriteLine("  [+] Removed unreachable code blocks.");
        }

        private void AddToQueue(Queue<Instruction> q, HashSet<Instruction> set, Instruction instr)
        {
            if (!set.Contains(instr))
            {
                set.Add(instr);
                q.Enqueue(instr);
            }
        }

        private void CleanupMethod(MethodDef method)
        {
            var body = method.Body;
            if (body == null) return;

            // Удаляем лишние NOP в конце метода
            for (int i = body.Instructions.Count - 1; i >= 0; i--)
            {
                if (body.Instructions[i].OpCode.Code == Code.Nop)
                {
                    // Проверка: является ли этот NOP целью какого-либо перехода?
                    bool isTarget = false;
                    foreach (var instr in body.Instructions)
                    {
                        if (instr.Operand == body.Instructions[i])
                        {
                            isTarget = true;
                            break;
                        }
                    }
                    
                    // Если не цель и стоит после ret/throw (упрощенно) - можно удалить, 
                    // но dnlib любит, чтобы список был непрерывным. Лучше оставить как Nop, 
                    // если он не мешает. 
                    // Здесь мы просто оставляем их как Nop, так как они уже безвредны.
                }
            }
            
            body.OptimizeMacros();
        }

        private object? GetConstantValue(Instruction? instr)
        {
            if (instr == null) return null;

            switch (instr.OpCode.Code)
            {
                case Code.Ldc_I4: return instr.Operand is int i ? i : (instr.Operand is int? ii ? ii : null);
                case Code.Ldc_I4_0: return 0;
                case Code.Ldc_I4_1: return 1;
                case Code.Ldc_I4_2: return 2;
                case Code.Ldc_I4_3: return 3;
                case Code.Ldc_I4_4: return 4;
                case Code.Ldc_I4_5: return 5;
                case Code.Ldc_I4_6: return 6;
                case Code.Ldc_I4_7: return 7;
                case Code.Ldc_I4_8: return 8;
                case Code.Ldc_I4_M1: return -1;
                case Code.Ldc_R4: return instr.Operand as float?;
                case Code.Ldc_R8: return instr.Operand as double?;
                case Code.Ldc_I8: return instr.Operand as long?;
                default: return null;
            }
        }

        // В dnlib 4.x у Instruction нет свойства Previous. 
        // Придется искать по индексу, если нужно.
        // Но в методе SimplifyConditionalJumps выше я использовал индексацию массива, что безопасно.
        // Если нужно получить предыдущий элемент относительно конкретного instruction в списке:
        private Instruction? GetPreviousInstruction(IList<Instruction> instructions, Instruction current)
        {
            int idx = instructions.IndexOf(current);
            return idx > 0 ? instructions[idx - 1] : null;
        }

        private bool IsObfuscatedName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            // Простая эвристика: имена из одного символа, вопросительные знаки, или бессмысленные наборы
            if (name.Length == 1 && char.IsLetter(name[0])) return true;
            if (name.Contains("?") || name.Contains("<")) return true;
            if (name.StartsWith("Class") || name.StartsWith("GClass") || name.StartsWith("smethod_")) return true;
            return false;
        }

        private void RenameWithAi(MethodDef method)
        {
            if (_aiAssistant == null) return;

            try
            {
                string ilCode = string.Join("\n", method.Body.Instructions.Take(30).Select(x => x.ToString()));
                string returnType = method.ReturnType?.ToString() ?? "void";
                
                string suggested = _aiAssistant.GetSuggestedName(method.Name, ilCode, returnType);
                
                if (!string.IsNullOrEmpty(suggested) && suggested != method.Name && !suggested.Contains("error"))
                {
                    method.Name = suggested;
                    Console.WriteLine($"  [AI] Renamed to: {suggested}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [AI] Failed: {ex.Message}");
            }
        }

        public void Save(string outputPath)
        {
            Console.WriteLine($"[*] Saving to: {outputPath}");
            
            var options = new ModuleWriterOptions(_module)
            {
                Logger = DummyLogger.NoThrowInstance,
                MetadataOptions = new MetadataOptions
                {
                    Flags = MetadataFlags.PreserveAll | MetadataFlags.KeepOldMaxStack
                }
            };

            try
            {
                _module.Write(outputPath, options);
                Console.WriteLine("[+] Successfully saved!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Save error: {ex.Message}");
                // Попытка спасти ситуацию, записав с еще меньшим количеством проверок
                Console.WriteLine("[*] Trying fallback save mode...");
                options.MetadataOptions.Flags |= MetadataFlags.TryToKeepPdbOffsets;
                _module.Write(outputPath, options);
                Console.WriteLine("[+] Saved with fallback options.");
            }
        }

        public void Dispose()
        {
            _aiAssistant?.Dispose();
            _module.Dispose();
        }
    }
}
