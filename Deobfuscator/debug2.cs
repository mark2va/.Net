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
        private readonly string? _logFilePath;
        private int _indentLevel = 0;
        private StreamWriter? _logWriter;

        public UniversalDeobfuscator(string filePath, AiConfig aiConfig, bool debugMode = false)
        {
            _module = ModuleDefMD.Load(filePath);
            _aiAssistant = aiConfig.Enabled ? new AiAssistant(aiConfig) : null;
            _debugMode = debugMode;

            if (_debugMode)
            {
                string dir = Path.GetDirectoryName(filePath) ?? Directory.GetCurrentDirectory();
                _logFilePath = Path.Combine(dir, "deob_log.txt");
                try
                {
                    _logWriter = new StreamWriter(_logFilePath, false);
                    _logWriter.AutoFlush = true;
                    Log("=== Deobfuscation Started ===");
                    Log($"Source: {filePath}");
                    Log($"Time: {DateTime.Now}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[!] Failed to create log file: {ex.Message}");
                }
            }
        }

        private void Log(string message)
        {
            if (!_debugMode) return;
            
            string indent = new string(' ', _indentLevel * 2);
            string fullMsg = $"[{DateTime.Now:HH:mm:ss}] {indent}{message}";
            
            Console.WriteLine(fullMsg);
            _logWriter?.WriteLine(fullMsg);
        }

        public void Deobfuscate()
        {
            Log("Starting deobfuscation...");
            Console.WriteLine("[*] Starting deobfuscation...");
            
            int count = 0;
            int totalMethods = 0;
            int emptyMethods = 0;
            
            foreach (var type in _module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody || !method.Body.HasInstructions) continue;
                    
                    totalMethods++;
                    Log($"Processing method: {method.FullName}");
                    _indentLevel++;
                    
                    // Сохраняем полный бэкап метода
                    var backupInstructions = method.Body.Instructions.ToList();
                    int originalCount = backupInstructions.Count;
                    Log($"Original instructions: {originalCount}");

                    try
                    {
                        bool wasModified = false;
                        
                        // Проверяем, является ли метод state machine (только если есть подозрения)
                        if (originalCount > 10 && LooksLikeStateMachine(method))
                        {
                            Log("Method looks like state machine, attempting to unpack...");
                            bool unpacked = UnpackStateMachineSafe(method);
                            if (unpacked)
                            {
                                wasModified = true;
                                Log($"State machine unpacked. Instructions: {originalCount} -> {method.Body.Instructions.Count}");
                            }
                            else
                            {
                                Log("Unpacking failed, keeping original");
                                RestoreMethod(method, backupInstructions);
                            }
                        }
                        
                        // Упрощаем условия только если метод не стал пустым
                        if (method.Body.Instructions.Count > 0)
                        {
                            int beforeSimplify = method.Body.Instructions.Count;
                            SimplifyConditionsSafe(method);
                            if (beforeSimplify != method.Body.Instructions.Count)
                            {
                                wasModified = true;
                                Log($"Simplified conditions. Instructions: {beforeSimplify} -> {method.Body.Instructions.Count}");
                            }
                        }
                        
                        // Чистим NOPs только если они есть
                        int nopCount = method.Body.Instructions.Count(i => i.OpCode.Code == Code.Nop);
                        if (nopCount > 0 && method.Body.Instructions.Count > nopCount)
                        {
                            CleanupNopsSafe(method);
                            wasModified = true;
                            Log($"Removed {nopCount} NOP instructions");
                        }
                        
                        // Проверяем, не стал ли метод пустым
                        if (method.Body.Instructions.Count == 0)
                        {
                            emptyMethods++;
                            Log($"WARNING: Method became empty! Restoring original...");
                            RestoreMethod(method, backupInstructions);
                            wasModified = false;
                        }
                        else if (wasModified)
                        {
                            count++;
                            Log($"Method successfully processed. Final instructions: {method.Body.Instructions.Count}");
                            
                            // Показываем первые инструкции для проверки
                            if (_debugMode && method.Body.Instructions.Count > 0)
                            {
                                Log("First 5 instructions after processing:");
                                _indentLevel++;
                                for (int i = 0; i < Math.Min(5, method.Body.Instructions.Count); i++)
                                {
                                    Log(method.Body.Instructions[i].ToString());
                                }
                                _indentLevel--;
                            }
                        }
                        
                        // AI переименование (только если метод не пустой)
                        if (_aiAssistant != null && method.Body.Instructions.Count > 0 && IsObfuscatedName(method.Name))
                        {
                            string oldName = method.Name;
                            RenameWithAi(method);
                            if (oldName != method.Name)
                            {
                                Log($"Renamed: {oldName} -> {method.Name}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        string err = $"[!] Error in {method.FullName}: {ex.Message}";
                        Console.WriteLine(err);
                        Log(err);
                        Log($"Restoring original method...");
                        RestoreMethod(method, backupInstructions);
                    }
                    
                    _indentLevel--;
                }
            }
            
            string doneMsg = $"[*] Processed {totalMethods} methods, modified {count} methods, {emptyMethods} methods were empty and restored.";
            Console.WriteLine(doneMsg);
            Log(doneMsg);
            Log("=== Deobfuscation Finished ===");
        }

        private bool LooksLikeStateMachine(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            if (instructions.Count < 10) return false;
            
            // Ищем характерные паттерны state machine
            int stateVarHints = 0;
            int switchHints = 0;
            
            for (int i = 0; i < instructions.Count - 3; i++)
            {
                // Паттерн: stloc (состояние) + сравнение
                if (IsStloc(instructions[i + 1], out _) && 
                    GetConstantValue(instructions[i]) != null)
                {
                    stateVarHints++;
                }
                
                // Паттерн: switch
                if (instructions[i].OpCode.Code == Code.Switch)
                {
                    switchHints++;
                }
            }
            
            return stateVarHints > 2 || switchHints > 0;
        }

        private bool UnpackStateMachineSafe(MethodDef method)
        {
            var backup = method.Body.Instructions.ToList();
            
            try
            {
                bool result = UnpackStateMachine(method);
                
                // Проверяем, не испортили ли мы метод
                if (result && method.Body.Instructions.Count > 0)
                {
                    // Убеждаемся, что метод заканчивается на ret или throw
                    var lastInstr = method.Body.Instructions.LastOrDefault();
                    if (lastInstr != null && 
                        lastInstr.OpCode.Code != Code.Ret && 
                        lastInstr.OpCode.Code != Code.Throw)
                    {
                        Log("Method doesn't end with ret/throw, restoring...");
                        RestoreMethod(method, backup);
                        return false;
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log($"UnpackStateMachine failed: {ex.Message}");
                RestoreMethod(method, backup);
            }
            
            return false;
        }

        private bool UnpackStateMachine(MethodDef method)
        {
            var body = method.Body;
            if (body == null || body.Instructions.Count < 5) return false;

            var instructions = body.Instructions;
            
            // 1. Поиск переменной состояния
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
            var stateBlocks = new Dictionary<object, List<Instruction>>();
            var transitions = new Dictionary<object, object?>();
            
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
                        var block = ExtractBlock(instructions, target, stateVarIndex, out object? nextVal);
                        
                        if (!stateBlocks.ContainsKey(checkVal) && block.Count > 0)
                        {
                            stateBlocks[checkVal] = block;
                            transitions[checkVal] = nextVal;
                            Log($"Found block for state {checkVal}. Instructions: {block.Count}");
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

            while (queue.Count > 0)
            {
                var currentState = queue.Dequeue();
                if (visited.Contains(currentState)) continue;
                visited.Add(currentState);

                if (stateBlocks.TryGetValue(currentState, out var block))
                {
                    finalInstructions.AddRange(block.Select(CloneInstruction));
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

            // Добавляем ret если его нет
            if (finalInstructions.LastOrDefault()?.OpCode.Code != Code.Ret &&
                finalInstructions.LastOrDefault()?.OpCode.Code != Code.Throw)
            {
                finalInstructions.Add(Instruction.Create(OpCodes.Ret));
                Log("Added missing ret instruction");
            }

            ReplaceMethodBody(method, finalInstructions);
            return true;
        }

        private void SimplifyConditionsSafe(MethodDef method)
        {
            var backup = method.Body.Instructions.ToList();
            
            try
            {
                SimplifyConditions(method);
                
                // Если метод стал пустым после упрощения, восстанавливаем
                if (method.Body.Instructions.Count == 0)
                {
                    Log("SimplifyConditions made method empty, restoring...");
                    RestoreMethod(method, backup);
                }
            }
            catch (Exception ex)
            {
                Log($"SimplifyConditions failed: {ex.Message}");
                RestoreMethod(method, backup);
            }
        }

        private void SimplifyConditions(MethodDef method)
        {
            var body = method.Body;
            var instructions = body.Instructions;
            bool changed = true;
            int passes = 0;

            while (changed && passes < 20 && instructions.Count > 0)
            {
                changed = false;
                passes++;
                
                var knownValues = new Dictionary<int, object?>();
                
                for (int i = 0; i < instructions.Count - 1; i++)
                {
                    var instr = instructions[i];
                    if (IsStloc(instructions[i + 1], out int idx))
                    {
                        var val = GetConstantValue(instr);
                        if (val != null) knownValues[idx] = val;
                    }
                }

                for (int i = 0; i < instructions.Count; i++)
                {
                    var instr = instructions[i];
                    if (instr.OpCode.FlowControl == FlowControl.Cond_Branch && i > 0)
                    {
                        bool? res = TryEvalSimpleCondition(instructions, i, knownValues);
                        if (res.HasValue && instructions.Count > 1)
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
                
                if (changed && instructions.Count > 0) 
                    body.UpdateInstructionOffsets();
            }
            
            if (instructions.Count > 0)
                RemoveUnreachableBlocks(method);
        }

        private void FixStackImbalance(MethodDef method)
        {
            var body = method.Body;
            if (body == null) return;
            var instrs = body.Instructions;
            
            // Не обрабатываем слишком маленькие методы
            if (instrs.Count < 3) return;
            
            bool changed = true;
            int maxPasses = 5;
            int passes = 0;
            
            while (changed && passes < maxPasses && instrs.Count > 0)
            {
                changed = false;
                passes++;
                
                for (int i = instrs.Count - 1; i >= 0; i--)
                {
                    var instr = instrs[i];
                    
                    // Не удаляем критические инструкции
                    if (instr.OpCode.Code == Code.Ret || 
                        instr.OpCode.Code == Code.Throw ||
                        instr.OpCode.Code == Code.Call ||
                        instr.OpCode.Code == Code.Callvirt)
                        continue;
                    
                    // Проверяем, не является ли инструкция целевой для перехода
                    bool isTarget = instrs.Any(ins => 
                        (ins.Operand == instr) || 
                        (ins.Operand is Instruction[] arr && arr.Contains(instr)));
                    
                    if (isTarget) continue;
                    
                    // Удаляем только изолированные NOPs
                    if (instr.OpCode.Code == Code.Nop && !isTarget)
                    {
                        instrs.RemoveAt(i);
                        changed = true;
                        continue;
                    }
                    
                    // Удаляем неиспользуемые константы только если они точно не нужны
                    if ((instr.OpCode.Code == Code.Ldc_I4 || instr.OpCode.Code == Code.Ldc_I8) && 
                        i + 1 < instrs.Count)
                    {
                        var next = instrs[i + 1];
                        // Удаляем только если следующая инструкция - NOP или другая константа
                        if (next.OpCode.Code == Code.Nop || 
                            next.OpCode.Code == Code.Ldc_I4 ||
                            next.OpCode.Code == Code.Ldc_I8)
                        {
                            instrs.RemoveAt(i);
                            changed = true;
                        }
                    }
                }
            }
            
            if (changed && instrs.Count > 0)
            {
                body.UpdateInstructionOffsets();
                body.SimplifyMacros(method.Parameters);
            }
        }

        private void CleanupNopsSafe(MethodDef method)
        {
            var backup = method.Body.Instructions.ToList();
            
            try
            {
                CleanupNops(method);
                
                if (method.Body.Instructions.Count == 0)
                {
                    Log("CleanupNops made method empty, restoring...");
                    RestoreMethod(method, backup);
                }
            }
            catch (Exception ex)
            {
                Log($"CleanupNops failed: {ex.Message}");
                RestoreMethod(method, backup);
            }
        }

        private void CleanupNops(MethodDef method)
        {
            var body = method.Body;
            var instrs = body.Instructions;
            bool changed = true;
            
            while (changed && instrs.Count > 0)
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
            
            if (instrs.Count > 0)
            {
                body.UpdateInstructionOffsets();
                body.SimplifyMacros(method.Parameters);
            }
        }

        private void RestoreMethod(MethodDef method, List<Instruction> backupInstructions)
        {
            var body = method.Body;
            body.Instructions.Clear();
            foreach (var instr in backupInstructions)
            {
                body.Instructions.Add(CloneInstruction(instr));
            }
            body.UpdateInstructionOffsets();
            body.SimplifyMacros(method.Parameters);
        }

        private List<Instruction> ExtractBlock(IList<Instruction> allInstructions, Instruction startInstr, int stateVarIndex, out object? nextState)
        {
            var block = new List<Instruction>();
            nextState = null;

            int startIndex = allInstructions.IndexOf(startInstr);
            if (startIndex == -1) return block;

            int ip = startIndex;
            int maxBlockLen = 100; 
            int count = 0;

            while (ip < allInstructions.Count && count < maxBlockLen)
            {
                var instr = allInstructions[ip];
                count++;

                if (ip + 1 < allInstructions.Count)
                {
                    var nextIns = allInstructions[ip+1];
                    if (IsStloc(nextIns, out int sIdx) && sIdx == stateVarIndex)
                    {
                        var val = GetConstantValue(instr);
                        if (val != null)
                        {
                            nextState = val;
                            break; 
                        }
                    }
                }

                if (instr.OpCode.FlowControl == FlowControl.Return)
                {
                    block.Add(CloneInstruction(instr));
                    break;
                }
                
                if (instr.OpCode.FlowControl == FlowControl.Branch && instr.Operand is Instruction t)
                {
                    int tIdx = allInstructions.IndexOf(t);
                    if (tIdx > ip) 
                        break;
                }

                // Добавляем инструкцию в блок, пропуская только явный мусор
                bool isJunk = false;

                if (GetLocalIndex(instr) == stateVarIndex && 
                   (instr.OpCode.Code == Code.Ldloc || instr.OpCode.Code == Code.Ldloc_S ||
                    (instr.OpCode.Code >= Code.Ldloc_0 && instr.OpCode.Code <= Code.Ldloc_3)))
                {
                    isJunk = true;
                }

                if (!isJunk && (instr.OpCode.Code == Code.Ceq || instr.OpCode.Code == Code.Cgt || 
                    instr.OpCode.Code == Code.Clt || instr.OpCode.Code == Code.Cgt_Un || 
                    instr.OpCode.Code == Code.Clt_Un))
                {
                    isJunk = true;
                }

                if (!isJunk && (instr.OpCode.FlowControl == FlowControl.Cond_Branch))
                {
                    isJunk = true;
                }

                if (!isJunk)
                {
                    block.Add(CloneInstruction(instr));
                }

                ip++;
            }

            return block;
        }

        // Остальные вспомогательные методы остаются без изменений
        private bool IsStloc(Instruction i, out int idx) { /* ... */ }
        private bool IsLdloc(Instruction i, out int idx) { /* ... */ }
        private int GetLocalIndex(Instruction i) { /* ... */ }
        private object? GetConstantValue(Instruction i) { /* ... */ }
        private bool IsObfuscatedName(string n) { /* ... */ }
        private void RenameWithAi(MethodDef m) { /* ... */ }
        private bool IsValidIdentifier(string n) { /* ... */ }
        private Instruction CloneInstruction(Instruction orig) { /* ... */ }
        private void ReplaceMethodBody(MethodDef m, List<Instruction> newInstrs) { /* ... */ }
        private void RemoveUnreachableBlocks(MethodDef method) { /* ... */ }
        private bool? TryEvalSimpleCondition(IList<Instruction> instrs, int idx, Dictionary<int, object?> known) { /* ... */ }
        private bool? CompareValues(object? a, object? b, Code op) { /* ... */ }

        public void Save(string path)
        {
            string saveMsg = $"[*] Saving to: {path}";
            Console.WriteLine(saveMsg);
            Log(saveMsg);
            
            var opts = new ModuleWriterOptions(_module)
            {
                Logger = DummyLogger.NoThrowInstance,
                MetadataOptions = new MetadataOptions { Flags = MetadataFlags.KeepOldMaxStack }
            };
            _module.Write(path, opts);
            
            string doneMsg = "[+] Done.";
            Console.WriteLine(doneMsg);
            Log(doneMsg);
            
            _logWriter?.Close();
        }

        public void Dispose()
        {
            _aiAssistant?.Dispose();
            _module.Dispose();
            _logWriter?.Dispose();
        }
    }
}
