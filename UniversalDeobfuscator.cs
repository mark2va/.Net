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
        
        // Хранит текущее известное значение локальной переменной во время символьного выполнения
        private readonly Dictionary<int, object?> _localValues = new Dictionary<int, object?>();

        public UniversalDeobfuscator(string filePath, AiConfig aiConfig)
        {
            // В dnlib 4.x загружаем напрямую без контекста, если не нужны сложные опции PDB
            _module = ModuleDefMD.Load(filePath);
            _aiAssistant = aiConfig.Enabled ? new AiAssistant(aiConfig) : null;
        }

        public void Deobfuscate()
        {
            Console.WriteLine("[*] Starting deobfuscation...");
            
            int methodsCount = 0;
            foreach (var type in _module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody || method.Body == null || !method.Body.HasInstructions) continue;
                    
                    methodsCount++;
                    Console.WriteLine($"[*] Processing: {method.FullName}");

                    try
                    {
                        // 1. Символьное выполнение для упрощения условий и goto
                        SymbolicExecution(method);

                        // 2. Очистка мертвого кода и nop
                        CleanupMethod(method);

                        // 3. AI переименование (если включено и имя странное)
                        if (_aiAssistant != null && IsObfuscatedName(method.Name))
                        {
                            RenameWithAi(method);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[!] Error in method {method.Name}: {ex.Message}");
                    }
                }
            }
            
            Console.WriteLine($"[*] Processed {methodsCount} methods.");
        }

        /// <summary>
        /// Символьное выполнение для раскрытия запутанных потоков управления (num = X; if num == Y...)
        /// </summary>
        private void SymbolicExecution(MethodDef method)
        {
            var body = method.Body;
            if (body == null) return;
            
            var instructions = body.Instructions;
            bool changed = true;
            int iterations = 0;
            const int maxIterations = 50; // Защита от бесконечных циклов

            while (changed && iterations < maxIterations)
            {
                changed = false;
                iterations++;
                _localValues.Clear();

                // Проходим по инструкциям, пытаясь вычислить значения переменных
                for (int i = 0; i < instructions.Count; i++)
                {
                    var instr = instructions[i];
                    
                    // Обработка загрузки констант в локаль (ldc -> stloc)
                    // Ищем паттерн: [i] ldc..., [i+1] stloc
                    if (i + 1 < instructions.Count)
                    {
                        var nextInstr = instructions[i + 1];
                        if (IsStloc(nextInstr, out int localIndex))
                        {
                            var constVal = GetConstantValue(instr);
                            if (constVal != null)
                            {
                                _localValues[localIndex] = constVal;
                                
                                // Если следующая инструкция сразу сравнивает эту переменную, можно упростить
                                if (i + 2 < instructions.Count)
                                {
                                    var cmpInstr = instructions[i + 2];
                                    if (TrySimplifyComparison(cmpInstr, localIndex, constVal, instructions, i + 2, ref changed))
                                    {
                                        // Перезапуск цикла, так как инструкции изменились
                                        break; 
                                    }
                                }
                            }
                        }
                    }

                    // Обновление значения переменной при присваивании
                    if (IsStloc(instr, out int idx))
                    {
                        // Пытаемся найти значение в стеке (упрощенно: если предыдущая была константой)
                        if (i > 0)
                        {
                            var prevVal = GetConstantValue(instructions[i - 1]);
                            if (prevVal != null)
                                _localValues[idx] = prevVal;
                        }
                    }
                }
            }
            
            // Финальная очистка unreachable кода после упрощения
            RemoveUnreachableBlocks(method);
        }

        private bool TrySimplifyComparison(Instruction instr, int localIndex, object? knownValue, 
            IList<Instruction> instructions, int currentIndex, ref bool changed)
        {
            // Проверяем, является ли инструкция сравнением или ветвлением
            if (instr.OpCode.FlowControl == FlowControl.Cond_Branch)
            {
                // Попытка найти паттерн: ldloc (наша переменная), ldc (константа), branch
                // В dnlib мы не можем легко посмотреть назад без индекса, но мы знаем currentIndex.
                // Обычно перед ветвлением идет ceq, cgt, clt или само ветвление с операндами в стеке.
                
                // Упрощение: если ветвление ведет на ту же инструкцию (бесконечный цикл) или на следующую
                if (instr.Operand is Instruction target)
                {
                    int targetIndex = instructions.IndexOf(target);
                    
                    // Ветвление на следующую инструкцию (бессмысленно) -> удаляем
                    if (targetIndex == currentIndex + 1)
                    {
                        instr.OpCode = OpCodes.Nop;
                        instr.Operand = null;
                        changed = true;
                        return true;
                    }
                    
                    // Если мы знаем, что условие всегда истинно/ложно (сложная логика, пока пропускаем для простоты)
                    // Здесь можно добавить логику: если knownValue == compareValue, то заменяем на безусловный переход
                }
            }
            return false;
        }

        /// <summary>
        /// Удаляет недостижимые блоки кода (после ret, throw, безусловных goto в конец)
        /// </summary>
        private void RemoveUnreachableBlocks(MethodDef method)
        {
            var body = method.Body;
            if (body == null) return;
            
            var instructions = body.Instructions;
            if (instructions.Count == 0) return;

            // Вычисляем достижимые инструкции
            var reachable = new HashSet<Instruction>();
            var queue = new Queue<Instruction>();
            
            // Точка входа
            if (instructions.Count > 0)
            {
                queue.Enqueue(instructions[0]);
                reachable.Add(instructions[0]);
            }

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                int index = instructions.IndexOf(current);
                if (index == -1) continue;

                // Если это не безусловный переход и не ret/throw, следующая инструкция достижима
                if (current.OpCode.Code != Code.Br && current.OpCode.Code != Code.Ret && current.OpCode.Code != Code.Throw)
                {
                    if (index + 1 < instructions.Count)
                    {
                        var next = instructions[index + 1];
                        if (reachable.Add(next))
                            queue.Enqueue(next);
                    }
                }

                // Если есть операнд (цель перехода), он достижим
                if (current.Operand is Instruction target)
                {
                    if (reachable.Add(target))
                        queue.Enqueue(target);
                }
                else if (current.Operand is Instruction[] targets)
                {
                    foreach (var t in targets)
                    {
                        if (reachable.Add(t))
                            queue.Enqueue(t);
                    }
                }
            }

            // Заменяем недостижимые инструкции на Nop
            bool hasChanges = false;
            for (int i = 0; i < instructions.Count; i++)
            {
                if (!reachable.Contains(instructions[i]))
                {
                    instructions[i].OpCode = OpCodes.Nop;
                    instructions[i].Operand = null;
                    hasChanges = true;
                }
            }

            if (hasChanges)
            {
                SimplifyMacrosSafe(method);
                body.UpdateInstructionOffsets();
            }
        }

        private void CleanupMethod(MethodDef method)
        {
            var body = method.Body;
            if (body == null) return;

            // Удаляем последовательные Nop
            bool changed = true;
            while (changed)
            {
                changed = false;
                var instructions = body.Instructions;
                for (int i = instructions.Count - 1; i >= 0; i--)
                {
                    if (instructions[i].OpCode.Code == Code.Nop)
                    {
                        // Можно удалить, если на него никто не ссылается
                        bool isTarget = false;
                        foreach (var instr in instructions)
                        {
                            if (instr.Operand == instructions[i])
                            {
                                isTarget = true;
                                break;
                            }
                            if (instr.Operand is Instruction[] arr && arr.Contains(instructions[i]))
                            {
                                isTarget = true;
                                break;
                            }
                        }

                        if (!isTarget)
                        {
                            instructions.RemoveAt(i);
                            changed = true;
                        }
                    }
                }
            }
            
            SimplifyMacrosSafe(method);
            body.UpdateInstructionOffsets();
        }

        // Безопасный вызов SimplifyMacros с передачей параметров метода
        private void SimplifyMacrosSafe(MethodDef method)
        {
            if (method.Body != null && method.Parameters != null)
            {
                method.Body.SimplifyMacros(method.Parameters);
            }
        }

        private bool IsStloc(Instruction instr, out int localIndex)
        {
            localIndex = -1;
            if (instr.OpCode.Code == Code.Stloc && instr.Operand is Local l)
            {
                localIndex = l.Index;
                return true;
            }
            if (instr.OpCode.Code == Code.Stloc_S && instr.Operand is Local ls)
            {
                localIndex = ls.Index;
                return true;
            }
            // Stloc.0 - Stloc.3
            switch (instr.OpCode.Code)
            {
                case Code.Stloc_0: localIndex = 0; return true;
                case Code.Stloc_1: localIndex = 1; return true;
                case Code.Stloc_2: localIndex = 2; return true;
                case Code.Stloc_3: localIndex = 3; return true;
            }
            return false;
        }

        private object? GetConstantValue(Instruction? instr)
        {
            if (instr == null) return null;

            switch (instr.OpCode.Code)
            {
                case Code.Ldc_I4:
                    if (instr.Operand is int val) return val;
                    break;
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
                    if (instr.Operand is float f) return f;
                    break;
                case Code.Ldc_R8:
                    if (instr.Operand is double d) return d;
                    break;
                case Code.Ldc_I8:
                    if (instr.Operand is long lng) return lng;
                    break;
            }
            return null;
        }

        private bool IsObfuscatedName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.Length == 1 && char.IsLetter(name[0])) return true;
            if (name.StartsWith("?") || name.StartsWith(" ")) return true;
            return false;
        }

        private void RenameWithAi(MethodDef method)
        {
            if (_aiAssistant == null) return;

            try
            {
                var ilSnippet = string.Join("\n", method.Body.Instructions.Take(10).Select(x => x.ToString()));
                var suggested = _aiAssistant.GetSuggestedName(method.Name, ilSnippet, method.ReturnType?.ToString() ?? "void");
                
                if (!string.IsNullOrEmpty(suggested) && suggested != method.Name && IsValidIdentifier(suggested))
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
            Console.WriteLine($"[*] Saving to: {outputPath}");
            
            var options = new ModuleWriterOptions(_module)
            {
                Logger = DummyLogger.NoThrowInstance,
                MetadataOptions = new MetadataOptions
                {
                    Flags = MetadataFlags.KeepOldMaxStack | MetadataFlags.PreserveAll
                }
            };

            try
            {
                _module.Write(outputPath, options);
                Console.WriteLine("[+] Successfully saved.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Save error: {ex.Message}");
                // Попытка сохранить без строгих проверок
                options.MetadataOptions.Flags = MetadataFlags.KeepOldMaxStack;
                _module.Write(outputPath, options);
                Console.WriteLine("[+] Saved with relaxed options.");
            }
        }

        public void Dispose()
        {
            _aiAssistant?.Dispose();
            _module.Dispose();
        }
    }
}
