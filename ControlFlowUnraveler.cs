using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Deobfuscator
{
    /// <summary>
    /// Отвечает за распутывание state machine и упрощение control flow.
    /// </summary>
    public class ControlFlowUnraveler
    {
        private readonly Action<string>? _log;

        public ControlFlowUnraveler(Action<string>? log = null)
        {
            _log = log;
        }

        public bool Unravel(MethodDef method)
        {
            var body = method.Body;
            var instrs = body.Instructions;

            if (!method.HasBody || !method.Body.IsIL || instrs.Count < 5)
                return false;

            // 1. Поиск переменной состояния (State Variable)
            int? stateVarIndex = FindStateVariable(method);

            if (!stateVarIndex.HasValue)
            {
                return SimplifyControlFlowBasic(method);
            }

            _log?.Invoke($"  Found state variable: V_{stateVarIndex.Value}");

            // 2. Символьное выполнение для вычисления значений состояния
            var stateMap = SymbolicExecuteState(method, stateVarIndex.Value);

            if (stateMap.Count == 0)
            {
                _log?.Invoke("  Symbolic execution yielded no states. Skipping.");
                return false;
            }

            bool changed = false;

            // 3. Упрощение ветвлений на основе карты состояний
            for (int i = instrs.Count - 1; i >= 0; i--)
            {
                var instr = instrs[i];

                // А. Удаляем записи в переменную состояния
                if (IsStloc(instr, out int sIdx) && sIdx == stateVarIndex.Value)
                {
                    if (i > 0 && IsLdc(instrs[i - 1]))
                    {
                        instrs[i - 1].OpCode = OpCodes.Nop;
                        instrs[i - 1].Operand = null;
                    }
                    instr.OpCode = OpCodes.Nop;
                    instr.Operand = null;
                    changed = true;
                    continue;
                }

                // Б. Упрощаем условные переходы
                if (instr.OpCode.FlowControl == FlowControl.Cond_Branch)
                {
                    if (TryResolveBranch(instrs, i, stateMap, stateVarIndex.Value))
                    {
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                CleanupNops(method);
                RemoveUnreachableBlocks(method);
                body.UpdateInstructionOffsets();
            }

            return changed;
        }

        private int? FindStateVariable(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            var usageCount = new Dictionary<int, int>();

            for (int i = 0; i < instructions.Count - 3; i++)
            {
                if (IsLdloc(instructions[i], out int idx))
                {
                    var next = instructions[i + 1];
                    var next2 = instructions[i + 2];

                    if (IsLdc(next) &&
                        (next2.OpCode.Code == Code.Ceq || next2.OpCode.Code == Code.Cgt ||
                         next2.OpCode.Code == Code.Clt || next2.OpCode.Code == Code.Cgt_Un ||
                         next2.OpCode.Code == Code.Clt_Un))
                    {
                        if (!usageCount.ContainsKey(idx)) usageCount[idx] = 0;
                        usageCount[idx]++;
                    }
                }
            }

            foreach (var kvp in usageCount)
            {
                if (kvp.Value >= 3) return kvp.Key;
            }

            return null;
        }

        private Dictionary<int, object?> SymbolicExecuteState(MethodDef method, int stateVarIndex)
        {
            var stateValues = new Dictionary<int, object?>();
            var instructions = method.Body.Instructions;

            var queue = new Queue<Tuple<int, object?>>();

            object? initialState = null;
            int startIp = 0;

            for (int i = 0; i < Math.Min(30, instructions.Count); i++)
            {
                if (IsStloc(instructions[i], out int idx) && idx == stateVarIndex)
                {
                    if (i > 0 && IsLdc(instructions[i - 1]))
                    {
                        initialState = GetConstantValue(instructions[i - 1]);
                        startIp = i + 1;
                        break;
                    }
                }
            }

            if (initialState == null) return stateValues;

            queue.Enqueue(Tuple.Create(startIp, initialState));

            var visited = new HashSet<int>();

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                int ip = current.Item1;
                object? currentState = current.Item2;

                if (ip < 0 || ip >= instructions.Count) continue;
                if (visited.Contains(ip)) continue;
                visited.Add(ip);

                if (!stateValues.ContainsKey(ip))
                    stateValues[ip] = currentState;

                var instr = instructions[ip];

                if (instr.OpCode.FlowControl == FlowControl.Branch)
                {
                    if (instr.Operand is Instruction target)
                    {
                        int targetIp = instructions.IndexOf(target);
                        if (targetIp != -1) queue.Enqueue(Tuple.Create(targetIp, currentState));
                    }
                }
                else if (instr.OpCode.FlowControl == FlowControl.Cond_Branch)
                {
                    if (instr.Operand is Instruction target)
                    {
                        int targetIp = instructions.IndexOf(target);
                        if (targetIp != -1) queue.Enqueue(Tuple.Create(targetIp, currentState));
                    }
                    int nextIp = ip + 1;
                    if (nextIp < instructions.Count) queue.Enqueue(Tuple.Create(nextIp, currentState));
                }
                else if (instr.OpCode.FlowControl == FlowControl.Ret || instr.OpCode.FlowControl == FlowControl.Throw)
                {
                    // Конец пути
                }
                else
                {
                    object? nextState = currentState;

                    if (IsStloc(instr, out int sIdx) && sIdx == stateVarIndex)
                    {
                        if (ip > 0 && IsLdc(instructions[ip - 1]))
                        {
                            nextState = GetConstantValue(instructions[ip - 1]);
                        }
                    }

                    int nextIp = ip + 1;
                    if (nextIp < instructions.Count)
                    {
                        queue.Enqueue(Tuple.Create(nextIp, nextState));
                    }
                }
            }

            return stateValues;
        }

        private bool TryResolveBranch(IList<Instruction> instrs, int branchIp, Dictionary<int, object?> stateMap, int stateVarIndex)
        {
            var branchInstr = instrs[branchIp];

            int lookback = 0;
            int cmpIp = -1;
            int ldcIp = -1;
            int ldlocIp = -1;

            if (branchIp >= 3)
            {
                var i1 = instrs[branchIp - 1];
                var i2 = instrs[branchIp - 2];
                var i3 = instrs[branchIp - 3];

                if ((i1.OpCode.Code == Code.Ceq || i1.OpCode.Code == Code.Cgt || i1.OpCode.Code == Code.Clt ||
                     i1.OpCode.Code == Code.Cgt_Un || i1.OpCode.Code == Code.Clt_Un) &&
                    IsLdc(i2) && IsLdloc(i3, out int lIdx) && lIdx == stateVarIndex)
                {
                    cmpIp = branchIp - 1;
                    ldcIp = branchIp - 2;
                    ldlocIp = branchIp - 3;
                }
            }

            if (cmpIp == -1) return false;

            if (!stateMap.TryGetValue(ldlocIp, out object? currentState))
                return false;

            var compareValue = GetConstantValue(instrs[ldcIp]);
            if (compareValue == null || currentState == null)
                return false;

            bool? result = CompareValues(currentState, compareValue, instrs[cmpIp].OpCode.Code);

            if (result.HasValue)
            {
                if (result.Value)
                {
                    _log?.Invoke($"    Resolved branch at {branchIp}: TRUE -> br");
                    branchInstr.OpCode = OpCodes.Br;
                    if (branchInstr.OpCode.Code == Code.Brtrue_S || branchInstr.OpCode.Code == Code.Brfalse_S)
                        branchInstr.OpCode = OpCodes.Br_S;
                    else branchInstr.OpCode = OpCodes.Br;

                    instrs[ldlocIp].OpCode = OpCodes.Nop;
                    instrs[ldlocIp].Operand = null;
                    instrs[ldcIp].OpCode = OpCodes.Nop;
                    instrs[ldcIp].Operand = null;
                    instrs[cmpIp].OpCode = OpCodes.Nop;
                    instrs[cmpIp].Operand = null;
                }
                else
                {
                    _log?.Invoke($"    Resolved branch at {branchIp}: FALSE -> nop");
                    branchInstr.OpCode = OpCodes.Nop;
                    branchInstr.Operand = null;

                    instrs[ldlocIp].OpCode = OpCodes.Nop;
                    instrs[ldlocIp].Operand = null;
                    instrs[ldcIp].OpCode = OpCodes.Nop;
                    instrs[ldcIp].Operand = null;
                    instrs[cmpIp].OpCode = OpCodes.Nop;
                    instrs[cmpIp].Operand = null;
                }
                return true;
            }

            return false;
        }

        private bool SimplifyControlFlowBasic(MethodDef method)
        {
            return false;
        }

        #region Helper Methods

        private bool IsLdloc(Instruction instr, out int index)
        {
            index = -1;
            if (instr.OpCode.Code == Code.Ldloc_0) { index = 0; return true; }
            if (instr.OpCode.Code == Code.Ldloc_1) { index = 1; return true; }
            if (instr.OpCode.Code == Code.Ldloc_2) { index = 2; return true; }
            if (instr.OpCode.Code == Code.Ldloc_3) { index = 3; return true; }
            if (instr.OpCode.Code == Code.Ldloc || instr.OpCode.Code == Code.Ldloc_S)
            {
                if (instr.Operand is Local local)
                {
                    index = local.Index;
                    return true;
                }
            }
            return false;
        }

        private bool IsStloc(Instruction instr, out int index)
        {
            index = -1;
            if (instr.OpCode.Code == Code.Stloc_0) { index = 0; return true; }
            if (instr.OpCode.Code == Code.Stloc_1) { index = 1; return true; }
            if (instr.OpCode.Code == Code.Stloc_2) { index = 2; return true; }
            if (instr.OpCode.Code == Code.Stloc_3) { index = 3; return true; }
            if (instr.OpCode.Code == Code.Stloc || instr.OpCode.Code == Code.Stloc_S)
            {
                if (instr.Operand is Local local)
                {
                    index = local.Index;
                    return true;
                }
            }
            return false;
        }

        private bool IsLdc(Instruction instr)
        {
            return instr.OpCode.Code == Code.Ldc_I4 || instr.OpCode.Code == Code.Ldc_I4_S ||
                   instr.OpCode.Code == Code.Ldc_I8 || instr.OpCode.Code == Code.Ldc_R4 ||
                   instr.OpCode.Code == Code.Ldc_R8;
        }

        private object? GetConstantValue(Instruction instr)
        {
            switch (instr.OpCode.Code)
            {
                case Code.Ldc_I4:
                case Code.Ldc_I4_S:
                    return instr.Operand as int?;
                case Code.Ldc_I8:
                    return instr.Operand as long?;
                case Code.Ldc_R4:
                    return instr.Operand as float?;
                case Code.Ldc_R8:
                    return instr.Operand as double?;
                default:
                    return null;
            }
        }

        private bool? CompareValues(object? a, object? b, Code cmpOp)
        {
            if (a == null || b == null) return null;

            try
            {
                double da = Convert.ToDouble(a);
                double db = Convert.ToDouble(b);

                switch (cmpOp)
                {
                    case Code.Ceq: return da == db;
                    case Code.Cgt: return da > db;
                    case Code.Clt: return da < db;
                    case Code.Cgt_Un:
                    case Code.Cge_Un: return da > db;
                    case Code.Clt_Un:
                    case Code.Cle_Un: return da < db;
                    default: return null;
                }
            }
            catch
            {
                return null;
            }
        }

        private void CleanupNops(MethodDef method)
        {
            var body = method.Body;
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = body.Instructions.Count - 1; i >= 0; i--)
                {
                    if (body.Instructions[i].OpCode.Code == Code.Nop)
                    {
                        bool isTarget = false;
                        foreach (var ins in body.Instructions)
                        {
                            if (ins.Operand == body.Instructions[i]) { isTarget = true; break; }
                            if (ins.Operand is Instruction[] arr && arr.Contains(body.Instructions[i])) { isTarget = true; break; }
                        }

                        if (!isTarget)
                        {
                            body.Instructions.RemoveAt(i);
                            changed = true;
                        }
                    }
                }
            }
        }

        private void RemoveUnreachableBlocks(MethodDef method)
        {
            var body = method.Body;
            if (body.Instructions.Count == 0) return;

            var reachable = new HashSet<Instruction>();
            var q = new Queue<Instruction>();

            q.Enqueue(body.Instructions[0]);
            reachable.Add(body.Instructions[0]);

            while (q.Count > 0)
            {
                var curr = q.Dequeue();
                int idx = body.Instructions.IndexOf(curr);
                if (idx == -1) continue;

                if (curr.OpCode.FlowControl != FlowControl.Branch &&
                    curr.OpCode.FlowControl != FlowControl.Ret &&
                    curr.OpCode.FlowControl != FlowControl.Throw)
                {
                    if (idx + 1 < body.Instructions.Count)
                    {
                        var next = body.Instructions[idx + 1];
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
            for (int i = body.Instructions.Count - 1; i >= 0; i--)
            {
                if (!reachable.Contains(body.Instructions[i]))
                {
                    body.Instructions[i].OpCode = OpCodes.Nop;
                    body.Instructions[i].Operand = null;
                    changed = true;
                }
            }

            if (changed) CleanupNops(method);
        }

        #endregion
    }
}
