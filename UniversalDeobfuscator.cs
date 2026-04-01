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
            Console.WriteLine("[*] Starting deep deobfuscation with stack emulation...");
            int count = 0;
            
            foreach (var type in _module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody || !method.Body.IsIL) continue;

                    try
                    {
                        // Пытаемся распутать через эмуляцию
                        bool unraveled = EmulateAndUnravel(method);
                        
                        if (!unraveled)
                        {
                            // Фоллбэк: простая очистка NOP и мертвого кода
                            CleanupMethod(method);
                            RemoveUnreachableBlocks(method);
                        }
                        else
                        {
                            Console.WriteLine($"[+] Unraveled: {method.FullName}");
                        }

                        if (_aiAssistant != null && IsObfuscatedName(method.Name))
                        {
                            RenameWithAi(method);
                        }
                        count++;
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
        /// Эмулирует выполнение метода для восстановления линейного потока.
        /// </summary>
        private bool EmulateAndUnravel(MethodDef method)
        {
            var body = method.Body;
            if (body == null || !body.HasInstructions) return false;

            var instructions = body.Instructions;
            if (instructions.Count < 5) return false;

            // 1. Поиск переменной состояния
            int stateVarIndex = -1;
            object? initialState = null;

            for (int i = 0; i < Math.Min(instructions.Count, 20); i++)
            {
                if (IsStloc(instructions[i], out int idx))
                {
                    if (i > 0)
                    {
                        var val = GetConstantValue(instructions[i - 1]);
                        if (val != null)
                        {
                            stateVarIndex = idx;
                            initialState = val;
                            break;
                        }
                    }
                }
            }

            if (stateVarIndex == -1) return false;

            Console.WriteLine($"  -> Found state var: Local_{stateVarIndex}, Initial: {initialState}");

            // 2. Эмуляция выполнения
            var executedInstructions = new List<Instruction>();
            var stack = new Stack<object?>();
            var locals = new object?[method.Body.Variables.Count];
            
            if (stateVarIndex < locals.Length)
                locals[stateVarIndex] = initialState;

            int ip = 0;
            int maxSteps = 10000;
            int steps = 0;
            
            var stateControlInstructions = new HashSet<Instruction>();

            while (ip >= 0 && ip < instructions.Count && steps < maxSteps)
            {
                steps++;
                var instr = instructions[ip];
                bool advanceIp = true;

                try
                {
                    // Ldc
                    if (instr.OpCode.Code == Code.Ldc_I4 || instr.OpCode.Code == Code.Ldc_I8 || 
                        instr.OpCode.Code == Code.Ldc_R4 || instr.OpCode.Code == Code.Ldc_R8 ||
                        (instr.OpCode.Code >= Code.Ldc_I4_0 && instr.OpCode.Code <= Code.Ldc_I4_M1))
                    {
                        var val = GetConstantValue(instr);
                        if (val != null) stack.Push(val);
                    }
                    // Stloc
                    else if (IsStloc(instr, out int storeIdx))
                    {
                        if (stack.Count > 0)
                        {
                            var val = stack.Pop();
                            if (storeIdx < locals.Length)
                                locals[storeIdx] = val;
                            
                            if (storeIdx == stateVarIndex)
                                stateControlInstructions.Add(instr);
                        }
                    }
                    // Ldloc
                    else if (IsLdloc(instr, out int loadIdx))
                    {
                        if (loadIdx < locals.Length)
                            stack.Push(locals[loadIdx]);
                    }
                    // Cond_Branch
                    else if (instr.OpCode.FlowControl == FlowControl.Cond_Branch)
                    {
                        if (ip >= 2)
                        {
                            var prev1 = instructions[ip - 1];
                            var prev2 = instructions[ip - 2];

                            if (IsLdloc(prev2, out int checkIdx) && checkIdx == stateVarIndex)
                            {
                                var constVal = GetConstantValue(prev1);
                                var current_state = locals[stateVarIndex];

                                if (constVal != null && current_state != null)
                                {
                                    bool takeBranch = CompareValues(current_state, constVal, instr.OpCode.Code) ?? false;
                                    
                                    stateControlInstructions.Add(prev2);
                                    stateControlInstructions.Add(prev1);
                                    stateControlInstructions.Add(instr);

                                    if (takeBranch)
                                    {
                                        if (instr.Operand is Instruction target)
                                        {
                                            ip = instructions.IndexOf(target);
                                            advanceIp = false;
                                            continue;
                                        }
                                    }
                                }
                            }
                        }
                        
                        // Br
                        if (instr.OpCode.Code == Code.Br || instr.OpCode.Code == Code.Br_S)
                        {
                            stateControlInstructions.Add(instr);
                            if (instr.Operand is Instruction target)
                            {
                                ip = instructions.IndexOf(target);
                                advanceIp = false;
                                continue;
                            }
                        }
                        
                        // Ret/Endfinally
                        if (instr.OpCode.Code == Code.Ret || instr.OpCode.Code == Code.Endfinally)
                        {
                             break; 
                        }
                    }
                    // Nop
                    else if (instr.OpCode.Code == Code.Nop)
                    {
                        // skip
                    }
                    // Real logic
                    else
                    {
                        if (!stateControlInstructions.Contains(instr))
                        {
                            executedInstructions.Add(CloneInstruction(instr));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [!] Emulation error at IP {ip}: {ex.Message}");
                    break;
                }

                if (advanceIp)
                    ip++;
            }

            if (executedInstructions.Count == 0)
                return false;

            if (executedInstructions.Last().OpCode.Code != Code.Ret)
            {
                executedInstructions.Add(Instruction.Create(OpCodes.Ret));
            }

            ReplaceMethodBody(method, executedInstructions);
            return true;
        }

        private bool? CompareValues(object? actual, object? expected, Code opCode)
        {
            if (actual == null || expected == null) return null;

            try
            {
                double dActual = Convert.ToDouble(actual);
                double dExpected = Convert.ToDouble(expected);

                switch (opCode)
                {
                    case Code.Beq:
                    case Code.Beq_S:
                        return dActual == dExpected;
                    case Code.Bne_Un:
                    case Code.Bne_Un_S:
                        return dActual != dExpected;
                    case Code.Bgt:
                    case Code.Bgt_S:
                    case Code.Bgt_Un:
                    case Code.Bgt_Un_S:
                        return dActual > dExpected;
                    case Code.Bge:
                    case Code.Bge_S:
                    case Code.Bge_Un:
                    case Code.Bge_Un_S:
                        return dActual >= dExpected;
                    case Code.Blt:
                    case Code.Blt_S:
                    case Code.Blt_Un:
                    case Code.Blt_Un_S:
                        return dActual < dExpected;
                    case Code.Ble:
                    case Code.Ble_S:
                    case Code.Ble_Un:
                    case Code.Ble_Un_S:
                        return dActual <= dExpected;
                    case Code.Brtrue:
                    case Code.Brtrue_S:
                        return dActual != 0;
                    case Code.Brfalse:
                    case Code.Brfalse_S:
                        return dActual == 0;
                }
            }
            catch { }
            
            return null;
        }

        private void CleanupMethod(MethodDef method)
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
                            {
                                knownValues[localIdx] = val;
                            }
                        }
                    }

                    // Упрощение условных переходов
                    if (instr.OpCode.FlowControl == FlowControl.Cond_Branch)
                    {
                        if (i >= 2)
                        {
                            var prev1 = instructions[i - 1];
                            var prev2 = instructions[i - 2];

                            if (IsLdloc(prev2, out int checkLocalIdx) && GetConstantValue(prev1) != null)
                            {
                                var constVal = GetConstantValue(prev1);
                                
                                if (knownValues.ContainsKey(checkLocalIdx))
                                {
                                    var actualVal = knownValues[checkLocalIdx];
                                    var result = CompareValues(actualVal, constVal, instr.OpCode.Code);
                                    
                                    if (result.HasValue)
                                    {
                                        if (result.Value)
                                        {
                                            instr.OpCode = OpCodes.Br;
                                            changed = true;
                                        }
                                        else
                                        {
                                            instr.OpCode = OpCodes.Nop;
                                            instr.Operand = null;
                                            changed = true;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                if (changed)
                {
                    body.UpdateInstructionOffsets();
                }
            }

            RemoveUnreachableBlocks(method);
        }

        /// <summary>
        /// Сравнение значений с учетом типа операции ветвления.
        /// Возвращает true (ветвь выполняется), false (не выполняется) или null (неизвестно).
        /// </summary>
        private bool? CompareValues(object? actual, object? expected, Code opCode)
        {
            if (actual == null || expected == null) return null;

            // Приводим к общему типу для сравнения (поддержка int, long, double, float)
            double dActual = Convert.ToDouble(actual);
            double dExpected = Convert.ToDouble(expected);

            switch (opCode)
            {
                case Code.Beq:
                case Code.Beq_S:
                    return dActual == dExpected;
                case Code.Bne_Un:
                case Code.Bne_Un_S:
                    return dActual != dExpected;
                case Code.Bgt:
                case Code.Bgt_S:
                case Code.Bgt_Un:
                case Code.Bgt_Un_S:
                    return dActual > dExpected;
                case Code.Bge:
                case Code.Bge_S:
                case Code.Bge_Un:
                case Code.Bge_Un_S:
                    return dActual >= dExpected;
                case Code.Blt:
                case Code.Blt_S:
                case Code.Blt_Un:
                case Code.Blt_Un_S:
                    return dActual < dExpected;
                case Code.Ble:
                case Code.Ble_S:
                case Code.Ble_Un:
                case Code.Ble_Un_S:
                    return dActual <= dExpected;
                
                // Обработка brtrue/brfalse (проверка на ноль/не ноль)
                case Code.Brtrue:
                case Code.Brtrue_S:
                    return dActual != 0;
                case Code.Brfalse:
                case Code.Brfalse_S:
                    return dActual == 0;
            }
            return null;
        }

        private void RemoveUnreachableBlocks(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            if (instructions.Count == 0) return;

            var reachable = new HashSet<Instruction>();
            var queue = new Queue<Instruction>();

            // Старт с первой инструкции
            queue.Enqueue(instructions[0]);
            reachable.Add(instructions[0]);

            while (queue.Count > 0)
            {
                var curr = queue.Dequeue();
                int idx = instructions.IndexOf(curr);
                if (idx == -1) continue;

                // Если это не безусловный переход и не возврат, следующая инструкция достижима
                if (curr.OpCode.Code != Code.Br && curr.OpCode.Code != Code.Ret && curr.OpCode.Code != Code.Throw)
                {
                    if (idx + 1 < instructions.Count)
                    {
                        var next = instructions[idx + 1];
                        if (reachable.Add(next))
                            queue.Enqueue(next);
                    }
                }

                // Цель перехода достижима
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

            // Замена недостижимого на Nop
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

        private void CleanupMethod(MethodDef method)
        {
            var body = method.Body;
            var instructions = body.Instructions;
            bool changed = true;

            while (changed)
            {
                changed = false;
                // Удаляем NOP, на которые нет ссылок
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
            
            // Попытка преобразовать оставшиеся простые конструкции в высокоуровневые (опционально)
            // body.SimplifyMacros(method.Parameters); 
            body.UpdateInstructionOffsets();
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

        /// <summary>
        /// Клонирует инструкцию с сохранением типа операнда.
        /// </summary>
        private Instruction CloneInstruction(Instruction orig)
        {
            var opCode = orig.OpCode;
            var operand = orig.Operand;

            // Обработка специальных типов операндов
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
            
            // Fallback: используем конструктор для неизвестных типов
            return new Instruction(opCode, operand);
        }

        /// <summary>
        /// Заменяет тело метода на новый список инструкций.
        /// </summary>
        private void ReplaceMethodBody(MethodDef method, List<Instruction> newInstructions)
        {
            var body = method.Body;
            
            // Очищаем старые инструкции
            body.Instructions.Clear();
            
            // Добавляем новые инструкции
            foreach (var instr in newInstructions)
            {
                body.Instructions.Add(instr);
            }
            
            // Обновляем оффсеты и пересчитываем стек
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
