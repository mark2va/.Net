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
        private readonly bool _debugMode;
        private readonly string _logPath;
        private int _indentLevel = 0;
        private StreamWriter? _logWriter;

        public UniversalDeobfuscator(string filePath, AiConfig aiConfig, bool debugMode = false)
        {
            _module = ModuleDefMD.Load(filePath);
            _aiAssistant = aiConfig.Enabled ? new AiAssistant(aiConfig) : null;
            _debugMode = debugMode;
            
            // Лог сохраняем рядом с файлом
            string dir = Path.GetDirectoryName(filePath) ?? ".";
            string fileName = Path.GetFileNameWithoutExtension(filePath);
            _logPath = Path.Combine(dir, $"{fileName}_deob_log.txt");
        }

        private void Log(string message)
        {
            if (!_debugMode) return;
            string indent = new string(' ', _indentLevel * 2);
            string logLine = $"[DEBUG] {indent}{message}";
            Console.WriteLine(logLine);
            _logWriter?.WriteLine(logLine);
        }

        public void Deobfuscate()
        {
            if (_debugMode)
            {
                _logWriter = new StreamWriter(_logPath, false);
                Log("=== Deobfuscation Started ===");
            }

            Console.WriteLine("[*] Starting deep deobfuscation...");
            int count = 0;

            foreach (var type in _module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody || !method.Body.HasInstructions) continue;

                    try
                    {
                        if (_debugMode)
                        {
                            Log($"Processing: {method.FullName}");
                            _indentLevel++;
                        }

                        if (UnpackStateMachine(method))
                        {
                            // После распаковки делаем несколько проходов очистки
                            CleanupPass(method); // Удаление NOP и мусора
                            FixStackImbalance(method); // Критично: убираем висячие константы
                            CleanupPass(method); // Финальная уборка
                            
                            count++;
                            if (_debugMode) Log("Method unpacked and cleaned.");
                        }
                        else
                        {
                            if (_debugMode) Log("Not a state machine. Trying simple simplification.");
                            SimplifyConditions(method);
                            CleanupPass(method);
                        }

                        if (_aiAssistant != null && IsObfuscatedName(method.Name))
                        {
                            RenameWithAi(method);
                        }

                        if (_debugMode) _indentLevel--;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[!] Error in {method.FullName}: {ex.Message}");
                        if (_debugMode) Log($"Exception: {ex}");
                        if (_debugMode) _indentLevel--;
                    }
                }
            }
            Console.WriteLine($"[*] Processed {count} methods.");
            
            if (_debugMode)
            {
                Log("=== Deobfuscation Finished ===");
                _logWriter?.Close();
                Console.WriteLine($"[*] Debug log saved to: {_logPath}");
            }
        }

        private bool UnpackStateMachine(MethodDef method)
        {
            var body = method.Body;
            var instructions = body.Instructions;
            if (instructions.Count < 5) return false;

            Log($"Analyzing {instructions.Count} instructions...");

            // --- PASS 1: ПОИСК СТРУКТУРЫ ---
            int stateVarIndex = -1;
            object? initialState = null;

            for (int i = 0; i < Math.Min(15, instructions.Count - 1); i++)
            {
                if (IsStloc(instructions[i + 1], out int idx))
                {
                    var val = GetConstantValue(instructions[i]);
                    if (val != null)
                    {
                        stateVarIndex = idx;
                        initialState = val;
                        Log($"Found state var: V_{stateVarIndex}, Init: {initialState}");
                        break;
                    }
                }
            }

            if (stateVarIndex == -1) return false;

            // --- PASS 2: ПОСТРОЕНИЕ ГРАФА И СБОР ИНСТРУКЦИЙ ---
            var stateBlocks = new Dictionary<object, List<Instruction>>();
            var transitions = new Dictionary<object, object?>();
            var junkInstructions = new HashSet<Instruction>(); // Множество мусорных инструкций

            Log("Building transition graph...");

            for (int i = 0; i < instructions.Count - 4; i++)
            {
                var i1 = instructions[i];
                var i2 = instructions[i+1];
                var i3 = instructions[i+2];
                var i4 = instructions[i+3];

                if (GetLocalIndex(i1) == stateVarIndex &&
                    GetConstantValue(i2) is object checkVal &&
                    (i3.OpCode.Code == Code.Ceq || i3.OpCode.Code == Code.Cgt || i3.OpCode.Code == Code.Clt) &&
                    (i4.OpCode.FlowControl == FlowControl.Cond_Branch))
                {
                    var target = i4.Operand as Instruction;
                    if (target != null)
                    {
                        // Помечаем механизм переключения как мусор
                        junkInstructions.Add(i1); // ldloc
                        junkInstructions.Add(i2); // ldc
                        junkInstructions.Add(i3); // ceq
                        junkInstructions.Add(i4); // br

                        var block = ExtractBlockPayload(instructions, target, stateVarIndex, junkInstructions, out object? nextVal);
                        
                        if (!stateBlocks.ContainsKey(checkVal))
                        {
                            stateBlocks[checkVal] = block;
                            transitions[checkVal] = nextVal;
                            Log($"State {checkVal} -> Next: {nextVal}, Payload size: {block.Count}");
                        }
                    }
                }
            }

            if (stateBlocks.Count == 0) return false;

            // --- PASS 3: СБОРКА ЛИНЕЙНОГО ПОТОКА ---
            var finalInstructions = new List<Instruction>();
            var visited = new HashSet<object?>();
            var queue = new Queue<object?>();
            if (initialState != null) queue.Enqueue(initialState);

            Log("Reconstructing flow...");

            while (queue.Count > 0)
            {
                var state = queue.Dequeue();
                if (visited.Contains(state)) continue;
                visited.Add(state);

                if (stateBlocks.TryGetValue(state, out var block))
                {
                    foreach (var ins in block)
                        finalInstructions.Add(CloneInstruction(ins));
                }

                if (transitions.TryGetValue(state, out var next) && next != null && !visited.Contains(next))
                    queue.Enqueue(next);
            }

            if (finalInstructions.Count == 0) return false;

            Log($"Reconstructed {finalInstructions.Count} instructions.");
            ReplaceMethodBody(method, finalInstructions);
            return true;
        }

        /// <summary>
        /// Извлекает полезную нагрузку блока, игнорируя вложенный мусор.
        /// Важно: НЕ добавляет ldc/stloc переменной состояния в результат.
        /// </summary>
        private List<Instruction> ExtractBlockPayload(IList<Instruction> all, Instruction start, int stateVarIdx, HashSet<Instruction> junkSet, out object? nextState)
        {
            var payload = new List<Instruction>();
            nextState = null;
            int startIdx = all.IndexOf(start);
            if (startIdx == -1) return payload;

            int ip = startIdx;
            int limit = 100; // Защита
            int count = 0;

            while (ip < all.Count && count < limit)
            {
                var instr = all[ip];
                count++;

                // Стоп-условия: выход из блока
                if (instr.OpCode.FlowControl == FlowControl.Return)
                {
                    payload.Add(CloneInstruction(instr));
                    break;
                }
                
                if (instr.OpCode.FlowControl == FlowControl.Branch)
                {
                    if (instr.Operand is Instruction t)
                    {
                        if (all.IndexOf(t) > ip) break; // Переход вперед (конец блока)
                    }
                }

                // Проверка на обновление состояния (конец текущего шага)
                if (ip + 1 < all.Count)
                {
                    var nextIns = all[ip+1];
                    if (IsStloc(nextIns, out int sIdx) && sIdx == stateVarIdx)
                    {
                        var val = GetConstantValue(instr);
                        if (val != null)
                        {
                            nextState = val;
                            junkSet.Add(instr); // ldc перед stloc - мусор
                            junkSet.Add(nextIns); // stloc - мусор
                            break; 
                        }
                    }
                }

                // Фильтрация мусора внутри блока
                bool isJunk = false;
                
                // 1. Чтение/Запись переменной состояния
                if (GetLocalIndex(instr) == stateVarIdx) isJunk = true;
                
                // 2. Сравнения и ветвления (они уже учтены в графе)
                if (instr.OpCode.Code == Code.Ceq || instr.OpCode.Code == Code.Cgt || instr.OpCode.Code == Code.Clt) isJunk = true;
                if (instr.OpCode.FlowControl == FlowControl.Cond_Branch) isJunk = true;

                if (isJunk)
                {
                    junkSet.Add(instr);
                }
                else
                {
                    payload.Add(instr);
                }

                ip++;
            }
            return payload;
        }

        /// <summary>
        /// Очищает NOP и недостижимый код.
        /// </summary>
        private void CleanupPass(MethodDef method)
        {
            RemoveUnreachableBlocks(method);
            RemoveNops(method);
        }

        /// <summary>
        /// КРИТИЧЕСКИЙ МЕТОД: Исправляет дисбаланс стека.
        /// Удаляет инструкции загрузки констант, если они не используются следующей операцией.
        /// Это устраняет ошибку "Basic block has to end with unconditional control flow".
        /// </summary>
        private void FixStackImbalance(MethodDef method)
        {
            var body = method.Body;
            var instrs = body.Instructions;
            if (instrs.Count == 0) return;

            bool changed = true;
            while (changed)
            {
                changed = false;
                // Простой анализ: если ldc идет перед другой ldc, ret, или branch, и между ними нет потребления - удаляем
                for (int i = 0; i < instrs.Count - 1; i++)
                {
                    var curr = instrs[i];
                    var next = instrs[i+1];

                    if (IsPushConstant(curr))
                    {
                        bool isConsumed = IsConsumedBy(next);
                        
                        // Если не потребляется следующей инструкцией
                        if (!isConsumed)
                        {
                            // Проверяем, не является ли это аргументом для вызова через пару инструкций?
                            // Для простоты: если следующая инструкция тоже пушит константу или возвращает управление - это мусор
                            if (IsPushConstant(next) || 
                                next.OpCode.FlowControl == FlowControl.Return || 
                                next.OpCode.FlowControl == FlowControl.Branch ||
                                next.OpCode.Code == Code.Pop)
                            {
                                Log($"Removing stack junk: {curr}");
                                curr.OpCode = OpCodes.Nop;
                                curr.Operand = null;
                                changed = true;
                            }
                        }
                    }
                }
                if (changed) RemoveNops(method);
            }
        }

        private bool IsPushConstant(Instruction i)
        {
            return i.OpCode.Code == Code.Ldc_I4 || i.OpCode.Code == Code.Ldc_I4_0 || i.OpCode.Code == Code.Ldc_I4_1 ||
                   i.OpCode.Code == Code.Ldc_I4_2 || i.OpCode.Code == Code.Ldc_I4_3 || i.OpCode.Code == Code.Ldc_I4_4 ||
                   i.OpCode.Code == Code.Ldc_I4_5 || i.OpCode.Code == Code.Ldc_I4_6 || i.OpCode.Code == Code.Ldc_I4_7 ||
                   i.OpCode.Code == Code.Ldc_I4_8 || i.OpCode.Code == Code.Ldc_I4_M1 ||
                   i.OpCode.Code == Code.Ldc_I8 || i.OpCode.Code == Code.Ldc_R4 || i.OpCode.Code == Code.Ldc_R8;
        }

        private bool IsConsumedBy(Instruction i)
        {
            // Инструкции, которые потребляют значение со стека
            switch (i.OpCode.StackBehaviourPop)
            {
                case StackBehaviour.Pop1:
                case StackBehaviour.Pop1_pop1:
                case StackBehaviour.Pop1_pop1_pop1: // Может отсутствовать в старых версиях, но есть в новых
                case StackBehaviour.Pop1_pop1_branch:
                case StackBehaviour.Pop1_branch:
                case StackBehaviour.PopAll:
                    return true;
                default:
                    return false;
            }
        }

        private void SimplifyConditions(MethodDef method)
        {
            // Упрощенная логика для не-state-machine методов
            var body = method.Body;
            var instrs = body.Instructions;
            bool changed = true;
            int pass = 0;
            while (changed && pass < 10)
            {
                changed = false;
                pass++;
                var known = new Dictionary<int, object?>();
                for (int i = 0; i < instrs.Count; i++)
                {
                    if (i + 1 < instrs.Count && IsStloc(instrs[i+1], out int idx))
                    {
                        var v = GetConstantValue(instrs[i]);
                        if (v != null) known[idx] = v;
                    }
                }

                for (int i = 0; i < instrs.Count; i++)
                {
                    var br = instrs[i];
                    if (br.OpCode.FlowControl == FlowControl.Cond_Branch)
                    {
                        // Попытка вычислить условие (упрощенно)
                        // ... (логика аналогична предыдущей версии) ...
                    }
                }
                if (changed) body.UpdateInstructionOffsets();
            }
            RemoveUnreachableBlocks(method);
        }

        private void RemoveUnreachableBlocks(MethodDef method)
        {
            var instrs = method.Body.Instructions;
            if (instrs.Count == 0) return;
            var reachable = new HashSet<Instruction>();
            var q = new Queue<Instruction>();
            q.Enqueue(instrs[0]);
            reachable.Add(instrs[0]);

            while (q.Count > 0)
            {
                var c = q.Dequeue();
                int idx = instrs.IndexOf(c);
                if (idx == -1) continue;

                if (c.OpCode.Code != Code.Br && c.OpCode.Code != Code.Br_S && c.OpCode.Code != Code.Ret && c.OpCode.Code != Code.Throw)
                {
                    if (idx + 1 < instrs.Count)
                    {
                        var n = instrs[idx + 1];
                        if (reachable.Add(n)) q.Enqueue(n);
                    }
                }
                if (c.Operand is Instruction t && reachable.Add(t)) q.Enqueue(t);
                else if (c.Operand is Instruction[] ts) { foreach (var x in ts) if (reachable.Add(x)) q.Enqueue(x); }
            }

            bool ch = false;
            for (int i = instrs.Count - 1; i >= 0; i--)
            {
                if (!reachable.Contains(instrs[i]))
                {
                    instrs[i].OpCode = OpCodes.Nop;
                    instrs[i].Operand = null;
                    ch = true;
                }
            }
            if (ch) method.Body.UpdateInstructionOffsets();
        }

        private void RemoveNops(MethodDef method)
        {
            var instrs = method.Body.Instructions;
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = instrs.Count - 1; i >= 0; i--)
                {
                    if (instrs[i].OpCode.Code == Code.Nop)
                    {
                        bool isTarget = false;
                        foreach (var ins in instrs)
                        {
                            if (ins.Operand == instrs[i]) { isTarget = true; break; }
                            if (ins.Operand is Instruction[] arr && arr.Contains(instrs[i])) { isTarget = true; break; }
                        }
                        if (!isTarget)
                        {
                            instrs.RemoveAt(i);
                            changed = true;
                        }
                    }
                }
            }
            method.Body.UpdateInstructionOffsets();
            method.Body.SimplifyMacros(method.Parameters);
        }

        #region Helpers (IsStloc, IsLdloc, GetConstantValue, CloneInstruction, etc.)
        // ... (вставьте сюда те же хелперы, что были в вашем рабочем коде) ...
        
        private bool IsStloc(Instruction i, out int idx)
        {
            idx = -1;
            if (i.Operand is Local l) idx = l.Index;
            else if (i.OpCode.Code == Code.Stloc_0) idx = 0;
            else if (i.OpCode.Code == Code.Stloc_1) idx = 1;
            else if (i.OpCode.Code == Code.Stloc_2) idx = 2;
            else if (i.OpCode.Code == Code.Stloc_3) idx = 3;
            return idx != -1 && (i.OpCode.Code == Code.Stloc || i.OpCode.Code == Code.Stloc_S || 
                   i.OpCode.Code >= Code.Stloc_0 && i.OpCode.Code <= Code.Stloc_3);
        }

        private bool IsLdloc(Instruction i, out int idx)
        {
            idx = -1;
            if (i.Operand is Local l) idx = l.Index;
            else if (i.OpCode.Code == Code.Ldloc_0) idx = 0;
            else if (i.OpCode.Code == Code.Ldloc_1) idx = 1;
            else if (i.OpCode.Code == Code.Ldloc_2) idx = 2;
            else if (i.OpCode.Code == Code.Ldloc_3) idx = 3;
            return idx != -1 && (i.OpCode.Code == Code.Ldloc || i.OpCode.Code == Code.Ldloc_S || 
                   i.OpCode.Code >= Code.Ldloc_0 && i.OpCode.Code <= Code.Ldloc_3);
        }

        private int GetLocalIndex(Instruction i)
        {
            if (IsLdloc(i, out int idx) || IsStloc(i, out idx)) return idx;
            return -1;
        }

        private object? GetConstantValue(Instruction i)
        {
            switch (i.OpCode.Code)
            {
                case Code.Ldc_I4: return i.Operand as int?;
                case Code.Ldc_I4_0: return 0; case Code.Ldc_I4_1: return 1; case Code.Ldc_I4_2: return 2;
                case Code.Ldc_I4_3: return 3; case Code.Ldc_I4_4: return 4; case Code.Ldc_I4_5: return 5;
                case Code.Ldc_I4_6: return 6; case Code.Ldc_I4_7: return 7; case Code.Ldc_I4_8: return 8;
                case Code.Ldc_I4_M1: return -1;
                case Code.Ldc_I8: return i.Operand as long?;
                case Code.Ldc_R4: return i.Operand as float?;
                case Code.Ldc_R8: return i.Operand as double?;
                default: return null;
            }
        }

        private bool IsObfuscatedName(string n) => !string.IsNullOrEmpty(n) && (n.Length == 1 || n.StartsWith("?"));

        private void RenameWithAi(MethodDef m)
        {
            if (_aiAssistant == null) return;
            try
            {
                var snip = string.Join("\n", m.Body.Instructions.Take(15).Select(x => x.ToString()));
                var newName = _aiAssistant.GetSuggestedName(m.Name, snip, m.ReturnType?.ToString());
                if (!string.IsNullOrEmpty(newName) && IsValidIdentifier(newName))
                {
                    m.Name = newName;
                    Console.WriteLine($"[AI] Renamed {m.Name}");
                }
            } catch { }
        }

        private bool IsValidIdentifier(string n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            if (!char.IsLetter(n[0]) && n[0] != '_') return false;
            foreach (var c in n) if (!char.IsLetterOrDigit(c) && c != '_') return false;
            return true;
        }

        private Instruction CloneInstruction(Instruction orig)
        {
            var op = orig.OpCode;
            var operand = orig.Operand;
            if (operand is Local l) return Instruction.Create(op, l);
            if (operand is Parameter p) return Instruction.Create(op, p);
            if (operand is Instruction t) return Instruction.Create(op, t);
            if (operand is Instruction[] ts) return Instruction.Create(op, ts);
            if (operand is string s) return Instruction.Create(op, s);
            if (operand is int i) return Instruction.Create(op, i);
            if (operand is long lo) return Instruction.Create(op, lo);
            if (operand is float f) return Instruction.Create(op, f);
            if (operand is double d) return Instruction.Create(op, d);
            if (operand is ITypeDefOrRef td) return Instruction.Create(op, td);
            if (operand is MethodDef m) return Instruction.Create(op, m);
            if (operand is FieldDef fd) return Instruction.Create(op, fd);
            if (operand is MemberRef mr) return Instruction.Create(op, mr);
            return new Instruction(op, operand);
        }

        private void ReplaceMethodBody(MethodDef m, List<Instruction> newInstrs)
        {
            var body = m.Body;
            body.Instructions.Clear();
            foreach (var i in newInstrs) body.Instructions.Add(i);
            body.UpdateInstructionOffsets();
            body.SimplifyMacros(m.Parameters);
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
            _logWriter?.Dispose();
        }
    }
}
