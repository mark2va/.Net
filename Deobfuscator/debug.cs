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
            
            foreach (var type in _module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody || !method.Body.HasInstructions) continue;
                    
                    totalMethods++;
                    Log($"Processing method: {method.FullName}");
                    _indentLevel++;
                    
                    var backupInstructions = method.Body.Instructions.ToList();
                    int originalCount = backupInstructions.Count;
                    Log($"Original instructions: {originalCount}");

                    try
                    {
                        bool wasModified = false;

                        // Применяем упрощение потока управления (Control Flow Simplification)
                        // Это основной метод борьбы с state-machine обфускацией
                        if (SimplifyControlFlow(method))
                        {
                            wasModified = true;
                            Log($"Control flow simplified. Instructions: {originalCount} -> {method.Body.Instructions.Count}");
                            
                            // Дополнительная очистка nop после упрощения
                            CleanupNops(method);
                            
                            // Удаление мертвого кода (недостижимых блоков)
                            RemoveUnreachableBlocks(method);
                            
                            // Финальная пересборка для исправления стека
                            method.Body.UpdateInstructionOffsets();
                            method.Body.SimplifyMacros(method.Parameters);
                        }
                        
                        if (wasModified)
                        {
                            count++;
                            Log($"Method successfully processed. Final instructions: {method.Body.Instructions.Count}");
                            
                            if (_debugMode && method.Body.Instructions.Count > 0)
                            {
                                Log("First 15 instructions after processing:");
                                _indentLevel++;
                                for (int i = 0; i < Math.Min(15, method.Body.Instructions.Count); i++)
                                {
                                    Log(method.Body.Instructions[i].ToString());
                                }
                                _indentLevel--;
                            }
                        }
                        else
                        {
                            Log("No changes made to method.");
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
            
            string doneMsg = $"[*] Processed {totalMethods} methods, modified {count} methods.";
            Console.WriteLine(doneMsg);
            Log(doneMsg);
            Log("=== Deobfuscation Finished ===");
        }

        /// <summary>
        /// Основной алгоритм упрощения потока управления (вдохновлен de4dot).
        /// Находит переменную состояния, вычисляет её значение символьно и упрощает ветвления.
        /// </summary>
        private bool SimplifyControlFlow(MethodDef method)
        {
            var body = method.Body;
            if (body == null || body.Instructions.Count < 10) return false;

            // 1. Поиск переменной состояния (State Variable)
            // Ищем локаль, которая часто используется в паттернах: stloc, ldloc + ldc + ceq
            int? stateVarIndex = FindStateVariable(method);
            
            if (!stateVarIndex.HasValue)
            {
                Log("No state variable found, skipping control flow simplification.");
                return false;
            }

            Log($"Found state variable: V_{stateVarIndex.Value}");

            bool changed = true;
            int maxPasses = 20;
            int pass = 0;

            while (changed && pass < maxPasses)
            {
                changed = false;
                pass++;

                // 2. Символьное выполнение для вычисления значений переменной состояния
                // Мы не запускаем код, а отслеживаем возможные значения переменной в каждой точке
                var stateValues = SymbolicExecuteState(method, stateVarIndex.Value);

                // 3. Упрощение условий на основе вычисленных значений
                if (SimplifyBranches(method, stateValues, stateVarIndex.Value))
                {
                    changed = true;
                    Log($"Pass {pass}: Branches simplified.");
                }

                // 4. Удаление инструкций записи в переменную состояния (они теперь лишние)
                if (RemoveStateStores(method, stateVarIndex.Value))
                {
                    changed = true;
                    Log($"Pass {pass}: State stores removed.");
                }
                
                // Обновляем оффсеты после изменений
                if (changed)
                {
                    body.UpdateInstructionOffsets();
                }
            }

            return pass > 1; // Если был хотя бы один проход упрощения
        }

        /// <summary>
        /// Находит индекс локальной переменной, используемой как состояние.
        /// </summary>
        private int? FindStateVariable(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            var usageCount = new Dictionary<int, int>();

            // Подсчитываем использования локальных переменных в паттернах сравнения
            for (int i = 0; i < instructions.Count - 3; i++)
            {
                // Паттерн: ldloc X, ldc Y, ceq, br...
                if (IsLdloc(instructions[i], out int idx) &&
                    (instructions[i+1].OpCode.Code == Code.Ldc_I4 || instructions[i+1].OpCode.Code == Code.Ldc_R8 || instructions[i+1].OpCode.Code == Code.Ldc_I8) &&
                    (instructions[i+2].OpCode.Code == Code.Ceq || instructions[i+2].OpCode.Code == Code.Cgt || instructions[i+2].OpCode.Code == Code.Clt))
                {
                    if (!usageCount.ContainsKey(idx)) usageCount[idx] = 0;
                    usageCount[idx]++;
                }
            }

            // Переменная считается состоянием, если участвует в >= 3 таких сравнениях
            foreach (var kvp in usageCount)
            {
                if (kvp.Value >= 3)
                {
                    Log($"Candidate state variable V_{kvp.Key} with {kvp.Value} comparisons.");
                    return kvp.Key;
                }
            }

            return null;
        }

        /// <summary>
        /// Символьное выполнение: определяет значение переменной состояния перед каждой инструкцией.
        /// Возвращает словарь: Index инструкции -> Значение переменной (если известно).
        /// </summary>
        private Dictionary<int, object?> SymbolicExecuteState(MethodDef method, int stateVarIndex)
        {
            var stateValues = new Dictionary<int, object?>();
            var instructions = method.Body.Instructions;
            
            // Карта: Instruction -> известное значение состояния ПЕРЕД этой инструкцией
            // Используем очередь для обхода графа потока (BFS)
            var queue = new Queue<Tuple<Instruction, object?>>();
            
            // Начальное значение: ищем первое присваивание stloc stateVar в начале метода
            object? initialState = null;
            for (int i = 0; i < Math.Min(20, instructions.Count); i++)
            {
                if (IsStloc(instructions[i], out int idx) && idx == stateVarIndex)
                {
                    if (i > 0)
                    {
                        initialState = GetConstantValue(instructions[i-1]);
                        if (initialState != null)
                        {
                            // Значение известно после инструкции stloc
                            if (i + 1 < instructions.Count)
                            {
                                queue.Enqueue(Tuple.Create(instructions[i+1], initialState));
                            }
                            break;
                        }
                    }
                }
            }

            if (initialState == null) return stateValues;

            var visited = new HashSet<Instruction>();

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var instr = current.Item1;
                var currentState = current.Item2;

                if (visited.Contains(instr)) continue;
                visited.Add(instr);

                int idx = instructions.IndexOf(instr);
                if (idx != -1 && !stateValues.ContainsKey(idx))
                {
                    stateValues[idx] = currentState;
                }

                // Эмулируем переход к следующей инструкции
                if (instr.OpCode.Code == Code.Br || instr.OpCode.Code == Code.Br_S)
                {
                    if (instr.Operand is Instruction target)
                        queue.Enqueue(Tuple.Create(target, currentState));
                }
                else if (instr.OpCode.FlowControl == FlowControl.Cond_Branch)
                {
                    // Если условие зависит от состояния, которое мы знаем, мы можем решить, куда идти.
                    // Но здесь мы просто распространяем текущее состояние по всем веткам,
                    // так как точное условие проверится в SimplifyBranches.
                    if (instr.Operand is Instruction target)
                        queue.Enqueue(Tuple.Create(target, currentState));
                    
                    // И переход к следующей инструкции (если ветвление не безусловное)
                    if (instr.OpCode.Code != Code.Brtrue && instr.OpCode.Code != Code.Brfalse && 
                        instr.OpCode.Code != Code.Brtrue_S && instr.OpCode.Code != Code.Brfalse_S)
                    {
                         // Для switch и других сложных ветвлений
                         // В простом случае state machine обычно brtrue/brfalse
                    }
                    
                    // Для brtrue/brfalse добавляем и следующую инструкцию (случай false/true соответственно)
                    // Так как мы еще не упростили код, идем по обоим путям с тем же значением состояния
                    int nextIdx = idx + 1;
                    if (nextIdx < instructions.Count)
                        queue.Enqueue(Tuple.Create(instructions[nextIdx], currentState));
                }
                else
                {
                    // Обычная инструкция: идем дальше
                    // Проверяем, не меняет ли она состояние
                    object? nextState = currentState;
                    
                    if (IsStloc(instr, out int sIdx) && sIdx == stateVarIndex)
                    {
                        // Нашли stloc stateVar. Берем значение из предыдущей инструкции
                        if (idx > 0)
                        {
                            var val = GetConstantValue(instructions[idx-1]);
                            if (val != null) nextState = val;
                        }
                    }
                    
                    int nextIdx = idx + 1;
                    if (nextIdx < instructions.Count)
                        queue.Enqueue(Tuple.Create(instructions[nextIdx], nextState));
                }
            }

            return stateValues;
        }

        /// <summary>
        /// Упрощает ветвления, заменяя их на br или nop, если условие можно вычислить.
        /// </summary>
        private bool SimplifyBranches(MethodDef method, Dictionary<int, object?> stateValues, int stateVarIndex)
        {
            var instructions = method.Body.Instructions;
            bool changed = false;

            for (int i = 0; i < instructions.Count - 3; i++)
            {
                var branchInstr = instructions[i];
                
                // Проверяем, является ли инструкция условным переходом
                if (branchInstr.OpCode.Code == Code.Brtrue || branchInstr.OpCode.Code == Code.Brfalse ||
                    branchInstr.OpCode.Code == Code.Brtrue_S || branchInstr.OpCode.Code == Code.Brfalse_S)
                {
                    // Смотрим назад, чтобы найти условие сравнения
                    // Обычно: ldloc, ldc, ceq, brtrue
                    if (i >= 3)
                    {
                        var cmpInstr = instructions[i-1]; // ceq
                        var ldcInstr = instructions[i-2]; // ldc
                        var ldlocInstr = instructions[i-3]; // ldloc

                        if (GetLocalIndex(ldlocInstr) == stateVarIndex &&
                            (cmpInstr.OpCode.Code == Code.Ceq || cmpInstr.OpCode.Code == Code.Cgt || cmpInstr.OpCode.Code == Code.Clt))
                        {
                            // Получаем известное значение состояния ПЕРЕД ldloc (то есть перед началом этого блока проверки)
                            // В нашем символическом выполнении мы сохранили значение для индекса i-3
                            if (stateValues.TryGetValue(i-3, out object? currentState))
                            {
                                var compareValue = GetConstantValue(ldcInstr);
                                
                                if (currentState != null && compareValue != null)
                                {
                                    bool result = CompareValues(currentState, compareValue, cmpInstr.OpCode.Code);
                                    
                                    if (result)
                                    {
                                        // Условие истинно: заменяем на безусловный переход (br)
                                        Log($"  Simplifying branch at {i}: condition TRUE -> br");
                                        branchInstr.OpCode = OpCodes.Br;
                                        if (branchInstr.OpCode.Code == Code.Brtrue_S || branchInstr.OpCode.Code == Code.Brfalse_S)
                                            branchInstr.OpCode = OpCodes.Br_S;
                                        else
                                            branchInstr.OpCode = OpCodes.Br;
                                        changed = true;
                                    }
                                    else
                                    {
                                        // Условие ложно: заменяем на nop (переход не выполняется)
                                        Log($"  Simplifying branch at {i}: condition FALSE -> nop");
                                        branchInstr.OpCode = OpCodes.Nop;
                                        branchInstr.Operand = null;
                                        changed = true;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return changed;
        }

        /// <summary>
        /// Удаляет инструкции записи в переменную состояния (stloc), так как они больше не влияют на поток.
        /// Заменяет их на nop.
        /// </summary>
        private bool RemoveStateStores(MethodDef method, int stateVarIndex)
        {
            var instructions = method.Body.Instructions;
            bool changed = false;

            for (int i = 0; i < instructions.Count; i++)
            {
                var instr = instructions[i];
                if (IsStloc(instr, out int idx) && idx == stateVarIndex)
                {
                    // Также удаляем константу перед ним, если она есть (ldc)
                    if (i > 0 && (instructions[i-1].OpCode.Code == Code.Ldc_I4 || instructions[i-1].OpCode.Code == Code.Ldc_R8 || instructions[i-1].OpCode.Code == Code.Ldc_I8))
                    {
                        instructions[i-1].OpCode = OpCodes.Nop;
                        instructions[i-1].Operand = null;
                    }
                    
                    instr.OpCode = OpCodes.Nop;
                    instr.Operand = null;
                    changed = true;
                }
            }

            return changed;
        }

        private void RemoveUnreachableBlocks(MethodDef method)
        {
            var body = method.Body;
            var instrs = body.Instructions;
            if (instrs.Count == 0) return;

            var reachable = new HashSet<Instruction>();
            var q = new Queue<Instruction>();

            // Начинаем с первой инструкции
            if (instrs.Count > 0)
            {
                q.Enqueue(instrs[0]);
                reachable.Add(instrs[0]);
            }

            while (q.Count > 0)
            {
                var curr = q.Dequeue();
                int idx = instrs.IndexOf(curr);
                if (idx == -1) continue;

                // Если это не безусловный переход и не ret/throw, то следующая инструкция достижима
                if (curr.OpCode.Code != Code.Br && curr.OpCode.Code != Code.Br_S &&
                    curr.OpCode.Code != Code.Ret && curr.OpCode.Code != Code.Throw)
                {
                    if (idx + 1 < instrs.Count)
                    {
                        var next = instrs[idx + 1];
                        if (reachable.Add(next)) q.Enqueue(next);
                    }
                }

                // Добавляем целевые инструкции переходов
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
                    // Заменяем недостижимый код на nop
                    instrs[i].OpCode = OpCodes.Nop;
                    instrs[i].Operand = null;
                    changed = true;
                }
            }
            
            if (changed)
            {
                Log("Removed unreachable blocks.");
                body.UpdateInstructionOffsets();
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
                case Code.Ldc_I8: return i.Operand as long?;
                case Code.Ldc_R4: return i.Operand as float?;
                case Code.Ldc_R8: return i.Operand as double?;
                case Code.Ldstr: return i.Operand as string;
                case Code.Ldnull: return null;
                default: return null;
            }
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

        #endregion

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
