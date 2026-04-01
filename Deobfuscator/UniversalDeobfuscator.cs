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

        public UniversalDeobfuscator(string filePath, AiConfig aiConfig)
        {
            _module = ModuleDefMD.Load(filePath);
            _aiAssistant = aiConfig.Enabled ? new AiAssistant(aiConfig) : null;
        }

        public void Deobfuscate()
        {
            Console.WriteLine("[*] Starting deep deobfuscation...");
            int count = 0;
            
            foreach (var type in _module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody || !method.Body.IsIL) continue;

                    try
                    {
                        // Попытка распутать state machine
                        if (UnpackStateMachine(method))
                        {
                            CleanupNops(method);
                            count++;
                        }
                        else
                        {
                            // Если не state machine, пробуем упростить поток
                            SimplifyControlFlow(method);
                            CleanupNops(method);
                        }

                        if (_aiAssistant != null && IsObfuscatedName(method.Name))
                        {
                            RenameWithAi(method);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[!] Error in {method.FullName}: {ex.Message}");
                    }
                }
            }
            Console.WriteLine($"[*] Processed {count} methods.");
        }

        /// <summary>
        /// Распутывает обфускацию типа "State Machine" путем симуляции выполнения.
        /// Мы эмулируем переходы состояний, но собираем только полезные инструкции.
        /// </summary>
        private bool UnpackStateMachine(MethodDef method)
        {
            var body = method.Body;
            if (body == null || !body.HasInstructions) return false;
            var instructions = body.Instructions;
            if (instructions.Count < 5) return false;

            // 1. Поиск переменной состояния (state variable)
            int stateVarIndex = -1;
            object? initialState = null;

            // Ищем паттерн: ldc.X -> stloc.s V_XX
            for (int i = 0; i < Math.Min(10, instructions.Count - 1); i++)
            {
                var ldc = instructions[i];
                var stloc = instructions[i + 1];

                if (IsStloc(stloc, out int idx))
                {
                    var val = GetConstantValue(ldc);
                    if (val != null)
                    {
                        stateVarIndex = idx;
                        initialState = val;
                        break;
                    }
                }
            }

            if (stateVarIndex == -1 || initialState == null)
                return false; // Не похоже на state machine

            // 2. Симуляция выполнения для построения линейного потока
            var resultInstructions = new List<Instruction>();
            var visitedStates = new HashSet<string>(); // Чтобы избежать бесконечных циклов при анализе
            
            // Локальное хранилище для эмуляции значения state-переменной
            object? currentStateValue = initialState;
            
            // Указатель на текущую инструкцию в оригинальном списке
            // Мы будем искать блоки кода динамически, основываясь на значении stateVar
            // Так как структура обфускации обычно: do { if (s==A) ... if (s==B) ... } while (s!=Exit)
            // Нам нужно найти блок, соответствующий текущему значению currentStateValue
            
            int maxIterations = instructions.Count * 10; // Защита от зацикливания
            int iteration = 0;

            while (iteration < maxIterations)
            {
                iteration++;

                // Формируем ключ состояния для проверки циклов
                string stateKey = $"{currentStateValue}";
                if (visitedStates.Contains(stateKey))
                {
                    // Если мы вернулись в то же состояние, скорее всего цикл завершен или это реальный цикл
                    // В контексте этой обфускации повтор состояния обычно означает выход (если логика линейная)
                    break;
                }
                
                // Пытаемся найти блок кода, который выполняется при текущем значении stateVar
                // Ищем конструкцию: ldloc(state), ldc(checkVal), ceq, brtrue target
                Instruction? blockStart = null;
                Instruction? nextStateStore = null;
                object? nextStateValue = null;

                for (int i = 0; i < instructions.Count - 4; i++)
                {
                    var instr1 = instructions[i]; // ldloc
                    var instr2 = instructions[i+1]; // ldc (значение для сравнения)
                    var instr3 = instructions[i+2]; // ceq/cgt/clt
                    var instr4 = instructions[i+3]; // brtrue/brfalse

                    if (GetLocalIndex(instr1) == stateVarIndex &&
                        GetConstantValue(instr2) != null &&
                        (instr3.OpCode.Code == Code.Ceq || instr3.OpCode.Code == Code.Cgt || instr3.OpCode.Code == Code.Clt) &&
                        (instr4.OpCode.Code == Code.Brtrue || instr4.OpCode.Code == Code.Brtrue_S || 
                         instr4.OpCode.Code == Code.Brfalse || instr4.OpCode.Code == Code.Brfalse_S))
                    {
                        var checkVal = GetConstantValue(instr2);
                        
                        // Проверяем, совпадает ли проверяемое значение с текущим состоянием
                        if (Equals(checkVal, currentStateValue))
                        {
                            // Нашли наш блок!
                            blockStart = instr4.Operand as Instruction; // Цель перехода - начало полезного кода
                            
                            // Теперь нужно найти, куда сохраняется следующее состояние внутри этого блока
                            // Обычно это идет после полезного кода: ldc(next), stloc(state)
                            // Сканируем вперед от blockStart
                            if (blockStart != null)
                            {
                                int blkIdx = instructions.IndexOf(blockStart);
                                if (blkIdx != -1)
                                {
                                    for (int k = blkIdx; k < Math.Min(blkIdx + 20, instructions.Count - 1); k++)
                                    {
                                        var curr = instructions[k];
                                        var next = instructions[k+1];

                                        if (IsStloc(next, out int sIdx) && sIdx == stateVarIndex)
                                        {
                                            var nVal = GetConstantValue(curr);
                                            if (nVal != null)
                                            {
                                                nextStateValue = nVal;
                                                nextStateStore = next;
                                                break;
                                            }
                                        }
                                        
                                        // Если встретили безусловный переход вперед (конец блока)
                                        if (curr.OpCode.FlowControl == FlowControl.Branch && curr.Operand is Instruction t)
                                        {
                                            if (t != blockStart && instructions.IndexOf(t) > k) 
                                            {
                                                // Переход на следующую проверку или выход
                                                break;
                                            }
                                        }
                                    }
                                }
                            }
                            break; 
                        }
                    }
                }

                if (blockStart == null)
                {
                    // Не нашли блок для текущего состояния. Возможно, это выход из цикла.
                    break;
                }

                visitedStates.Add(stateKey);

                // 3. Извлечение полезных инструкций из найденного блока
                int startIdx = instructions.IndexOf(blockStart);
                if (startIdx == -1) break;

                int ptr = startIdx;
                while (ptr < instructions.Count)
                {
                    var currInstr = instructions[ptr];

                    // Условия остановки сбора блока:
                    // 1. Достигли инструкции сохранения следующего состояния
                    if (currInstr == nextStateStore) 
                        break;

                    // 2. Безусловный переход вперед (конец блока)
                    if (currInstr.OpCode.FlowControl == FlowControl.Branch && currInstr.Operand is Instruction target)
                    {
                        int tIdx = instructions.IndexOf(target);
                        if (tIdx > ptr) break; // Переход вперед
                    }

                    // 3. Возврат из метода
                    if (currInstr.OpCode.FlowControl == FlowControl.Return)
                    {
                        resultInstructions.Add(CloneInstruction(currInstr));
                        ptr++;
                        break; 
                    }

                    // ФИЛЬТРАЦИЯ МУСОРА:
                    // Пропускаем инструкции, которые являются частью механизма обфускации
                    if (IsObfuscationJunk(currInstr, stateVarIndex))
                    {
                        ptr++;
                        continue;
                    }

                    // Добавляем полезную инструкцию
                    resultInstructions.Add(CloneInstruction(currInstr));

                    ptr++;
                    
                    // Защита от слишком длинных блоков (на всякий случай)
                    if (resultInstructions.Count > 0 && resultInstructions.Count % 100 == 0 && ptr - startIdx > 50) 
                        break;
                }

                // Переходим к следующему состоянию
                if (nextStateValue != null)
                {
                    currentStateValue = nextStateValue;
                }
                else
                {
                    // Если следующего состояния нет, значит вышли
                    break;
                }
            }

            if (resultInstructions.Count == 0) return false;

            // Заменяем тело метода
            ReplaceMethodBody(method, resultInstructions);
            return true;
        }

        /// <summary>
        /// Определяет, является ли инструкция мусором обфускации.
        /// </summary>
        private bool IsObfuscationJunk(Instruction instr, int stateVarIndex)
        {
            // Загрузка переменной состояния
            if (GetLocalIndex(instr) == stateVarIndex && 
               (instr.OpCode.Code == Code.Ldloc || instr.OpCode.Code == Code.Ldloc_S ||
                (instr.OpCode.Code >= Code.Ldloc_0 && instr.OpCode.Code <= Code.Ldloc_3)))
                return true;

            // Загрузка константы (часто используется для сравнения состояний)
            // Осторожно: не удаляем все ldc, только те, что явно ведут к ceq с state var?
            // В данном алгоритме мы уже вырезали блоки по состояниям, поэтому ldc внутри блока
            // могут быть как мусором (остатки проверок), так и реальными константами.
            // Но в типичной обфускации вида "if (num == X)" внутри блока самого сравнения уже нет,
            // оно было до перехода. Однако иногда там остаются ldc для следующего сравнения.
            // Лучший критерий: если за ldc следует ceq, а за ceq следует branch - это мусор.
            // Но здесь мы проверяем одну инструкцию. 
            // Вернемся к простому: ldc сами по себе не мусор, если они аргументы.
            // Мусором считаются ldc, которые были частью условия "ldloc, ldc, ceq".
            // Так как мы перепрыгнули на блок после brtrue, эти инструкции уже позади.
            // Значит, внутри блока ldc обычно легитимны, ЕСЛИ они не часть вложенной проверки состояния.
            
            // Проверка на равенство (ceq) - почти всегда мусор в этом контексте
            if (instr.OpCode.Code == Code.Ceq || instr.OpCode.Code == Code.Cgt || instr.OpCode.Code == Code.Clt ||
                instr.OpCode.Code == Code.Cgt_Un || instr.OpCode.Code == Code.Clt_Un)
                return true;

            // Ветвления (условные и безусловные), используемые для навигации по автомату
            if (instr.OpCode.FlowControl == FlowControl.Cond_Branch || instr.OpCode.FlowControl == FlowControl.Branch)
                return true;

            return false;
        }

        private void SimplifyControlFlow(MethodDef method)
        {
            var body = method.Body;
            if (body == null) return;
            RemoveUnreachableBlocks(method);
        }

        private void RemoveUnreachableBlocks(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            if (instructions.Count == 0) return;

            var reachable = new HashSet<Instruction>();
            var queue = new Queue<Instruction>();

            queue.Enqueue(instructions[0]);
            reachable.Add(instructions[0]);

            while (queue.Count > 0)
            {
                var curr = queue.Dequeue();
                int idx = instructions.IndexOf(curr);
                if (idx == -1) continue;

                if (curr.OpCode.Code != Code.Br && curr.OpCode.Code != Code.Ret && curr.OpCode.Code != Code.Throw)
                {
                    if (idx + 1 < instructions.Count)
                    {
                        var next = instructions[idx + 1];
                        if (reachable.Add(next))
                            queue.Enqueue(next);
                    }
                }

                if (curr.Operand is Instruction target)
                {
                    if (reachable.Add(target))
                        queue.Enqueue(target);
                }
                else if (curr.Operand is Instruction[] targets)
                {
                    foreach (var t in targets)
                        if (reachable.Add(t)) queue.Enqueue(t);
                }
            }

            bool changed = false;
            for (int i = instructions.Count - 1; i >= 0; i--)
            {
                if (!reachable.Contains(instructions[i]))
                {
                    instructions[i].OpCode = OpCodes.Nop;
                    instructions[i].Operand = null;
                    changed = true;
                }
            }

            if (changed)
            {
                method.Body.UpdateInstructionOffsets();
            }
        }

        private void CleanupNops(MethodDef method)
        {
            var body = method.Body;
            var instructions = body.Instructions;
            bool changed = true;

            while (changed)
            {
                changed = false;
                for (int i = instructions.Count - 1; i >= 0; i--)
                {
                    if (instructions[i].OpCode.Code == Code.Nop)
                    {
                        bool isTarget = false;
                        foreach (var instr in instructions)
                        {
                            if (instr.Operand == instructions[i]) { isTarget = true; break; }
                            if (instr.Operand is Instruction[] arr && arr.Contains(instructions[i])) { isTarget = true; break; }
                        }

                        if (!isTarget)
                        {
                            instructions.RemoveAt(i);
                            changed = true;
                        }
                    }
                }
            }
            
            body.UpdateInstructionOffsets();
            body.SimplifyMacros(method.Parameters);
        }

        #region Helpers

        private bool IsStloc(Instruction instr, out int index)
        {
            index = -1;
            if (instr.Operand is Local l) index = l.Index;
            else if (instr.OpCode.Code == Code.Stloc_0) index = 0;
            else if (instr.OpCode.Code == Code.Stloc_1) index = 1;
            else if (instr.OpCode.Code == Code.Stloc_2) index = 2;
            else if (instr.OpCode.Code == Code.Stloc_3) index = 3;

            return index != -1 && (instr.OpCode.Code == Code.Stloc || instr.OpCode.Code == Code.Stloc_S ||
                   instr.OpCode.Code == Code.Stloc_0 || instr.OpCode.Code == Code.Stloc_1 ||
                   instr.OpCode.Code == Code.Stloc_2 || instr.OpCode.Code == Code.Stloc_3);
        }

        private bool IsLdloc(Instruction instr, out int index)
        {
            index = -1;
            if (instr.Operand is Local l) index = l.Index;
            else if (instr.OpCode.Code == Code.Ldloc_0) index = 0;
            else if (instr.OpCode.Code == Code.Ldloc_1) index = 1;
            else if (instr.OpCode.Code == Code.Ldloc_2) index = 2;
            else if (instr.OpCode.Code == Code.Ldloc_3) index = 3;

            return index != -1 && (instr.OpCode.Code == Code.Ldloc || instr.OpCode.Code == Code.Ldloc_S ||
                   instr.OpCode.Code == Code.Ldloc_0 || instr.OpCode.Code == Code.Ldloc_1 ||
                   instr.OpCode.Code == Code.Ldloc_2 || instr.OpCode.Code == Code.Ldloc_3);
        }

        private int GetLocalIndex(Instruction instr)
        {
            if (IsLdloc(instr, out int idx) || IsStloc(instr, out idx)) return idx;
            return -1;
        }

        private object? GetConstantValue(Instruction instr)
        {
            switch (instr.OpCode.Code)
            {
                case Code.Ldc_I4: return instr.Operand as int?;
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
                case Code.Ldc_I8: return instr.Operand as long?;
                case Code.Ldc_R4: return instr.Operand as float?;
                case Code.Ldc_R8: return instr.Operand as double?;
                default: return null;
            }
        }

        private bool IsObfuscatedName(string name) => !string.IsNullOrEmpty(name) && (name.Length == 1 || name.StartsWith("?"));

        private void RenameWithAi(MethodDef method)
        {
            if (_aiAssistant == null) return;
            try
            {
                var snippet = string.Join("\n", method.Body.Instructions.Take(15).Select(x => x.ToString()));
                var newName = _aiAssistant.GetSuggestedName(method.Name, snippet, method.ReturnType?.ToString());
                if (!string.IsNullOrEmpty(newName) && IsValidIdentifier(newName))
                {
                    method.Name = newName;
                    Console.WriteLine($"[AI] Renamed {method.Name}");
                }
            }
            catch { }
        }

        private bool IsValidIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (!char.IsLetter(name[0]) && name[0] != '_') return false;
            foreach (var c in name) if (!char.IsLetterOrDigit(c) && c != '_') return false;
            return true;
        }

        private Instruction CloneInstruction(Instruction orig)
        {
            var opCode = orig.OpCode;
            var operand = orig.Operand;

            if (operand is Local local)
                return Instruction.Create(opCode, local);
            
            if (operand is Parameter param)
                return Instruction.Create(opCode, param);
            
            if (operand is Instruction target)
                return Instruction.Create(opCode, target);
            
            if (operand is Instruction[] targets)
                return Instruction.Create(opCode, targets);
            
            if (operand is string str)
                return Instruction.Create(opCode, str);
            
            if (operand is int i)
                return Instruction.Create(opCode, i);
            
            if (operand is long l)
                return Instruction.Create(opCode, l);
            
            if (operand is float f)
                return Instruction.Create(opCode, f);
            
            if (operand is double d)
                return Instruction.Create(opCode, d);
            
            if (operand is ITypeDefOrRef type)
                return Instruction.Create(opCode, type);
            
            if (operand is MethodDef method)
                return Instruction.Create(opCode, method);
            
            if (operand is FieldDef field)
                return Instruction.Create(opCode, field);
            
            if (operand is MemberRef memberRef)
                return Instruction.Create(opCode, memberRef);
            
            return new Instruction(opCode, operand);
        }

        private void ReplaceMethodBody(MethodDef method, List<Instruction> newInstructions)
        {
            var body = method.Body;
            body.Instructions.Clear();
            foreach (var instr in newInstructions)
            {
                body.Instructions.Add(instr);
            }
            body.UpdateInstructionOffsets();
            body.SimplifyMacros(method.Parameters);
        }

        #endregion

        public void Save(string path)
        {
            Console.WriteLine($"[*] Saving to: {path}");
            var opts = new ModuleWriterOptions(_module)
            {
                Logger = DummyLogger.NoThrowInstance,
                MetadataOptions = new MetadataOptions { Flags = MetadataFlags.KeepOldMaxStack }
            };
            _module.Write(path, opts);
            Console.WriteLine("[+] Done.");
        }

        public void Dispose()
        {
            _aiAssistant?.Dispose();
            _module.Dispose();
        }
    }
}
