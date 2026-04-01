using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Deobfuscator
{
    public class UniversalDeobfuscator : IDisposable
    {
        private readonly ModuleDefMD _module;
        private readonly AiAssistant? _aiAssistant;
        // Храним константы: Local -> Value
        private readonly Dictionary<Local, object?> _constantLocals = new Dictionary<Local, object?>();

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

                    // Пропускаем слишком маленькие методы или конструкторы для скорости
                    if (method.Body.Instructions.Count < 5 && !method.IsConstructor) 
                    {
                         // Можно обработать, но для примера пропускаем
                    }

                    Console.WriteLine($"[*] Processing: {method.FullName}");
                    
                    try
                    {
                        // 1. Сбор констант
                        AnalyzeConstants(method);

                        // 2. Упрощение условий (на основе собранных констант)
                        SimplifyConstantConditions(method);

                        // 3. Распутывание потоков (Goto, циклы)
                        UnravelControlFlow(method);

                        // 4. Удаление мертвого кода
                        RemoveDeadCode(method);

                        // 5. Обновление инструкций (важно после изменений)
                        method.Body.UpdateInstructionOffsets();

                        // 6. AI Переименование (если включено и имя обфусцировано)
                        if (_aiAssistant != null && IsObfuscatedName(method.Name))
                        {
                            RenameWithAi(method);
                        }

                        methodsProcessed++;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[!] Error in method {method.FullName}: {ex.Message}");
                    }
                }
            }
            
            Console.WriteLine($"[*] Deobfuscation complete. Processed {methodsProcessed} methods.");
        }

        private bool IsObfuscatedName(string name)
        {
            // Простая эвристика: имена типа "a", "<>c__DisplayClass0_0", "Method_123"
            if (string.IsNullOrEmpty(name)) return false;
            if (name.Length == 1) return true;
            if (name.StartsWith("<")) return true;
            if (name.Contains("__") && char.IsDigit(name[name.Length - 1])) return true;
            return false;
        }

        private void AnalyzeConstants(MethodDef method)
        {
            _constantLocals.Clear();
            var instructions = method.Body.Instructions;

            for (int i = 0; i < instructions.Count; i++)
            {
                var instr = instructions[i];
                Local? local = null;

                // Определяем, в какую локаль сохраняется значение
                if (instr.OpCode.Code == Code.Stloc)
                {
                    local = instr.Operand as Local;
                }
                else if (instr.OpCode.Code >= Code.Stloc_0 && instr.OpCode.Code <= Code.Stloc_3)
                {
                    int index = instr.OpCode.Code - Code.Stloc_0;
                    if (index < method.Body.Variables.Count)
                        local = method.Body.Variables[index];
                }
                else if (instr.OpCode.Code == Code.Stloc_S)
                {
                    local = instr.Operand as Local;
                }

                if (local != null)
                {
                    // Ищем предыдущую инструкцию загрузки константы
                    // В простом случае это просто инструкция перед Stloc
                    Instruction? prev = GetPreviousInstruction(instructions, instr);
                    if (prev != null)
                    {
                        object? val = GetConstantValue(prev);
                        if (val != null)
                        {
                            _constantLocals[local] = val;
                        }
                    }
                }
            }
        }

        // Helper: Получение предыдущей инструкции из списка, так как у Instruction нет свойства Previous
        private Instruction? GetPreviousInstruction(IList<Instruction> instructions, Instruction current)
        {
            int index = instructions.IndexOf(current);
            if (index > 0) return instructions[index - 1];
            return null;
        }

        private object? GetConstantValue(Instruction? instr)
        {
            if (instr == null) return null;

            switch (instr.OpCode.Code)
            {
                case Code.Ldc_I4:
                    return instr.Operand is int val ? val : (object?)null;
                
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
                    return instr.Operand is float f ? f : (object?)null;

                case Code.Ldc_R8:
                    return instr.Operand is double d ? d : (object?)null;

                case Code.Ldc_I8:
                    return instr.Operand is long l ? l : (object?)null;

                default:
                    return null;
            }
        }

        private void SimplifyConstantConditions(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            bool changed = true;

            // Упрощенная реализация: замена ветвлений, если условие известно
            // Полная реализация требует эмуляции стека, здесь показан принцип
            for (int i = 0; i < instructions.Count; i++)
            {
                var instr = instructions[i];

                if (instr.OpCode.FlowControl == FlowControl.Cond_Branch)
                {
                    // Пример: если перед ветвлением было сравнение двух констант
                    // Это сложная часть, требующая анализа стека. 
                    // Для начала реализуем только замену переменных на константы, если они известны
                }
            }
        }

        private void UnravelControlFlow(MethodDef method)
        {
            bool changed = true;
            while (changed)
            {
                changed = false;
                var instructions = method.Body.Instructions;

                for (int i = 0; i < instructions.Count; i++)
                {
                    var instr = instructions[i];

                    // 1. Удаление цепочек goto: goto L1; L1: goto L2; -> goto L2;
                    if (instr.OpCode.Code == Code.Br && instr.Operand is Instruction target)
                    {
                        if (target.OpCode.Code == Code.Br && target.Operand is Instruction nextTarget)
                        {
                            instr.Operand = nextTarget;
                            changed = true;
                        }
                    }

                    // 2. Удаление безусловных переходов на следующую инструкцию
                    if (instr.OpCode.Code == Code.Br && instr.Operand is Instruction nextInstr)
                    {
                        if (nextInstr == instr.Next)
                        {
                            instr.OpCode = OpCodes.Nop;
                            instr.Operand = null;
                            changed = true;
                        }
                    }
                    
                    // 3. Удаление Nop перед целевой точкой, если это безопасно (упрощенно)
                    if (instr.OpCode.Code == Code.Nop && instr.Next != null)
                    {
                         // Можно добавить логику слияния блоков, но пока оставим Nop для безопасности
                    }
                }
            }
        }

        private void RemoveDeadCode(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            
            // Находим все инструкции, на которые есть переходы
            var targets = new HashSet<Instruction>();
            foreach (var instr in instructions)
            {
                if (instr.Operand is Instruction t) targets.Add(t);
                if (instr.Operand is Instruction[] ts)
                {
                    foreach (var x in ts) targets.Add(x);
                }
            }

            // Удаляем код после безусловного возврата, если он не является целью перехода
            for (int i = 0; i < instructions.Count; i++)
            {
                var instr = instructions[i];
                if (instr.OpCode.FlowControl == FlowControl.Ret || instr.OpCode.FlowControl == FlowControl.Throw)
                {
                    // Проверяем следующие инструкции
                    for (int j = i + 1; j < instructions.Count; j++)
                    {
                        if (!targets.Contains(instructions[j]))
                        {
                            instructions[j].OpCode = OpCodes.Nop;
                            instructions[j].Operand = null;
                        }
                    }
                    break; // Дальше идти нет смысла
                }
            }
        }

        private void RenameWithAi(MethodDef method)
        {
            if (_aiAssistant == null) return;

            try
            {
                // Берем первые 30 инструкций для контекста
                string ilCode = string.Join("\n", method.Body.Instructions.Take(30).Select(x => x.ToString()));
                string suggested = _aiAssistant.GetSuggestedName(method.Name, ilCode, method.ReturnType?.ToString() ?? "void");
                
                if (!string.IsNullOrEmpty(suggested) && suggested != method.Name && IsValidIdentifier(suggested))
                {
                    method.Name = suggested;
                    Console.WriteLine($"[AI] Renamed '{method.Name}' -> '{suggested}'");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AI Error]: {ex.Message}");
            }
        }

        private bool IsValidIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (!char.IsLetter(name[0]) && name[0] != '_') return false;
            foreach (char c in name)
            {
                if (!char.IsLetterOrDigit(c) && c != '_') return false;
            }
            return true;
        }

        public void Save(string outputPath)
        {
            // Опции сохранения
            var opts = new DNLib.WriteOptions();
            _module.Write(outputPath, opts);
            Console.WriteLine($"[+] Saved to: {outputPath}");
        }

        public void Dispose()
        {
            _aiAssistant?.Dispose();
            _module.Dispose();
        }
    }
}
