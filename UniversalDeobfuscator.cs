using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

namespace Deobfuscator
{
    public class UniversalDeobfuscator : IDisposable
    {
        private readonly ModuleDefMD _module;
        private readonly AiConfig _aiConfig;
        private readonly bool _debugMode;
        private StreamWriter? _logWriter;
        private int _renameCounter = 0;

        public UniversalDeobfuscator(string filePath, AiConfig aiConfig, bool debugMode = false)
        {
            _module = ModuleDefMD.Load(filePath);
            _aiConfig = aiConfig;
            _debugMode = debugMode;

            if (_debugMode)
            {
                string dir = Path.GetDirectoryName(filePath) ?? Directory.GetCurrentDirectory();
                string logPath = Path.Combine(dir, "deob_log.txt");
                _logWriter = new StreamWriter(logPath, false);
                _logWriter.AutoFlush = true;
                Log("=== Deobfuscation Engine Started ===");
                Log($"Target: {filePath}");
            }
        }

        private void Log(string msg)
        {
            if (!_debugMode) return;
            string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            Console.WriteLine(line);
            _logWriter?.WriteLine(line);
        }

        public void Deobfuscate()
        {
            Console.WriteLine("[*] Phase 1: Unraveling Control Flow (State Machine)...");
            Log("Starting control flow unraveling...");
            int unraveledCount = UnravelAllStateMachines();
            Console.WriteLine($"[+] Unraveled {unraveledCount} methods.");
            Log($"Unraveled {unraveledCount} methods.");

            Console.WriteLine("[*] Phase 2: Cleaning Dead Code & Nops...");
            Log("Starting cleanup...");
            int cleanedCount = CleanupAll();
            Console.WriteLine($"[+] Cleaned {cleanedCount} methods.");
            Log($"Cleaned {cleanedCount} methods.");

            Console.WriteLine("[*] Phase 3: Renaming Obfuscated Items...");
            Log("Starting renaming...");
            RenameAllItems();
            
            Log("=== Deobfuscation Finished ===");
        }

        #region Control Flow Unraveling (Symbolic Execution)

        private int UnravelAllStateMachines()
        {
            int count = 0;
            foreach (var type in _module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody || !method.Body.IsIL) continue;
                    if (method.Body.Instructions.Count < 10) continue;

                    try
                    {
                        if (UnravelStateMachine(method))
                        {
                            count++;
                            Log($"Unraveled: {method.FullName}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Error in {method.FullName}: {ex.Message}");
                    }
                }
            }
            return count;
        }

