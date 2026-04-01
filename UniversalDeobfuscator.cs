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
        private Dictionary<int, object?> _localValues = new Dictionary<int, object?>();

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
                    
                    // Важно: отключаем пересчет MaxStack, так как мы меняем поток управления
                    method.Body.KeepOldMaxStack = true;

                    Console.WriteLine($"[*] Processing: {method.FullName}");

                    // 1. Символьное выполнение и упрощение потока (Goto, num checks)
                    SimplifyControlFlowWithSymbolicExec(method);

                    // 2. Очистка мертвого кода (после упрощения goto)
                    RemoveDeadCode(method);

                    // 3. AI переименование (если включено)
                    if (_aiAssistant != null && method.Name.StartsWith("<") || method.Name.Length == 1)
                    {
                        RenameWithAi(method);
                    }
                }
            }

            Console.WriteLine("[*] Deobfuscation complete.");
        }

        /// <summary>
        /// Символьное выполнение для раскрытия паттернов: num = X; if (num == Y) goto ...
        /// </summary>
        private void SimplifyControlFlowWithSymbolicExec(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            bool changed = true;
            int maxIterations = 50; // Защита от бесконечного цикла
            int iteration = 0;

            while (changed && iteration < maxIterations)
            {
                changed = false;
                iteration++;
                _localValues.Clear();

                // Проходим по инструкциям, эмулируя присваивания констант
                for (int i = 0; i < instructions.Count; i++)
                {
                    var instr = instructions[i];

                    // Отслеживание присваиваний констант локальным переменным (ldc_*. stloc)
                    if (instr.OpCode.Code == Code.Stloc)
                    {
                        var local = instr.Operand as Local;
                        if (local != null && i > 0)
                        {
                            var prev = instructions[i - 1];
                            var val = GetConstantValue(prev);
                            if (val != null)
                            {
                                _localValues[local.Index] = val;
                            }
                        }
                    }
                    // Обработка коротких форм stloc.s, stloc.0 и т.д. (упрощенно считаем, что ldc было перед этим)
                    else if (instr.OpCode.Code >= Code.Stloc_0 && instr.OpCode.Code <= Code.Stloc_3)
                    {
                        int idx = instr.OpCode.Code - Code.Stloc_0;
                        if (i > 0)
                        {
                            var val = GetConstantValue(instructions[i - 1]);
                            if (val != null) _localValues[idx] = val;
                        }
                    }

                    // Упрощение условных переходов, если условие известно
                    if (IsConditionalBranch(instr))
                    {
                        bool? conditionResult = EvaluateCondition(instr, _localValues);
                        
                        if (conditionResult.HasValue)
                        {
                            // Условие стало константой (true/false)
                            Instruction? target = conditionResult.Value 
                                ? (instr.Operand as Instruction) 
                                : FindNextInstruction(instructions, i);

                            if (target != null)
                            {
                                // Заменяем условный переход на безусловный или nop
                                if (conditionResult.Value)
                                {
                                    instr.OpCode = OpCodes.Br;
                                }
                                else
                                {
                                    instr.OpCode = OpCodes.Nop;
                                    instr.Operand = null;
                                }
                                changed = true;
                            }
                        }
                    }

                    // Распутывание цепочек безусловных переходов (goto L1; L1: goto L2)
                    if (instr.OpCode.Code == Code.Br && instr.Operand is Instruction targetInstr)
                    {
                        // Ищем индекс целевой инструкции
                        int targetIndex = instructions.IndexOf(targetInstr);
                        if (targetIndex != -1 && targetIndex < instructions.Count)
                        {
                            var nextInstr = instructions[targetIndex];
                            // Если цель сразу ведет дальше (например, nop или другой br)
                            if (nextInstr.OpCode.Code == Code.Br && nextInstr.Operand is Instruction finalTarget)
                            {
                                instr.Operand = finalTarget;
                                changed = true;
                            }
                            // Если цель - это следующая инструкция по порядку, убираем goto
                            if (targetIndex == i + 1)
                            {
                                instr.OpCode = OpCodes.Nop;
                                instr.Operand = null;
                                changed = true;
                            }
                        }
                    }
                }
                
                // После изменений нужно обновить оффсеты, чтобы dnlib не ругался
                if (changed)
                {
                    method.Body.UpdateInstructionOffsets();
                }
            }
        }

        private bool IsConditionalBranch(Instruction instr)
        {
            return instr.OpCode.FlowControl == FlowControl.Cond_Branch;
        }

        private Instruction? FindNextInstruction(IList<Instruction> instructions, int currentIndex)
        {
            if (currentIndex + 1 < instructions.Count)
                return instructions[currentIndex + 1];
            return null;
        }

        /// <summary>
        /// Вычисляет результат условия, операнды которого могут быть константами или известными локалями.
        /// </summary>
        private bool? EvaluateCondition(Instruction condInstr, Dictionary<int, object?> knownLocals)
        {
            // В dnlib условный переход обычно сравнивает два значения со стека.
            // Нам нужно заглянуть назад, чтобы найти, что лежало на стеке.
            // Это упрощенная реализация для паттерна: ldc / ldloc -> ldc / ldloc -> ceq/clt -> br
            
            int index = method.Body.Instructions.IndexOf(condInstr);
            if (index < 2) return null;

            var instructions = method.Body.Instructions;
            
            // Пытаемся найти операнды сравнения (идем назад от ветвления)
            // Пропускаем само сравнение (ceq, clt, bgt и т.д.), если оно есть перед br, 
            // но в dnlib br часто содержит результат вычисления флагов неявно или явно через предшествующую инструкцию.
            // Однако в обфускаторах часто бывает: ldloc.0, ldc.i4.1, ceq, brtrue.
            
            // Найдем инструкцию сравнения перед branch
            Instruction? cmpInstr = null;
            int searchIdx = index - 1;
            
            // Ищем инструкцию сравнения сразу перед ветвлением
            if (IsComparisonOp(instructions[searchIdx].OpCode.Code))
            {
                cmpInstr = instructions[searchIdx];
                searchIdx--;
            }

            if (cmpInstr == null) return null;

            // Теперь ищем два операнда перед сравнением
            // Определяем количество аргументов операции сравнения (обычно 2)
            int argsNeeded = 2; 
            object? val2 = GetStackValue(instructions, searchIdx, ref searchIdx, knownLocals);
            object? val1 = GetStackValue(instructions, searchIdx, ref searchIdx, knownLocals);

            if (val1 == null || val2 == null) return null;

            return PerformComparison(val1, val2, cmpInstr.OpCode.Code, condInstr.OpCode.Code);
        }

        private object? GetStackValue(IList<Instruction> instructions, int currentIndex, ref int outIndex, Dictionary<int, object?> knownLocals)
        {
            while (currentIndex >= 0)
            {
                var instr = instructions[currentIndex];
                outIndex = currentIndex - 1;

                // Пропускаем dup, pop и прочий мусор, если нужно (упрощенно)
                
                if (instr.OpCode.Code == Code.Ldloc || instr.OpCode.Code == Code.Ldloc_S)
                {
                    var local = instr.Operand as Local;
                    if (local != null && knownLocals.ContainsKey(local.Index))
                        return knownLocals[local.Index];
                }
                else if (instr.OpCode.Code >= Code.Ldloc_0 && instr.OpCode.Code <= Code.Ldloc_3)
                {
                    int idx = instr.OpCode.Code - Code.Ldloc_0;
                    if (knownLocals.ContainsKey(idx)) return knownLocals[idx];
                }
                else
                {
                    var val = GetConstantValue(instr);
                    if (val != null) return val;
                }
                
                currentIndex--;
            }
            return null;
        }

        private bool IsComparisonOp(Code code)
        {
            return code == Code.Ceq || code == Code.Clt || code == Code.Clt_Un || code == Code.Cgt || code == Code.Cgt_Un;
        }

        private bool? PerformComparison(object? v1, object? v2, Code cmpOp, Code branchOp)
        {
            if (v1 == null || v2 == null) return null;

            bool result = false;

            // Приводим к общему типу (double для float/double, long для int/long)
            if (v1 is double d1 && v2 is double d2)
            {
                if (cmpOp == Code.Ceq) result = d1 == d2;
                else if (cmpOp == Code.Clt) result = d1 < d2;
                else if (cmpOp == Code.Cgt) result = d1 > d2;
            }
            else if (v1 is long l1 && v2 is long l2)
            {
                if (cmpOp == Code.Ceq) result = l1 == l2;
                else if (cmpOp == Code.Clt) result = l1 < l2;
                else if (cmpOp == Code.Cgt) result = l1 > l2;
            }
            else if (v1 is int i1 && v2 is int i2)
            {
                if (cmpOp == Code.Ceq) result = i1 == i2;
                else if (cmpOp == Code.Clt) result = i1 < i2;
                else if (cmpOp == Code.Cgt) result = i1 > i2;
            }
            else
            {
                // Попытка приведения
                double dv1 = Convert.ToDouble(v1);
                double dv2 = Convert.ToDouble(v2);
                if (cmpOp == Code.Ceq) result = dv1 == dv2;
                else if (cmpOp == Code.Clt) result = dv1 < dv2;
                else if (cmpOp == Code.Cgt) result = dv1 > dv2;
            }

            // Учитываем тип ветвления (brtrue, brfalse, br.s и т.д.)
            // В dnlib условные ветвления (brtrue, brfalse) имеют Operand = target.
            // Если мы вычислили условие ceq/clt, то brtrue срабатывает если result=true.
            // Но в нашем случае мы анализируем последовательность: ... ceq, brtrue target.
            // Если opcode ветвления - это просто условный переход (Cond_Branch), он зависит от вершины стека.
            // Мы уже вычислили вершину стека (result).
            
            // Особый случай: brfalse срабатывает если 0/false
            string opName = branchOp.Name.ToLower();
            if (opName.Contains("false"))
                return !result;
            
            return result;
        }

        private object? GetConstantValue(Instruction? instr)
        {
            if (instr == null) return null;

            switch (instr.OpCode.Code)
            {
                case Code.Ldc_I4: return instr.Operand is int ? (int?)instr.Operand : null;
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
                case Code.Ldc_R4: return instr.Operand is float ? (double?)(float)instr.Operand : null;
                case Code.Ldc_R8: return instr.Operand is double ? (double?)instr.Operand : null;
                case Code.Ldc_I8: return instr.Operand is long ? (long?)instr.Operand : null;
                default: return null;
            }
        }

        private void RemoveDeadCode(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            var reachable = new HashSet<Instruction>();
            
            // Простой анализ достижимости
            var queue = new Queue<Instruction>();
            if (instructions.Count > 0)
            {
                queue.Enqueue(instructions[0]);
                reachable.Add(instructions[0]);
            }

            while (queue.Count > 0)
            {
                var curr = queue.Dequeue();
                int idx = instructions.IndexOf(curr);
                if (idx == -1) continue;

                // Если это не безусловный переход и не ret/throw, следующая инструкция достижима
                if (curr.OpCode.Code != Code.Br && curr.OpCode.Code != Code.Ret && curr.OpCode.Code != Code.Throw)
                {
                    if (idx + 1 < instructions.Count)
                    {
                        var next = instructions[idx + 1];
                        if (!reachable.Contains(next))
                        {
                            reachable.Add(next);
                            queue.Enqueue(next);
                        }
                    }
                }

                // Целевая инструкция перехода достижима
                if (curr.Operand is Instruction target)
                {
                    if (!reachable.Contains(target))
                    {
                        reachable.Add(target);
                        queue.Enqueue(target);
                    }
                }
                else if (curr.OpCode.Code == Code.Switch && curr.Operand is Instruction[] targets)
                {
                    foreach (var t in targets)
                    {
                        if (!reachable.Contains(t))
                        {
                            reachable.Add(t);
                            queue.Enqueue(t);
                        }
                    }
                }
            }

            // Заменяем недостижимые инструкции на Nop
            for (int i = 0; i < instructions.Count; i++)
            {
                if (!reachable.Contains(instructions[i]))
                {
                    instructions[i].OpCode = OpCodes.Nop;
                    instructions[i].Operand = null;
                }
            }
            
            method.Body.UpdateInstructionOffsets();
        }

        private void RenameWithAi(MethodDef method)
        {
            if (_aiAssistant == null) return;

            try
            {
                string ilCode = string.Join("\n", method.Body.Instructions.Take(30).Select(x => x.ToString()));
                string suggested = _aiAssistant.GetSuggestedName(method.Name, ilCode, method.ReturnType?.ToString() ?? "void");
                
                if (!string.IsNullOrEmpty(suggested) && suggested != method.Name && suggested.Length > 1)
                {
                    method.Name = suggested;
                    Console.WriteLine($"[AI] Renamed to: {suggested}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AI Error]: {ex.Message}");
            }
        }

        public void Save(string outputPath)
        {
            try
            {
                var options = new ModuleWriterOptions(_module)
                {
                    Logger = DummyLogger.NoThrowInstance,
                    MetadataOptions = new MetadataOptions
                    {
                        Flags = MetadataFlags.KeepOldMaxStack | MetadataFlags.PreserveAll
                    }
                };
                
                _module.Write(outputPath, options);
                Console.WriteLine($"[+] Saved to: {outputPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error saving]: {ex.Message}");
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
