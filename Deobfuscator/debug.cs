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
        private readonly bool _debugMode;
        private int _indentLevel = 0;

        public UniversalDeobfuscator(string filePath, AiConfig aiConfig, bool debugMode = false)
        {
            _module = ModuleDefMD.Load(filePath);
            _aiAssistant = aiConfig.Enabled ? new AiAssistant(aiConfig) : null;
            _debugMode = debugMode;
        }

        private void Log(string message)
        {
            if (!_debugMode) return;
            string indent = new string(' ', _indentLevel * 2);
            Console.WriteLine($"[DEBUG] {indent}{message}");
        }

        public void Deobfuscate()
        {
            Console.WriteLine("[*] Starting deep deobfuscation...");
            if (_debugMode) Log("Debug mode enabled.");
            
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
                            Log($"Processing method: {method.FullName}");
                            _indentLevel++;
                        }

                        // Попытка распутать state machine
                        if (UnpackStateMachine(method))
                        {
                            CleanupNops(method);
                            count++;
                            if (_debugMode) Log("State machine unpacked successfully.");
                        }
                        else
                        {
                            if (_debugMode) Log("Not a state machine or unpacking failed. Trying simplification.");
                            SimplifyConditions(method);
                            CleanupNops(method);
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
                        if (_debugMode) Log($"Exception details: {ex}");
                        if (_debugMode) _indentLevel--;
                    }
                }
            }
            Console.WriteLine($"[*] Processed {count} methods.");
        }

        private bool UnpackStateMachine(MethodDef method)
        {
            var body = method.Body;
            if (body == null || body.Instructions.Count < 5) return false;

            var instructions = body.Instructions;
            
            Log($"Analyzing {instructions.Count} instructions.");

            // 1. Поиск переменной состояния
            int stateVarIndex = -1;
            object? initialState = null;

            for (int i = 0; i < Math.Min(10, instructions.Count - 1); i++)
            {
                if (IsStloc(instructions[i + 1], out int idx))
                {
                    var val = GetConstantValue(instructions[i]);
                    if (val != null)
                    {
                        stateVarIndex = idx;
                        initialState = val;
                        Log($"Found state variable: V_{stateVarIndex}, Initial Value: {initialState}");
                        break;
                    }
                }
            }

            if (stateVarIndex == -1 || initialState == null)
            {
                Log("No state variable found.");
                return false;
            }

            // 2. Построение графа состояний
            // Map: StateValue -> List of Instructions (Payload)
            var stateBlocks = new Dictionary<object, List<Instruction>>();
            // Map: StateValue -> NextStateValue
            var transitions = new Dictionary<object, object?>();
            
            // Мы будем искать паттерны:
            // IL_X: ldloc.s V_state
            // IL_Y: ldc.i4 XXXX (CheckVal)
            // IL_Z: ceq
            // IL_K: brtrue.s IL_Target (Start of Payload)
            // ... Payload ...
            // IL_N: ldc.i4 YYYY (NextVal)
            // IL_M: stloc.s V_state
            
            Log("Building state transition graph...");

            for (int i = 0; i < instructions.Count - 4; i++)
            {
                var instr1 = instructions[i];
                var instr2 = instructions[i+1];
                var instr3 = instructions[i+2];
                var instr4 = instructions[i+3];

                if (GetLocalIndex(instr1) == stateVarIndex &&
                    GetConstantValue(instr2) is object checkVal &&
                    (instr3.OpCode.Code == Code.Ceq || instr3.OpCode.Code == Code.Cgt || instr3.OpCode.Code == Code.Clt) &&
                    (instr4.OpCode.Code == Code.Brtrue || instr4.OpCode.Code == Code.Brtrue_S || 
                     instr4.OpCode.Code == Code.Brfalse || instr4.OpCode.Code == Code.Brfalse_S))
                {
                    var target = instr4.Operand as Instruction;
                    if (target != null)
                    {
                        Log($"Found state check: if (state == {checkVal}) goto {target.Offset}");
                        
                        // Извлекаем блок
                        var block = ExtractBlock(instructions, target, stateVarIndex, out object? nextVal);
                        
                        if (!stateBlocks.ContainsKey(checkVal))
                        {
                            stateBlocks[checkVal] = block;
                            transitions[checkVal] = nextVal;
                            Log($"  Extracted block for state {checkVal}. Next state: {nextVal ?? "null"}");
                            Log($"  Block instructions count: {block.Count}");
                            if (_debugMode && block.Count > 0)
                            {
                                _indentLevel++;
                                foreach(var ins in block.Take(5)) // Покажем первые 5
                                {
                                    Log($"    {ins}");
                                }
                                if (block.Count > 5) Log($"    ... and {block.Count - 5} more");
                                _indentLevel--;
                            }
                        }
                    }
                }
            }

            if (stateBlocks.Count == 0)
            {
                Log("No state blocks found.");
                return false;
            }

            // 3. Сборка линейного кода
            var finalInstructions = new List<Instruction>();
            var visited = new HashSet<object?>();
            var queue = new Queue<object?>();

            if (initialState != null) queue.Enqueue(initialState);

            Log("Reconstructing linear flow...");

            while (queue.Count > 0)
            {
                var currentState = queue.Dequeue();
                if (visited.Contains(currentState))
                {
                    Log($"  Skipping already visited state: {currentState}");
                    continue;
                }
                visited.Add(currentState);
                Log($"  Processing state: {currentState}");

                if (stateBlocks.TryGetValue(currentState, out var block))
                {
                    foreach (var ins in block)
                    {
                        finalInstructions.Add(CloneInstruction(ins));
                    }
                }

                if (transitions.TryGetValue(currentState, out var nextVal) && nextVal != null)
                {
                    if (!visited.Contains(nextVal))
                        queue.Enqueue(nextVal);
                }
            }

            if (finalInstructions.Count == 0)
            {
                Log("Resulting instruction list is empty.");
                return false;
            }

            Log($"Final instruction count: {finalInstructions.Count}");
            ReplaceMethodBody(method, finalInstructions);
            return true;
        }

        private List<Instruction> ExtractBlock(IList<Instruction> allInstructions, Instruction startInstr, int stateVarIndex, out object? nextState)
        {
            var block = new List<Instruction>();
            nextState = null;

            int startIndex = allInstructions.IndexOf(startInstr);
            if (startIndex == -1) return block;

            int ip = startIndex;
            // Ограничим размер блока, чтобы не уйти далеко
            int maxBlockLen = 50; 
            int count = 0;

            while (ip < allInstructions.Count && count < maxBlockLen)
            {
                var instr = allInstructions[ip];
                count++;

                // Проверка на конец блока: переход вперед или возврат
                if (instr.OpCode.FlowControl == FlowControl.Branch)
                {
                    if (instr.Operand is Instruction t)
                    {
                        int tIdx = allInstructions.IndexOf(t);
                        if (tIdx > ip) 
                        {
                            Log($"    End of block at {instr.Offset} (branch forward to {t.Offset})");
                            break; 
                        }
                    }
                }
                
                if (instr.OpCode.FlowControl == FlowControl.Return)
                {
                    block.Add(CloneInstruction(instr));
                    Log($"    Added (Ret): {instr}");
                    break;
                }

                // Проверка на обновление состояния (конец текущего блока)
                // Паттерн: ldc.X, stloc.s (state)
                if (ip + 1 < allInstructions.Count)
                {
                    var nextIns = allInstructions[ip+1];
                    if (IsStloc(nextIns, out int sIdx) && sIdx == stateVarIndex)
                    {
                        var val = GetConstantValue(instr);
                        if (val != null)
                        {
                            nextState = val;
                            Log($"    Found state update: state = {val}. Ending block.");
                            break; // Конец блока, не включаем эти инструкции
                        }
                    }
                }

                // Фильтрация мусора внутри блока
                // Мы хотим убрать оставшиеся проверки, если они есть внутри блока (редко, но бывает)
                // И оставить только полезную нагрузку
                
                bool isJunk = false;

                // Если это ldloc(state) - мусор
                if (GetLocalIndex(instr) == stateVarIndex && 
                   (instr.OpCode.Code == Code.Ldloc || instr.OpCode.Code == Code.Ldloc_S ||
                    (instr.OpCode.Code >= Code.Ldloc_0 && instr.OpCode.Code <= Code.Ldloc_3)))
                {
                    isJunk = true;
                    if (_debugMode) Log($"    Skipped (State Load): {instr}");
                }
                
                // Если это сравнение (ceq, cgt...) - мусор
                if (!isJunk && (instr.OpCode.Code == Code.Ceq || instr.OpCode.Code == Code.Cgt || instr.OpCode.Code == Code.Clt ||
                    instr.OpCode.Code == Code.Cgt_Un || instr.OpCode.Code == Code.Clt_Un))
                {
                    isJunk = true;
                    if (_debugMode) Log($"    Skipped (Comparison): {instr}");
                }

                // Если это ветвление - мусор (мы уже обработали логику ветвления при построении графа)
                if (!isJunk && (instr.OpCode.FlowControl == FlowControl.Cond_Branch || instr.OpCode.FlowControl == FlowControl.Branch))
                {
                    isJunk = true;
                    if (_debugMode) Log($"    Skipped (Branch): {instr}");
                }

                if (!isJunk)
                {
                    block.Add(CloneInstruction(instr));
                    if (_debugMode) Log($"    Added: {instr}");
                }

                ip++;
            }

            return block;
        }

        private void SimplifyConditions(MethodDef method)
        {
            var body = method.Body;
            var instructions = body.Instructions;
            bool changed = true;
            int passes = 0;

            while (changed && passes < 20)
            {
                changed = false;
                passes++;
                
                var knownValues = new Dictionary<int, object?>();
                
                for (int i = 0; i < instructions.Count; i++)
                {
                    var instr = instructions[i];
                    if (i + 1 < instructions.Count && IsStloc(instructions[i+1], out int idx))
                    {
                        var val = GetConstantValue(instr);
                        if (val != null) knownValues[idx] = val;
                    }
                }

                for (int i = 0; i < instructions.Count; i++)
                {
                    var instr = instructions[i];
                    if (instr.OpCode.FlowControl == FlowControl.Cond_Branch)
                    {
                        bool? res = TryEvalSimpleCondition(instructions, i, knownValues);
                        if (res.HasValue)
                        {
                            if (instr.Operand is Instruction target)
                            {
                                if (res.Value)
                                {
                                    instr.OpCode = OpCodes.Br;
                                    instr.Operand = target;
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
                }
                
                if (changed) body.UpdateInstructionOffsets();
            }
            
            RemoveUnreachableBlocks(method);
        }

        private bool? TryEvalSimpleCondition(IList<Instruction> instrs, int idx, Dictionary<int, object?> known)
        {
            if (idx < 2) return null;
            var branch = instrs[idx];
            var prev1 = instrs[idx - 1]; 
            var prev2 = instrs[idx - 2]; 
            var prev3 = (idx >= 3) ? instrs[idx - 3] : null; 

            if ((prev1.OpCode.Code == Code.Ceq || prev1.OpCode.Code == Code.Cgt || prev1.OpCode.Code == Code.Clt))
            {
                if (prev3 != null && IsLdloc(prev3, out int lIdx) && GetConstantValue(prev2) is object val2)
                {
                    if (known.ContainsKey(lIdx))
                    {
                        return CompareValues(known[lIdx], val2, prev1.OpCode.Code);
                    }
                }
            }
            
            if (branch.OpCode.Code == Code.Brtrue || branch.OpCode.Code == Code.Brfalse || 
                branch.OpCode.Code == Code.Brtrue_S || branch.OpCode.Code == Code.Brfalse_S)
            {
                if (prev2 != null && IsLdloc(prev2, out int lIdx) && known.ContainsKey(lIdx))
                {
                    var val = known[lIdx];
                    if (val == null) return null;
                    bool isZero = Convert.ToDouble(val) == 0;
                    return (branch.OpCode.Code == Code.Brtrue || branch.OpCode.Code == Code.Brtrue_S) ? !isZero : isZero;
                }
            }

            return null;
        }

        private bool? CompareValues(object? a, object? b, Code op)
        {
            if (a == null || b == null) return null;
            try
            {
                double da = Convert.ToDouble(a);
                double db = Convert.ToDouble(b);
                switch (op)
                {
                    case Code.Ceq: return da == db;
                    case Code.Cgt: case Code.Cgt_Un: return da > db;
                    case Code.Clt: case Code.Clt_Un: return da < db;
                }
            } catch { }
            return null;
        }

        private void RemoveUnreachableBlocks(MethodDef method)
        {
            var body = method.Body;
            var instrs = body.Instructions;
            if (instrs.Count == 0) return;

            var reachable = new HashSet<Instruction>();
            var q = new Queue<Instruction>();
            
            q.Enqueue(instrs[0]);
            reachable.Add(instrs[0]);

            while (q.Count > 0)
            {
                var curr = q.Dequeue();
                int idx = instrs.IndexOf(curr);
                if (idx == -1) continue;

                if (curr.OpCode.Code != Code.Br && curr.OpCode.Code != Code.Br_S && 
                    curr.OpCode.Code != Code.Ret && curr.OpCode.Code != Code.Throw)
                {
                    if (idx + 1 < instrs.Count)
                    {
                        var next = instrs[idx + 1];
                        if (reachable.Add(next)) q.Enqueue(next);
                    }
                }

                if (curr.Operand is Instruction t)
                {
                    if (reachable.Add(t)) q.Enqueue(t);
                }
                else if (curr.Operand is Instruction[] ts)
                {
                    foreach (var x in ts) if (reachable.Add(x)) q.Enqueue(x);
                }
            }

            bool changed = false;
            for (int i = instrs.Count - 1; i >= 0; i--)
            {
                if (!reachable.Contains(instrs[i]))
                {
                    instrs[i].OpCode = OpCodes.Nop;
                    instrs[i].Operand = null;
                    changed = true;
                }
            }
            if (changed) body.UpdateInstructionOffsets();
        }

        private void CleanupNops(MethodDef method)
        {
            var body = method.Body;
            var instrs = body.Instructions;
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
            body.UpdateInstructionOffsets();
            body.SimplifyMacros(method.Parameters);
        }

        #region Helpers

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
        }
    }
}
