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
                        // 1. Попытка распутать State Machine
                        if (UnpackStateMachine(method))
                        {
                            CleanupNops(method);
                            count++;
                            Console.WriteLine($"[+] Unpacked: {method.FullName}");
                        }
                        else
                        {
                            // 2. Если не State Machine, пробуем упростить поток (constant folding)
                            SimplifyControlFlow(method);
                            CleanupNops(method);
                        }

                        // 3. Переименование через ИИ (если включено)
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
        /// Распутывает обфускацию типа "State Machine".
        /// Алгоритм: Находит переменную состояния -> Эмулирует переходы -> Собирает полезные инструкции.
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

            for (int i = 0; i < Math.Min(15, instructions.Count - 1); i++)
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
                return false;

            // 2. Эмуляция выполнения для построения линейного потока
            var resultInstructions = new List<Instruction>();
            var visitedStates = new HashSet<string>();
            
            object? currentStateValue = initialState;
            int maxIterations = instructions.Count * 20; 
            int iteration = 0;

            while (iteration < maxIterations)
            {
                iteration++;
                string stateKey = $"{currentStateValue}";
                
                // Защита от зацикливания (если состояние повторилось, выходим)
                if (visitedStates.Contains(stateKey))
                    break;
                
                visitedStates.Add(stateKey);

                // Поиск блока кода для текущего состояния
                // Паттерн: ldloc(state), ldc(checkVal), ceq, brtrue.s TARGET
                Instruction? blockTarget = null;
                object? nextStateValue = null;

                for (int i = 0; i < instructions.Count - 4; i++)
                {
                    var instr1 = instructions[i]; // ldloc
                    var instr2 = instructions[i+1]; // ldc (значение проверки)
                    var instr3 = instructions[i+2]; // ceq
                    var instr4 = instructions[i+3]; // brtrue

                    if (GetLocalIndex(instr1) == stateVarIndex &&
                        GetConstantValue(instr2) != null &&
                        (instr3.OpCode.Code == Code.Ceq || instr3.OpCode.Code == Code.Cgt || instr3.OpCode.Code == Code.Clt) &&
                        (instr4.OpCode.FlowControl == FlowControl.Cond_Branch))
                    {
                        var checkVal = GetConstantValue(instr2);
                        
                        if (Equals(checkVal, currentStateValue))
                        {
                            blockTarget = instr4.Operand as Instruction;
                            
                            // Поиск следующего состояния внутри этого блока
                            if (blockTarget != null)
                            {
                                int blkIdx = instructions.IndexOf(blockTarget);
                                if (blkIdx != -1)
                                {
                                    // Сканируем блок вперед в поисках "stloc(state)"
                                    for (int k = blkIdx; k < Math.Min(blkIdx + 30, instructions.Count - 1); k++)
                                    {
                                        var curr = instructions[k];
                                        var next = (k + 1 < instructions.Count) ? instructions[k+1] : null;

                                        // Нашли обновление состояния: ldc.X, stloc.s(state)
                                        if (next != null && IsStloc(next, out int sIdx) && sIdx == stateVarIndex)
                                        {
                                            var nVal = GetConstantValue(curr);
                                            if (nVal != null)
                                            {
                                                nextStateValue = nVal;
                                                break;
                                            }
                                        }
                                        
                                        // Если встретили безусловный переход вперед (конец блока)
                                        if (curr.OpCode.Code == Code.Br || curr.OpCode.Code == Code.Br_S)
                                        {
                                            if (curr.Operand is Instruction t)
                                            {
                                                int tIdx = instructions.IndexOf(t);
                                                if (tIdx > k) break; // Переход на следующую проверку
                                            }
                                        }
                                    }
                                }
                            }
                            break; 
                        }
                    }
                }

                if (blockTarget == null)
                {
                    // Блок не найден. Возможно, это выход из метода или конец цикла.
                    // Проверяем, нет ли здесь инструкций возврата, которые мы могли пропустить
                    break;
                }

                // 3. Копирование полезных инструкций из блока
                int startIdx = instructions.IndexOf(blockTarget);
                if (startIdx == -1) break;

                int ptr = startIdx;
                while (ptr < instructions.Count)
                {
                    var currInstr = instructions[ptr];

                    // Стоп-условия:
                    // 1. Достигли инструкции обновления состояния (ldc перед stloc) - она будет обработана в следующем цикле как переход
                    // Но нам нужно найти именно момент перехода. 
                    // Проще: идем до безусловного перехода вперед или до ret.
                    
                    if (currInstr.OpCode.FlowControl == FlowControl.Return)
                    {
                        resultInstructions.Add(CloneInstruction(currInstr));
                        break; // Конец метода
                    }

                    if (currInstr.OpCode.Code == Code.Br || currInstr.OpCode.Code == Code.Br_S)
                    {
                        if (currInstr.Operand is Instruction t)
                        {
                            int tIdx = instructions.IndexOf(t);
                            if (tIdx > ptr) 
                                break; // Переход вперед (к следующей проверке состояния)
                            // Если переход назад (цикл внутри блока) - оставляем его (редко, но бывает)
                        }
                    }

                    // ФИЛЬТР МУСОРА ОБФУСКАЦИИ
                    if (IsJunkInstruction(currInstr, stateVarIndex))
                    {
                        ptr++;
                        continue;
                    }

                    // Добавляем полезную инструкцию
                    resultInstructions.Add(CloneInstruction(currInstr));
                    ptr++;
                }

                // Переход к следующему состоянию
                if (nextStateValue != null)
                {
                    currentStateValue = nextStateValue;
                }
                else
                {
                    // Если следующее состояние не найдено явно, проверяем, не вышли ли мы из метода
                    // Или прерываем цикл, если застряли
                    break;
                }
            }

            if (resultInstructions.Count == 0) return false;

            ReplaceMethodBody(method, resultInstructions);
            return true;
        }

        /// <summary>
        /// Определяет, является ли инструкция мусором обфускации.
        /// Важно: НЕ удаляем вызовы методов, арифметику, работу с другими локалями.
        /// </summary>
        private bool IsJunkInstruction(Instruction instr, int stateVarIndex)
        {
            // 1. Чтение переменной состояния (ldloc state)
            if (GetLocalIndex(instr) == stateVarIndex && 
               (instr.OpCode.Code == Code.Ldloc || instr.OpCode.Code == Code.Ldloc_S ||
                (instr.OpCode.Code >= Code.Ldloc_0 && instr.OpCode.Code <= Code.Ldloc_3)))
                return true;

            // 2. Операции сравнения (ceq, cgt, clt) - часть механизма переключения
            if (instr.OpCode.Code == Code.Ceq || instr.OpCode.Code == Code.Cgt || instr.OpCode.Code == Code.Clt ||
                instr.OpCode.Code == Code.Cgt_Un || instr.OpCode.Code == Code.Clt_Un)
                return true;

            // 3. Условные переходы (brtrue, brfalse) - механизм переключения
            if (instr.OpCode.FlowControl == FlowControl.Cond_Branch)
                return true;

            // 4. Безусловные переходы (br) - часто используются для прыжков между проверками
            // Осторожно: если это реальный goto в коде, мы можем его сломать.
            // Но в state machine обфускации все br обычно служебные.
            if (instr.OpCode.Code == Code.Br || instr.OpCode.Code == Code.Br_S)
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

        #region Helpers & AI

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

            return index != -1 && (instr.OpCode.Code == Code.Ldloc || instr.Op.Code == Code.Ldloc_S ||
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

        private bool IsObfuscatedName(string name) => !string.IsNullOrEmpty(name) && (name.Length == 1 || name.StartsWith("?") || name.Length == 2 && char.IsSurrogate(name[0]));

        private void RenameWithAi(MethodDef method)
        {
            if (_aiAssistant == null) return;
            try
            {
                // Берем первые 20 инструкций для контекста
                var snippet = string.Join("\n", method.Body.Instructions.Take(20).Select(x => x.ToString()));
                var returnType = method.ReturnType?.ToString() ?? "void";
                
                var newName = _aiAssistant.GetSuggestedName(method.Name, snippet, returnType);
                
                if (!string.IsNullOrEmpty(newName) && IsValidIdentifier(newName) && newName != method.Name)
                {
                    method.Name = newName;
                    Console.WriteLine($"[AI] Renamed {method.Name} -> {newName}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AI] Error renaming {method.Name}: {ex.Message}");
            }
        }

        private bool IsValidIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            // Разрешаем буквы, цифры и подчеркивание, включая Unicode буквы (для местных языков)
            if (!char.IsLetter(name[0]) && name[0] != '_') return false;
            foreach (var c in name) 
                if (!char.IsLetterOrDigit(c) && c != '_') return false;
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
            if (operand is MethodDef md)
                return Instruction.Create(opCode, md);
            if (operand is FieldDef fd)
                return Instruction.Create(opCode, fd);
            if (operand is MemberRef mr)
                return Instruction.Create(opCode, mr);
            
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