        private bool UnravelStateMachine(MethodDef method)
        {
            var body = method.Body;
            var instructions = body.Instructions;

            // 1. Поиск переменной состояния (State Variable)
            // Ищем локаль, которая часто записывается константами и сравнивается
            int? stateVarIndex = FindStateVariable(method);
            if (!stateVarIndex.HasValue)
                return false; // Не похоже на стандартный state machine

            Log($"  Found state variable: V{stateVarIndex.Value}");

            // 2. Символьное выполнение для определения значения стейта в каждой точке
            // Map: Index инструкции -> Значение стейта ПЕРЕД выполнением этой инструкции
            var stateMap = new Dictionary<int, object?>();
            
            // Очередь для обхода графа: (Индекс инструкции, Значение стейта)
            var queue = new Queue<Tuple<int, object?>>();
            
            // Попытка найти начальное значение (обычно в начале метода: ldc -> stloc)
            object? initialState = null;
            for (int i = 0; i < Math.Min(20, instructions.Count); i++)
            {
                if (IsStloc(instructions[i], out int idx) && idx == stateVarIndex.Value)
                {
                    if (i > 0)
                    {
                        initialState = GetConstantValue(instructions[i - 1]);
                        if (initialState != null)
                        {
                            // Запускаем эмуляцию с следующей инструкции
                            queue.Enqueue(Tuple.Create(i + 1, initialState));
                            break;
                        }
                    }
                }
            }

            if (initialState == null) return false;

            var visited = new HashSet<Tuple<int, object?>>();

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                int ip = current.Item1;
                object? currentState = current.Item2;

                if (ip < 0 || ip >= instructions.Count) continue;
                
                var key = Tuple.Create(ip, currentState);
                if (visited.Contains(key)) continue;
                visited.Add(key);

                // Сохраняем состояние для этой точки
                if (!stateMap.ContainsKey(ip))
                    stateMap[ip] = currentState;
                else
                {
                    // Если уже есть другое значение, значит ветвление сходится, помечаем как unknown?
                    // Для простоты оставим первое найденное или попробуем объединить, но пока оставим так.
                }

                var instr = instructions[ip];

                // Эмуляция перехода
                object? nextState = currentState;

                // Если это запись в переменную состояния, обновляем nextState
                if (IsStloc(instr, out int sIdx) && sIdx == stateVarIndex.Value)
                {
                    // Смотрим инструкцию перед записью (в стеке эмуляции сложно, упростим: берем из prev)
                    // В полноценном эмуляторе нужно вести стек. Здесь упрощение:
                    // Если перед stloc лежит ldc, мы уже знаем значение.
                    if (ip > 0)
                    {
                        var val = GetConstantValue(instructions[ip - 1]);
                        if (val != null) nextState = val;
                    }
                }

                // Обработка переходов
                if (instr.OpCode.FlowControl == FlowControl.Branch)
                {
                    if (instr.Operand is Instruction target)
                    {
                        int targetIp = instructions.IndexOf(target);
                        if (targetIp != -1)
                            queue.Enqueue(Tuple.Create(targetIp, nextState));
                    }
                }
                else if (instr.OpCode.FlowControl == FlowControl.Cond_Branch)
                {
                    // Пытаемся вычислить условие на основе currentState
                    bool? conditionResult = EvaluateConditionAt(instr, ip, currentState, stateVarIndex.Value, instructions);

                    if (conditionResult.HasValue)
                    {
                        // Условие известно!
                        if (conditionResult.Value)
                        {
                            // Переход истинен
                            if (instr.Operand is Instruction t)
                            {
                                int tIp = instructions.IndexOf(t);
                                if (tIp != -1) queue.Enqueue(Tuple.Create(tIp, nextState));
                            }
                        }
                        else
                        {
                            // Переход ложен -> идем дальше
                            queue.Enqueue(Tuple.Create(ip + 1, nextState));
                        }
                    }
                    else
                    {
                        // Не смогли вычислить (зависит от внешних данных) -> идем по обоим путям
                        if (instr.Operand is Instruction t)
                        {
                            int tIp = instructions.IndexOf(t);
                            if (tIp != -1) queue.Enqueue(Tuple.Create(tIp, nextState));
                        }
                        queue.Enqueue(Tuple.Create(ip + 1, nextState));
                    }
                }
                else if (instr.OpCode.FlowControl == FlowControl.Ret || instr.OpCode.FlowControl == FlowControl.Throw)
                {
                    // Конец пути
                }
                else
                {
                    // Обычная инструкция
                    queue.Enqueue(Tuple.Create(ip + 1, nextState));
                }
            }

            // 3. Упрощение кода на основе stateMap
            bool changed = false;
            for (int i = 0; i < instructions.Count; i++)
            {
                var instr = instructions[i];
                
                // Если мы знаем значение стейта в этой точке, попробуем упростить ветвления
                if (stateMap.TryGetValue(i, out object? knownState))
                {
                    if (instr.OpCode.FlowControl == FlowControl.Cond_Branch)
                    {
                        bool? res = EvaluateConditionAt(instr, i, knownState, stateVarIndex.Value, instructions);
                        if (res.HasValue)
                        {
                            if (res.Value)
                            {
                                // Заменяем условный переход на безусловный
                                instr.OpCode = OpCodes.Br;
                                Log($"    Simplified branch at {i} to TRUE");
                                changed = true;
                            }
                            else
                            {
                                // Заменяем на NOP (переход не выполняется)
                                instr.OpCode = OpCodes.Nop;
                                instr.Operand = null;
                                Log($"    Removed branch at {i} (FALSE)");
                                changed = true;
                            }
                        }
                    }
                }

                // Удаляем инструкции обновления стейта (ldc + stloc), если они больше не нужны
                // Это делается аккуратно, чтобы не сломать логику, если стейт используется где-то еще
                // Но в обфускаторах стейт обычно только для потока.
                if (IsStloc(instr, out int sIdx) && sIdx == stateVarIndex.Value)
                {
                     // Помечаем на удаление (замена на nop будет в Cleanup)
                     // Но лучше сразу заменить, если уверены. 
                     // Оставим пока, пусть Cleanup удалит unreachable, а потом удалим сами stloc если они стали лишними
                }
            }

            if (changed)
            {
                body.UpdateInstructionOffsets();
                return true;
            }

