using System;
using System.Collections.Generic;
using System.IO;
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
        
        // Словарь для хранения известных значений локальных переменных во время анализа
        // Ключ: индекс локальной переменной, Значение: объект (double, int, etc.)
        private Dictionary<int, object?> _knownValues = new Dictionary<int, object?>();

        public UniversalDeobfuscator(string filePath, AiConfig aiConfig)
        {
            _module = ModuleDefMD.Load(filePath);
            _aiAssistant = aiConfig.Enabled ? new AiAssistant(aiConfig) : null;
        }

        public void Deobfuscate()
        {
            Console.WriteLine("[*] Starting deobfuscation...");

            foreach (var type in _module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody || !method.Body.HasInstructions) continue;

                    // Включаем сохранение старого MaxStack, чтобы избежать ошибок при записи
                    method.Body.KeepOldMaxStack = true;

                    Console.WriteLine($"[*] Processing: {method.FullName}");

                    // 1. Попытка распутать поток управления через симуляцию значений
                    UnravelFlowWithSymbolicExecution(method);

                    // 2. Очистка цепочек goto и nop
                    SimplifyJumps(method);

                    // 3. Удаление мертвого кода
                    RemoveDeadCode(method);

                    // 4. AI переименование (если включено)
                    if (_aiAssistant != null && method.Name.StartsWith("<") || method.Name.StartsWith("?"))
                    {
                        RenameWithAi(method);
                    }
                    
                    // Важно: обновляем оффсеты после изменений
                    method.Body.UpdateInstructionOffsets();
                }
            }

            Console.WriteLine("[*] Deobfuscation complete.");
        }

        /// <summary>
        /// Символьное выполнение для отслеживания переменных типа num = 1; if (num == 2)...
        /// </summary>
        private void UnravelFlowWithSymbolicExecution(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            bool changed = true;
            int iterations = 0;
            const int MAX_ITERATIONS = 50; // Защита от бесконечного цикла

            while (changed && iterations < MAX_ITERATIONS)
            {
                changed = false;
                iterations++;
                _knownValues.Clear();

                // Проходим по инструкциям, пытаясь вычислить значения
                for (int i = 0; i < instructions.Count; i++)
                {
                    var instr = instructions[i];
                    
                    // Отслеживание загрузки констант в локальные переменные
                    if (instr.OpCode.Code == Code.Stloc)
                    {
                        if (instr.Operand is Local local)
                        {
                            // Смотрим назад, чтобы найти, что было загружено
                            // В dnlib 4.x нет Previous, ищем по индексу
                            if (i > 0)
                            {
                                var prev = instructions[i - 1];
                                var val = GetConstantValue(prev);
                                if (val != null)
                                {
                                    _knownValues[local.Index] = val;
                                }
                            }
                        }
                    }
                    // Обработка коротких форм stloc.s, stloc.0 и т.д.
                    else if (instr.OpCode.Code >= Code.Stloc_0 && instr.OpCode.Code <= Code.Stloc_3)
                    {
                        int localIndex = instr.OpCode.Code - Code.Stloc_0;
                        if (i > 0)
                        {
                            var prev = instructions[i - 1];
                            var val = GetConstantValue(prev);
                            if (val != null)
                                _knownValues[localIndex] = val;
                        }
                    }
                    else if (instr.OpCode.Code == Code.Stloc_S)
                    {
                        if (instr.Operand is Local localS && i > 0)
                        {
                            var prev = instructions[i - 1];
                            var val = GetConstantValue(prev);
                            if (val != null)
                                _knownValues[localS.Index] = val;
                        }
                    }

                    // Упрощение условий: if (num == X)
                    // Ищем последовательность: ldloc, ldc, ceq, brtrue/brfalse
                    if (instr.OpCode.FlowControl == FlowControl.Cond_Branch && i >= 3)
                    {
                        // Проверяем паттерн: ldloc -> ldc -> ceq -> branch
                        var opCeq = instructions[i - 1];
                        var opLdc = instructions[i - 2];
                        var opLdloc = instructions[i - 3];

                        if (opCeq.OpCode.Code == Code.Ceq && opLdloc.OpCode.Code == Code.Ldloc)
                        {
                            int localIdx = -1;
                            if (opLdloc.Operand is Local l) localIdx = l.Index;
                            else if (opLdloc.OpCode.Code >= Code.Ldloc_0 && opLdloc.OpCode.Code <= Code.Ldloc_3)
                                localIdx = opLdloc.OpCode.Code - Code.Ldloc_0;
                            else if (opLdloc.OpCode.Code == Code.Ldloc_S && opLdloc.Operand is Local ls)
                                localIdx = ls.Index;

                            if (localIdx != -1 && _knownValues.ContainsKey(localIdx))
                            {
                                var knownVal = _knownValues[localIdx];
                                var compareVal = GetConstantValue(opLdc);

                                if (knownVal != null && compareVal != null)
                                {
                                    bool isEqual = knownVal.Equals(compareVal);
                                    bool isBranchTrue = instr.OpCode.Code == Code.Brtrue || instr.OpCode.Code == Code.Brtrue_S;
                                    
                                    // Если условие всегда истинно или ложно, заменяем переход
                                    bool shouldJump = (isEqual && isBranchTrue) || (!isEqual && !isBranchTrue);

                                    if (shouldJump)
                                    {
                                        // Заменяем условный переход на безусловный к цели
                                        instr.OpCode = OpCodes.Br;
                                        changed = true;
                                        Console.WriteLine($"  [Fix] Resolved conditional jump at IL_{instr.Offset:X4} based on known value.");
                                    }
                                    else
                                    {
                                        // Условие ложно, превращаем в Nop (не прыгаем)
                                        instr.OpCode = OpCodes.Nop;
                                        instr.Operand = null;
                                        changed = true;
                                        Console.WriteLine($"  [Fix] Removed false conditional jump at IL_{instr.Offset:X4}.");
                                    }
                                }
                            }
                        }
                    }
                    
                    // Обновление известных значений при присваивании внутри блоков
                    if (instr.OpCode.Code == Code.Ldc_R8 || instr.OpCode.Code == Code.Ldc_I4 || instr.OpCode.Code == Code.Ldc_I8)
                    {
                         // Если перед этим было stloc, мы уже обработали выше. 
                         // Здесь можно добавить логику, если значение используется сразу.
                    }
                }
                
                if (changed)
                {
                    method.Body.UpdateInstructionOffsets();
                }
            }
        }

        private object? GetConstantValue(Instruction? instr)
        {
            if (instr == null) return null;

            switch (instr.OpCode.Code)
            {
                case Code.Ldc_I4:
                    return instr.Operand is int val ? (object)val : null;
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
                case Code.Ldc_R4:
                    return instr.Operand is float f ? (object)f : null;
                case Code.Ldc_R8:
                    return instr.Operand is double d ? (object)d : null;
                case Code.Ldc_I8:
                    return instr.Operand is long l ? (object)l : null;
                default:
                    return null;
            }
        }

        /// <summary>
        /// Упрощение цепочек goto и удаление лишних nop
        /// </summary>
        private void SimplifyJumps(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            bool changed = true;

            while (changed)
            {
                changed = false;
                for (int i = 0; i < instructions.Count; i++)
                {
                    var instr = instructions[i];

                    // 1. Удаление цепочек: Br -> Br
                    if (instr.OpCode.Code == Code.Br && instr.Operand is Instruction target)
                    {
                        if (target.OpCode.Code == Code.Br && target.Operand is Instruction nextTarget)
                        {
                            instr.Operand = nextTarget;
                            changed = true;
                        }
                    }

                    // 2. Удаление перехода на следующую инструкцию
                    if (instr.OpCode.Code == Code.Br && instr.Operand is Instruction nextInstr)
                    {
                        // Находим индекс текущей инструкции, чтобы проверить следующую
                        // В dnlib 4.x надежнее искать по индексу списка
                        if (i + 1 < instructions.Count && nextInstr == instructions[i+1])
                        {
                            instr.OpCode = OpCodes.Nop;
                            instr.Operand = null;
                            changed = true;
                        }
                    }
                }
            }
            
            // Физическое удаление Nop (опционально, лучше оставить для безопасности структуры)
            // Здесь мы просто очищаем операнды у Nop
        }

        private void RemoveDeadCode(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            var reachable = new HashSet<Instruction>();
            
            // Простой анализ достижимости от начала
            var queue = new Queue<Instruction>();
            if (instructions.Count > 0)
            {
                queue.Enqueue(instructions[0]);
                reachable.Add(instructions[0]);
            }

            while (queue.Count > 0)
            {
                var curr = queue.Dequeue();
                if (curr == null) continue;

                // Если это ветвление
                if (curr.OpCode.FlowControl == FlowControl.Cond_Branch)
                {
                    if (curr.Operand is Instruction target)
                    {
                        AddIfNew(target, reachable, queue);
                    }
                    // Следующая инструкция тоже достижима
                    if (curr.Next != null) // Next работает в списке инструкций dnlib при итерации, но надежнее по индексу
                    {
                         // В dnlib 4.x Instruction.Next доступен, если список не изменен радикально, 
                         // но безопаснее использовать индекс. Однако для простоты оставим проверку.
                         // Если Next null, ищем по индексу
                         var idx = instructions.IndexOf(curr);
                         if (idx >= 0 && idx + 1 < instructions.Count)
                             AddIfNew(instructions[idx+1], reachable, queue);
                    }
                }
                else if (curr.OpCode.Code == Code.Br || curr.OpCode.Code == Code.Switch)
                {
                    if (curr.Operand is Instruction brTarget)
                    {
                         // Для Switch operand - массив инструкций
                         if (curr.OpCode.Code == Code.Switch && curr.Operand is Instruction[] targets)
                         {
                             foreach(var t in targets) AddIfNew(t, reachable, queue);
                         }
                         else
                         {
                             AddIfNew(brTarget, reachable, queue);
                         }
                    }
                }
                else if (curr.OpCode.FlowControl != FlowControl.Ret && 
                         curr.OpCode.FlowControl != FlowControl.Throw &&
                         curr.OpCode.FlowControl != FlowControl.Branch) // Branch обычно означает безусловный, обработан выше
                {
                    var idx = instructions.IndexOf(curr);
                    if (idx >= 0 && idx + 1 < instructions.Count)
                        AddIfNew(instructions[idx+1], reachable, queue);
                }
            }

            // Замена недостижимого кода на Nop
            for (int i = 0; i < instructions.Count; i++)
            {
                if (!reachable.Contains(instructions[i]))
                {
                    // Не удаляем, а заменяем на Nop, чтобы не сломать оффсеты других переходов до пересчета
                    if (instructions[i].OpCode.Code != Code.Nop)
                    {
                        instructions[i].OpCode = OpCodes.Nop;
                        instructions[i].Operand = null;
                    }
                }
            }
        }

        private void AddIfNew(Instruction instr, HashSet<Instruction> set, Queue<Instruction> queue)
        {
            if (instr != null && set.Add(instr))
            {
                queue.Enqueue(instr);
            }
        }

        private void RenameWithAi(MethodDef method)
        {
            if (_aiAssistant == null) return;

            try
            {
                string ilCode = string.Join("\n", method.Body.Instructions.Take(30).Select(x => x.ToString()));
                string suggested = _aiAssistant.GetSuggestedName(method.Name, ilCode, method.ReturnType.ToString());

                if (!string.IsNullOrEmpty(suggested) && suggested != method.Name && !suggested.Contains(" "))
                {
                    method.Name = suggested;
                    Console.WriteLine($"[AI] Renamed {method.Name}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AI Error]: {ex.Message}");
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
                Console.WriteLine("[+] Successfully saved.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Failed to save: {ex.Message}");
                throw;
            }
        }

        public void Dispose()
        {
            _aiAssistant?.Dispose();
            _module.Dispose();
        }
    }
}
