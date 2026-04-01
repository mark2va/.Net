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
            // Загрузка модуля без лишнего контекста
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
                    // Проверка на наличие тела метода и инструкций
                    if (method.Body == null || !method.Body.HasInstructions) 
                        continue;

                    Console.WriteLine($"[*] Processing: {method.FullName}");
                    
                    // ВАЖНО: Отключаем пересчет MaxStack для обфусцированных методов
                    method.Body.KeepOldMaxStack = true;

                    // 1. Сбор констант
                    AnalyzeConstants(method);

                    // 2. Упрощение условий
                    SimplifyConstantConditions(method);

                    // 3. Распутывание потоков (Goto, циклы)
                    UnravelControlFlow(method);

                    // 4. Удаление мертвого кода
                    RemoveDeadCode(method);

                    // Обновляем оффсеты после изменений инструкций
                    method.Body.UpdateInstructionOffsets();

                    // 5. AI Переименование (если включено)
                    if (_aiAssistant != null && method.Name.StartsWith("<"))
                    {
                        RenameWithAi(method);
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
                Code code = instr.OpCode.Code;

                // Обработка всех видов Stloc
                Local? local = null;
                if (code == Code.Stloc && instr.Operand is Local l) local = l;
                else if (code == Code.Stloc_S && instr.Operand is Local ls) local = ls;
                else if (code >= Code.Stloc_0 && code <= Code.Stloc_3) 
                    local = method.Body.Variables[code - Code.Stloc_0];

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
            // Заглушка для будущей логики упрощения условий
            // Реализация требует сложного анализа стека
        }

        private void UnravelControlFlow(MethodDef method)
        {
            bool changed = true;
            var instructions = method.Body.Instructions;

            while (changed)
            {
                changed = false;
                // Пересоздаем список для безопасной итерации при изменениях
                for (int i = 0; i < instructions.Count; i++)
                {
                    var instr = instructions[i];

                    // Удаление цепочек goto: goto L1; L1: goto L2; -> goto L2;
                    if (instr.OpCode.Code == Code.Br && instr.Operand is Instruction target)
                    {
                        if (target.OpCode.Code == Code.Br && target.Operand is Instruction nextTarget)
                        {
                            instr.Operand = nextTarget;
                            changed = true;
                        }
                    }
                    
                    // Удаление безусловных переходов на следующую инструкцию
                    if (instr.OpCode.Code == Code.Br && instr.Operand is Instruction nextInstr)
                    {
                        // Проверяем, является ли целевая инструкция следующей по порядку
                        if (i + 1 < instructions.Count && instructions[i+1] == nextInstr)
                        {
                            instr.OpCode = OpCodes.Nop;
                            instr.Operand = null;
                            changed = true;
                        }
                    }
                }
            }
        }

        private void RemoveDeadCode(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            for (int i = instructions.Count - 1; i >= 0; i--)
            {
                var instr = instructions[i];
                if (instr.OpCode.FlowControl == FlowControl.Ret || instr.OpCode.FlowControl == FlowControl.Throw)
                {
                    // Удаляем все инструкции после, если они не являются целевыми точками ветвлений
                    for (int j = i + 1; j < instructions.Count; j++)
                    {
                        if (!IsBranchTarget(instructions[j], instructions))
                        {
                            instructions[j].OpCode = OpCodes.Nop;
                            instructions[j].Operand = null;
                        }
                    }
                    break; 
                }
            }
        }

        private bool IsBranchTarget(Instruction instr, IList<Instruction> all)
        {
            foreach (var i in all)
            {
                if (i.Operand == instr && (i.OpCode.FlowControl == FlowControl.Cond_Branch || i.OpCode.Code == Code.Br || i.OpCode.Code == Code.Switch))
                    return true;
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
                    Console.WriteLine($"[AI] Renamed to: {suggested}");
                }
            }
            catch { }
        }

        public void Save(string outputPath)
        {
            Console.WriteLine($"[*] Saving to: {outputPath}");
            
            // Настройка опций записи для игнорирования ошибок стека
            var options = new ModuleWriterOptions(_module)
            {
                Logger = DummyLogger.NoThrowInstance,
                MetadataOptions = new MetadataOptions
                {
                    // Сохраняем старый MaxStack и игнорируем некоторые ошибки валидации
                    Flags = MetadataFlags.KeepOldMaxStack
                },
                // Если метод имеет неправильный стек, мы уже установили KeepOldMaxStack в теле метода,
                // но эта настройка добавляет страховку на уровне модуля
            };

            try 
            {
                _module.Write(outputPath, options);
                Console.WriteLine($"[+] Saved successfully!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Error saving: {ex.Message}");
                // Попытка сохранения с минимальными проверками
                Console.WriteLine("[*] Trying fallback save...");
                _module.Write(outputPath);
            }
        }

        public void Dispose()
        {
            _aiAssistant?.Dispose();
            _module.Dispose();
        }
    }
}
