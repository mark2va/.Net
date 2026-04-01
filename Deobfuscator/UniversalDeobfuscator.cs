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
                    
                    // 1. Сбор констант
                    AnalyzeConstants(method);

                    // 2. Упрощение условий
                    SimplifyConstantConditions(method);

                    // 3. Распутывание потоков (Goto, циклы)
                    UnravelControlFlow(method);

                    // 4. Удаление мертвого кода
                    RemoveDeadCode(method);

                    // 5. AI Переименование (если включено)
                    if (_aiAssistant != null && method.Name.StartsWith("<"))
                    {
                        RenameWithAi(method);
                    }
                }
            }
            
            Console.WriteLine("[*] Deobfuscation complete.");
        }

        // Вспомогательный метод для получения предыдущей инструкции
        private Instruction? GetPreviousInstruction(IList<Instruction> instructions, Instruction current)
        {
            for (int i = 0; i < instructions.Count; i++)
            {
                if (instructions[i] == current && i > 0)
                {
                    return instructions[i - 1];
                }
            }
            return null;
        }

        private void AnalyzeConstants(MethodDef method)
        {
            _constantLocals.Clear();
            var instructions = method.Body.Instructions;

            foreach (var instr in instructions)
            {
                Local? local = null;

                // Обработка всех вариантов сохранения в локаль (stloc, stloc.s, stloc.0-3)
                if (instr.OpCode.Code == Code.Stloc)
                {
                    local = instr.Operand as Local;
                }
                else if (instr.OpCode.Code == Code.Stloc_S)
                {
                    local = instr.Operand as Local;
                }
                else if (instr.OpCode.Code >= Code.Stloc_0 && instr.OpCode.Code <= Code.Stloc_3)
                {
                    int index = instr.OpCode.Code - Code.Stloc_0;
                    if (index < method.Body.Variables.Count)
                        local = method.Body.Variables[index];
                }

                if (local != null)
                {
                    // Ищем предыдущую инструкцию загрузки константы
                    var prev = GetPreviousInstruction(instructions, instr);
                    if (prev != null)
                    {
                        object? val = GetConstantValue(prev);
                        if (val != null)
                            _constantLocals[local] = val;
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
                    return instr.Operand is int i ? i : (instr.Operand is int? ii ? ii : null);
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

        private void SimplifyConstantConditions(MethodDef method)
        {
            // Здесь можно добавить логику замены ветвлений на основе _constantLocals
            // Для примера пока заглушка, так как полная реализация требует анализа стека
            Console.WriteLine($"[*] Found {_constantLocals.Count} constant locals in {method.Name}");
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

                    // Удаление цепочек goto: goto L1; L1: goto L2; -> goto L2;
                    if (instr.OpCode.Code == Code.Br && instr.Operand is Instruction target)
                    {
                        if (target.OpCode.Code == Code.Br && target.Operand is Instruction nextTarget)
                        {
                            instr.Operand = nextTarget;
                            changed = true;
                        }
                        else if (IsNopBlock(target))
                        {
                            // Пропуск блоков nop
                            var realTarget = FindRealTarget(target);
                            if (realTarget != null)
                            {
                                instr.Operand = realTarget;
                                changed = true;
                            }
                        }
                    }
                    
                    // Удаление безусловных переходов на следующую инструкцию
                    if (instr.OpCode.Code == Code.Br && instr.Operand is Instruction nextInstr && nextInstr == instr.Next)
                    {
                        instr.OpCode = OpCodes.Nop;
                        instr.Operand = null;
                        changed = true;
                    }
                }
                
                // Обновляем список инструкций после изменений (dnlib требует пересчета)
                if (changed)
                    method.Body.SimplifyMacros();
            }
        }

        private bool IsNopBlock(Instruction instr)
        {
            int count = 0;
            var curr = instr;
            while (curr != null && curr.OpCode.Code == Code.Nop && count < 10)
            {
                curr = curr.Next;
                count++;
            }
            return count > 0 && curr != null;
        }

        private Instruction? FindRealTarget(Instruction start)
        {
            var curr = start;
            while (curr != null && curr.OpCode.Code == Code.Nop)
            {
                curr = curr.Next;
            }
            return curr;
        }

        private void RemoveDeadCode(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            for (int i = instructions.Count - 1; i >= 0; i--)
            {
                var instr = instructions[i];
                if (instr.OpCode.FlowControl == FlowControl.Ret || instr.OpCode.FlowControl == FlowControl.Throw)
                {
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
                    Console.WriteLine($"[AI] Renamed {method.Name}");
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
