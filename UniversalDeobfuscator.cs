using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Deobfuscator
{
    public class UniversalDeobfuscator : IDisposable
    {
        private readonly ModuleDefMD _module;
        private readonly AiAssistant? _aiAssistant;
        private readonly Dictionary<Local, object?> _constantLocals = new Dictionary<Local, object?>();

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

                    Console.WriteLine($"[*] Processing: {method.FullName}");
                    
                    // Обновляем список инструкций (важно для dnlib 4.x)
                    var instructions = method.Body.Instructions;

                    // 1. Сбор констант
                    AnalyzeConstants(method, instructions);

                    // 2. Упрощение условий
                    SimplifyConstantConditions(method, instructions);

                    // 3. Распутывание потоков (Goto, циклы)
                    UnravelControlFlow(method, instructions);

                    // 4. Удаление мертвого кода
                    RemoveDeadCode(method, instructions);

                    // 5. AI Переименование
                    if (_aiAssistant != null && method.Name.StartsWith("<"))
                    {
                        RenameWithAi(method);
                    }
                    
                    // Обновляем оффсеты после изменений
                    method.Body.UpdateInstructionOffsets();
                }
            }
            
            Console.WriteLine("[*] Deobfuscation complete.");
        }

        private void AnalyzeConstants(MethodDef method, IList<Instruction> instructions)
        {
            _constantLocals.Clear();
            for (int i = 0; i < instructions.Count; i++)
            {
                var instr = instructions[i];
                
                // Обработка всех вариантов сохранения в локаль
                if (instr.OpCode.Code == Code.Stloc || instr.OpCode.Code == Code.Stloc_S)
                {
                    if (instr.Operand is Local local)
                    {
                        // Ищем предыдущую инструкцию (i - 1)
                        if (i > 0)
                        {
                            var prev = instructions[i - 1];
                            object? val = GetConstantValue(prev);
                            if (val != null)
                                _constantLocals[local] = val;
                        }
                    }
                }
                // Обработка коротких форм stloc.0 - stloc.3
                else if (instr.OpCode.Code >= Code.Stloc_0 && instr.OpCode.Code <= Code.Stloc_3)
                {
                    int index = instr.OpCode.Code - Code.Stloc_0;
                    if (method.Body.Variables.Count > index)
                    {
                        var local = method.Body.Variables[index];
                        if (i > 0)
                        {
                            var prev = instructions[i - 1];
                            object? val = GetConstantValue(prev);
                            if (val != null)
                                _constantLocals[local] = val;
                        }
                    }
                }
            }
        }

        private object? GetConstantValue(Instruction? instr)
        {
            if (instr == null) return null;

            switch (instr.OpCode.Code)
            {
                case Code.Ldc_I4:
                    return instr.Operand as int?;
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
                    return instr.Operand as float?;
                case Code.Ldc_R8:
                    return instr.Operand as double?;
                case Code.Ldc_I8:
                    return instr.Operand as long?;
                default:
                    return null;
            }
        }

        private void SimplifyConstantConditions(MethodDef method, IList<Instruction> instructions)
        {
            // Здесь можно добавить логику замены ветвлений на основе _constantLocals
            // Для базовой версии пока оставим заглушку, чтобы не усложнять код стека
        }

        private void UnravelControlFlow(MethodDef method, IList<Instruction> instructions)
        {
            bool changed = true;
            while (changed)
            {
                changed = false;

                for (int i = 0; i < instructions.Count; i++)
                {
                    var instr = instructions[i];
                    Instruction? nextInstr = (i < instructions.Count - 1) ? instructions[i + 1] : null;

                    // 1. Удаление безусловных переходов на следующую инструкцию
                    if (instr.OpCode.Code == Code.Br && instr.Operand is Instruction target)
                    {
                        if (target == nextInstr)
                        {
                            instr.OpCode = OpCodes.Nop;
                            instr.Operand = null;
                            changed = true;
                            continue;
                        }

                        // 2. Распутывание цепочек goto: goto L1 -> L1: goto L2
                        int targetIndex = instructions.IndexOf(target);
                        if (targetIndex != -1 && targetIndex < instructions.Count)
                        {
                            var targetInstr = instructions[targetIndex];
                            if (targetInstr.OpCode.Code == Code.Br && targetInstr.Operand is Instruction nextTarget)
                            {
                                instr.Operand = nextTarget;
                                changed = true;
                                continue;
                            }
                        }
                    }
                    
                    // 3. Удаление Nop перед целевой точкой (упрощенно)
                    if (instr.OpCode.Code == Code.Nop && nextInstr != null)
                    {
                        // Можно добавить логику удаления nop, если они не являются целями ветвлений
                        // Пока пропускаем, чтобы не сломать метки
                    }
                }
            }
        }

        private void RemoveDeadCode(MethodDef method, IList<Instruction> instructions)
        {
            // Находим все инструкции, на которые есть переходы (метки)
            var branchTargets = new HashSet<Instruction>();
            foreach (var instr in instructions)
            {
                if (instr.Operand is Instruction target)
                    branchTargets.Add(target);
                else if (instr.Operand is Instruction[] targets)
                {
                    foreach (var t in targets) branchTargets.Add(t);
                }
            }

            bool deadCodeStarted = false;
            for (int i = 0; i < instructions.Count; i++)
            {
                var instr = instructions[i];

                if (deadCodeStarted)
                {
                    // Если инструкция не является целью ветвления, заменяем на Nop
                    if (!branchTargets.Contains(instr))
                    {
                        instr.OpCode = OpCodes.Nop;
                        instr.Operand = null;
                    }
                    continue;
                }

                // Начало мертвого кода после ret/throw
                if (instr.OpCode.FlowControl == FlowControl.Ret || instr.OpCode.FlowControl == FlowControl.Throw)
                {
                    deadCodeStarted = true;
                }
            }
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
            _module.Write(outputPath);
            Console.WriteLine($"[+] Saved to: {outputPath}");
        }

        public void Dispose()
        {
            _aiAssistant?.Dispose();
            _module.Dispose();
        }
    }
}
