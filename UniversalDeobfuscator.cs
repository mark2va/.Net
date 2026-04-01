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
        private readonly Dictionary<Local, object?> _constantLocals = new Dictionary<Local, object?>();

        public UniversalDeobfuscator(string filePath, AiConfig aiConfig)
        {
            // Включаем режим игнорирования ошибок MaxStack при загрузке, если нужно
            var ctx = ModuleCreationContext.Create(filePath);
            _module = ModuleDefMD.Load(filePath, ctx);
            
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

                    // Пропускаем методы без IL (например, P/Invoke)
                    if (!method.Body.IsIL) continue;

                    try 
                    {
                        Console.WriteLine($"[*] Processing: {method.FullName}");
                        
                        // 1. Сбор констант
                        AnalyzeConstants(method);

                        // 2. Упрощение условий
                        SimplifyConstantConditions(method);

                        // 3. Распутывание потоков (Goto, циклы)
                        UnravelControlFlow(method);

                        // 4. Удаление мертвого кода
                        RemoveDeadCode(method);

                        // ВАЖНО: После изменений сбрасываем флаг, чтобы dnlib пересчитал стек корректно
                        // Но если метод сильно обфусцирован, лучше оставить старый стек
                        method.Body.KeepOldMaxStack = true; 

                        // 5. AI Переименование (если включено)
                        if (_aiAssistant != null && method.Name.StartsWith("<"))
                        {
                            RenameWithAi(method);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[!] Error in method {method.FullName}: {ex.Message}");
                    }
                }
            }
            
            Console.WriteLine("[*] Deobfuscation complete.");
        }

        private void AnalyzeConstants(MethodDef method)
        {
            _constantLocals.Clear();
            var instructions = method.Body.Instructions;
            
            for (int i = 0; i < instructions.Count; i++)
            {
                var instr = instructions[i];
                
                // Обработка всех видов Stloc
                Local? local = null;
                if (instr.OpCode.Code == Code.Stloc && instr.Operand is Local l) local = l;
                else if (instr.OpCode.Code == Code.Stloc_S && instr.Operand is Local ls) local = ls;
                else if (instr.OpCode.Code >= Code.Stloc_0 && instr.OpCode.Code <= Code.Stloc_3)
                {
                    int idx = instr.OpCode.Code - Code.Stloc_0;
                    if (idx < method.Body.Variables.Count) local = method.Body.Variables[idx];
                }

                if (local != null && i > 0)
                {
                    var prev = instructions[i - 1];
                    object? val = GetConstantValue(prev);
                    if (val != null)
                        _constantLocals[local] = val;
                }
            }
        }

        private object? GetConstantValue(Instruction? instr)
        {
            if (instr == null) return null;

            switch (instr.OpCode.Code)
            {
                case Code.Ldc_I4:
                    if (instr.Operand is int) return (int)instr.Operand;
                    return null;
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
                    if (instr.Operand is float) return (float)instr.Operand;
                    return null;
                case Code.Ldc_R8:
                    if (instr.Operand is double) return (double)instr.Operand;
                    return null;
                case Code.Ldc_I8:
                    if (instr.Operand is long) return (long)instr.Operand;
                    return null;
                default:
                    return null;
            }
        }

        private void SimplifyConstantConditions(MethodDef method)
        {
            // Заглушка для будущей реализации сложной логики
            // Сейчас просто оставляем как есть, чтобы не ломать стек
        }

        private void UnravelControlFlow(MethodDef method)
        {
            bool changed = true;
            var instructions = method.Body.Instructions;

            while (changed)
            {
                changed = false;
                // Пересоздаем список инструкций для безопасной итерации, если нужно, 
                // но здесь мы работаем по индексу, что безопасно при замене Nop
                
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
                    // Проверяем, является ли цель следующей инструкцией в списке
                    if (instr.OpCode.Code == Code.Br && instr.Operand is Instruction nextInstr)
                    {
                        int currentIndex = instructions.IndexOf(instr);
                        int targetIndex = instructions.IndexOf(nextInstr);
                        
                        if (targetIndex == currentIndex + 1)
                        {
                            instr.OpCode = OpCodes.Nop;
                            instr.Operand = null;
                            changed = true;
                        }
                    }
                }
            }
            
            // Удаляем лишние Nop в конце метода, если они не являются целями ветвлений
            CleanupNops(method);
        }

        private void CleanupNops(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            // Простая очистка: если Nop не является целью ветвления, можно удалить (заменить на пустоту позже)
            // Для безопасности пока просто оставляем, dnlib сам сожмет их при оптимизации, 
            // если не стоит KeepOldMaxStack. Но мы его поставили.
        }

        private void RemoveDeadCode(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            bool foundTerminal = false;

            // Идем с конца к началу
            for (int i = instructions.Count - 1; i >= 0; i--)
            {
                var instr = instructions[i];

                if (foundTerminal)
                {
                    // Если инструкция недостижима (после ret/throw) и не является целью ветвления
                    if (!IsBranchTarget(instr, instructions))
                    {
                        instr.OpCode = OpCodes.Nop;
                        instr.Operand = null;
                    }
                }
                else
                {
                    if (instr.OpCode.FlowControl == FlowControl.Ret || 
                        instr.OpCode.FlowControl == FlowControl.Throw)
                    {
                        foundTerminal = true;
                    }
                }
            }
        }

        private bool IsBranchTarget(Instruction instr, IList<Instruction> all)
        {
            foreach (var i in all)
            {
                if (i.Operand == instr) return true;
                
                // Проверка для Switch
                if (i.OpCode.Code == Code.Switch && i.Operand is Instruction[] targets)
                {
                    if (targets.Contains(instr)) return true;
                }
            }
            return false;
        }

        private void RenameWithAi(MethodDef method)
        {
            if (_aiAssistant == null) return;

            try
            {
                string ilCode = string.Join("\n", method.Body.Instructions.Take(20).Select(x => x.ToString()));
                string suggested = _aiAssistant.GetSuggestedName(method.Name, ilCode, method.ReturnType.ToString());
                
                if (!string.IsNullOrEmpty(suggested) && suggested != method.Name)
                {
                    method.Name = suggested;
                    Console.WriteLine(" [AI] Renamed to: " + suggested);
                }
            }
            catch { }
        }

        public void Save(string outputPath)
        {
            try
            {
                var options = new ModuleWriterOptions(_module)
                {
                    Logger = DummyLogger.NoThrowInstance, // Игнорируем предупреждения
                    MetadataOptions = new MetadataOptions
                    {
                        Flags = MetadataFlags.PreserveAll | MetadataFlags.KeepOldMaxStack
                    }
                };
                
                // Явно разрешаем запись даже с потенциальными ошибками стека
                options.MetadataOptions.Flags |= MetadataFlags.KeepOldMaxStack;

                Console.WriteLine("[*] Saving module...");
                _module.Write(outputPath, options);
                Console.WriteLine("[+] Saved to: " + outputPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[!] Fatal Error during save: " + ex.Message);
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