            return false;
        }

        private int? FindStateVariable(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            var counts = new Dictionary<int, int>();

            for (int i = 0; i < instructions.Count - 3; i++)
            {
                // Паттерн: ldloc X, ldc Y, ceq, br...
                if (IsLdloc(instructions[i], out int idx))
                {
                    var next = instructions[i + 1];
                    var next2 = instructions[i + 2];
                    
                    if ((next.OpCode.Code == Code.Ldc_I4 || next.OpCode.Code == Code.Ldc_I8) &&
                        (next2.OpCode.Code == Code.Ceq || next2.OpCode.Code == Code.Cgt || next2.OpCode.Code == Code.Clt))
                    {
                        if (!counts.ContainsKey(idx)) counts[idx] = 0;
                        counts[idx]++;
                    }
                }
            }

            // Переменная с наибольшим количеством сравнений (минимум 3)
            int bestVar = -1;
            int maxCount = 2;
            foreach (var kvp in counts)
            {
                if (kvp.Value > maxCount)
                {
                    maxCount = kvp.Value;
                    bestVar = kvp.Key;
                }
            }

            return bestVar != -1 ? bestVar : (int?)null;
        }

        private bool? EvaluateConditionAt(Instruction branchInstr, int ip, object? currentState, int stateVarIndex, IList<Instruction> instructions)
        {
            // Смотрим назад, чтобы найти сравнение
            // Обычно: ... ldloc(state), ldc(val), ceq, brtrue ...
            if (ip < 3) return null;

            var cmpInstr = instructions[ip - 1]; // ceq/cgt/clt
            var ldcInstr = instructions[ip - 2]; // ldc
            var ldlocInstr = instructions[ip - 3]; // ldloc

            if (GetLocalIndex(ldlocInstr) != stateVarIndex) return null;
            if (cmpInstr.OpCode.Code != Code.Ceq && cmpInstr.OpCode.Code != Code.Cgt && cmpInstr.OpCode.Code != Code.Clt) return null;

            var constVal = GetConstantValue(ldcInstr);
            if (constVal == null || currentState == null) return null;

            return CompareValues(currentState, constVal, cmpInstr.OpCode.Code);
        }

        #endregion

        #region Cleanup

        private int CleanupAll()
        {
            int count = 0;
            foreach (var type in _module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody) continue;
                    
                    bool changed = true;
                    while (changed)
                    {
                        changed = false;
                        if (RemoveUnreachableBlocks(method)) changed = true;
                        if (CleanupNops(method)) changed = true;
                        if (RemoveDeadStores(method)) changed = true;
                    }
                    
                    if (changed) count++;
                }
            }
            return count;
        }

        private bool RemoveUnreachableBlocks(MethodDef method)
        {
            var body = method.Body;
            var instrs = body.Instructions;
            if (instrs.Count == 0) return false;

            var reachable = new HashSet<Instruction>();
            var q = new Queue<Instruction>();

            q.Enqueue(instrs[0]);
            reachable.Add(instrs[0]);

            while (q.Count > 0)
            {
                var curr = q.Dequeue();
                int idx = instrs.IndexOf(curr);
                if (idx == -1) continue;

                // Next instruction
                if (curr.OpCode.FlowControl != FlowControl.Branch &&
                    curr.OpCode.FlowControl != FlowControl.Ret &&
                    curr.OpCode.FlowControl != FlowControl.Throw)
                {
                    if (idx + 1 < instrs.Count)
                    {
                        var next = instrs[idx + 1];
                        if (reachable.Add(next)) q.Enqueue(next);
                    }
                }

                // Targets
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
            return changed;
        }

        private bool CleanupNops(MethodDef method)
        {
            var body = method.Body;
            bool changed = false;
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
            if (changed) body.UpdateInstructionOffsets();
            return changed;
        }

        private bool RemoveDeadStores(MethodDef method)
        {
            // Удаляет простые ldc + stloc, если stloc больше нигде не читается (упрощенно)
            // В рамках state machine можно удалить все stloc переменной состояния, если она больше не используется для ветвлений
            // Но это сложно определить безопасно. Оставим базовую очистку.
            return false; 
        }

        #endregion

        #region Renaming

        private void RenameAllItems()
        {
            bool useAi = _aiConfig.Enabled;
            AiAssistant? ai = null;

            if (useAi)
            {
                try
                {
                    ai = new AiAssistant(_aiConfig);
                    if (!ai.IsConnected)
                    {
                        Console.WriteLine("[!] AI Connection Failed. Using fallback naming.");
                        useAi = false;
                        ai.Dispose();
                        ai = null;
                    }
                    else
                    {
                        Console.WriteLine($"[+] AI Connected: {_aiConfig.ModelName}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[!] AI Error: {ex.Message}. Using fallback.");
                    useAi = false;
                    ai?.Dispose();
                    ai = null;
                }
            }

            int renamedTypes = 0;
            int renamedMethods = 0;

            foreach (var type in _module.GetTypes())
            {
                if (IsObfuscatedName(type.Name))
                {
                    string newName = "Class_" + (_renameCounter++).ToString("D2");
                    
                    if (useAi && ai != null)
                    {
                        // Попытка получить имя для класса (по первому методу или заглушке)
                        string ctx = type.Methods.FirstOrDefault(m => m.HasBody)?.FullName ?? "";
                        string aiName = ai.GetSuggestedName(type.Name, ctx.Substring(0, Math.Min(100, ctx.Length)), "Class");
                        if (!string.IsNullOrEmpty(aiName) && IsValidIdentifier(aiName) && !aiName.StartsWith("Class_"))
                            newName = aiName;
                    }
                    
                    type.Name = newName;
                    renamedTypes++;
                }

                foreach (var method in type.Methods)
                {
                    if (method.IsConstructor || method.IsStaticConstructor) continue;
                    if (IsObfuscatedName(method.Name))
                    {
                        string baseName = "Method";
                        string snippet = "";
                        string retType = method.ReturnType?.ToString() ?? "void";

                        if (method.HasBody && method.Body.Instructions.Count > 0)
                        {
                            var sb = new StringBuilder();
                            int count = Math.Min(15, method.Body.Instructions.Count);
                            for(int i=0; i<count; i++) sb.AppendLine(method.Body.Instructions[i].ToString());
                            snippet = sb.ToString();
                            
                            // Эвристика для базового имени без AI
                            if (snippet.Contains("call") && snippet.Contains("System.Net")) baseName = "NetworkOp";
                            else if (snippet.Contains("call") && snippet.Contains("File")) baseName = "FileOp";
                            else if (snippet.Contains("call") && snippet.Contains("Crypt")) baseName = "CryptoOp";
                            else if (snippet.Contains("throw")) baseName = "CheckAssert";
                            else if (retType.Contains("String")) baseName = "GetString";
                            else if (retType.Contains("Boolean")) baseName = "CheckCondition";
                        }

                        string finalName = $"{baseName}_{_renameCounter++:D2}";

                        if (useAi && ai != null)
                        {
                            string aiName = ai.GetSuggestedName(method.Name, snippet, retType);
                            if (!string.IsNullOrEmpty(aiName) && IsValidIdentifier(aiName) && aiName != method.Name)
                            {
                                finalName = aiName;
                            }
                        }

                        method.Name = finalName;
                        renamedMethods++;
                    }
                }
            }

            Console.WriteLine($"[+] Renamed: {renamedTypes} types, {renamedMethods} methods.");
            ai?.Dispose();
        }

        private bool IsObfuscatedName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.StartsWith("<")) return false; // Специальные имена компилятора
            if (name.Length <= 2 && name.All(char.IsLetter)) return true; // a, b, aa
            if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-z]{1,3}\d+$")) return true; // a1, ab12
            if (name.All(c => c == '_' || char.IsLetterOrDigit(c)) && name.Length > 10 && name.Where(char.IsLetter).Distinct().Count() <= 3) return true; // Длинное имя с малым набором букв
            return false;
        }

        private bool IsValidIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (!char.IsLetter(name[0]) && name[0] != '_') return false;
            return name.All(c => char.IsLetterOrDigit(c) || c == '_');
        }

        #endregion

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

        #endregion

        public void Save(string path)
        {
            Console.WriteLine($"[*] Saving result to: {path}");
            Log($"Saving to: {path}");
            
            var opts = new ModuleWriterOptions(_module)
            {
                Logger = DummyLogger.NoThrowInstance,
                MetadataOptions = new MetadataOptions { Flags = MetadataFlags.KeepOldMaxStack }
            };
            _module.Write(path, opts);
            
            Console.WriteLine("[+] Save complete.");
            Log("Save successful.");
        }

        public void Dispose()
        {
            _logWriter?.Close();
            _logWriter?.Dispose();
            _module.Dispose();
        }
    }
}
