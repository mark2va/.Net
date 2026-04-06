using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
        private int _obfCounter = 0;

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
                Log("=== Deobfuscation Started ===");
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
            Log("Phase 1: Control Flow Unpacking (Safe Mode)");
            Console.WriteLine("[*] Unraveling control flow...");
            int unraveledCount = SimplifyControlFlowAll();
            Console.WriteLine($"[+] Unraveled {unraveledCount} methods.");

            Log("Phase 2: Dead Code Removal & Stack Fix");
            Console.WriteLine("[*] Cleaning up dead code and fixing stacks...");
            CleanupAll();

            Log("Phase 3: Renaming");
            Console.WriteLine("[*] Renaming obfuscated items...");
            RenameObfuscatedItems();

            Log("=== Deobfuscation Finished ===");
        }

        /// <summary>
        /// Глобальный проход по всем методам для упрощения потока управления.
        /// </summary>
        private int SimplifyControlFlowAll()
        {
            int count = 0;
            foreach (var type in _module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody || !method.Body.IsIL) continue;
                    try
                    {
                        if (UnravelMethod(method))
                            count++;
                    }
                    catch (Exception ex)
                    {
                        Log($"[!] Error in {method.FullName}: {ex.Message}");
                    }
                }
            }
            return count;
        }

        /// <summary>
        /// Основной алгоритм распутывания одного метода.
        /// Использует символьное выполнение для вычисления переходов.
        /// </summary>
        private bool UnravelMethod(MethodDef method)
        {
            var body = method.Body;
            if (body == null || body.Instructions.Count < 5) return false;

            // 1. Поиск переменной состояния (State Variable)
            // Ищем локаль, которая часто используется в паттернах сравнения
            int? stateVarIndex = FindStateVariable(method);
            
            if (!stateVarIndex.HasValue)
            {
                // Если явной переменной состояния нет, пробуем упростить простые условия
                return SimplifySimpleBranches(method);
            }

            Log($"  Found state variable V_{stateVarIndex.Value} in {method.Name}");

            // 2. Символьное выполнение
            // Карта: Индекс инструкции -> Значение переменной состояния ПЕРЕД этой инструкцией
            var stateMap = new Dictionary<int, object?>();
            var visited = new HashSet<int>();
            var queue = new Queue<int>();

            // Инициализация: ищем первое присваивание стейта
            object? initialState = null;
            for (int i = 0; i < Math.Min(20, body.Instructions.Count); i++)
            {
                var instr = body.Instructions[i];
                if (IsStloc(instr, out int idx) && idx == stateVarIndex.Value)
                {
                    if (i > 0)
                    {
                        initialState = GetConstantValue(body.Instructions[i - 1]);
                        if (initialState != null)
                        {
                            queue.Enqueue(i + 1);
                            stateMap[i + 1] = initialState;
                            break;
                        }
                    }
                }
            }

            if (initialState == null) return false;

            bool changed = false;
            int maxSteps = body.Instructions.Count * 100;
            int steps = 0;

            while (queue.Count > 0 && steps < maxSteps)
            {
                steps++;
                int currentIndex = queue.Dequeue();
                if (currentIndex >= body.Instructions.Count || visited.Contains(currentIndex)) continue;
                
                visited.Add(currentIndex);
                object? currentState = stateMap.ContainsKey(currentIndex) ? stateMap[currentIndex] : null;
                var instr = body.Instructions[currentIndex];

                // Обработка перехода
                if (instr.OpCode.FlowControl == FlowControl.Branch)
                {
                    if (instr.Operand is Instruction target)
                    {
                        int targetIdx = body.Instructions.IndexOf(target);
                        if (targetIdx != -1 && !visited.Contains(targetIdx))
                        {
                            stateMap[targetIdx] = currentState;
                            queue.Enqueue(targetIdx);
                        }
                    }
                }
                else if (instr.OpCode.FlowControl == FlowControl.Cond_Branch)
                {
                    // Пытаемся вычислить условие
                    bool? conditionResult = EvaluateConditionAt(body, currentIndex, currentState, stateVarIndex.Value);
                    
                    if (conditionResult.HasValue)
                    {
                        changed = true;
                        Instruction? target = instr.Operand as Instruction;
                        if (target != null)
                        {
                            if (conditionResult.Value)
                            {
                                // Условие истинно: превращаем в безусловный переход
                                instr.OpCode = GetShortBranchOp(OpCodes.Br);
                                Log($"    IL_{currentIndex:X4}: Forced branch TRUE");
                            }
                            else
                            {
                                // Условие ложно: превращаем в NOP (переход не выполняется)
                                instr.OpCode = OpCodes.Nop;
                                instr.Operand = null;
                                Log($"    IL_{currentIndex:X4}: Forced branch FALSE (NOP)");
                                
                                // Добавляем следующую инструкцию в очередь с тем же стейтом
                                int nextIdx = currentIndex + 1;
                                if (nextIdx < body.Instructions.Count && !visited.Contains(nextIdx))
                                {
                                    stateMap[nextIdx] = currentState;
                                    queue.Enqueue(nextIdx);
                                }
                                continue; // Не идем по ветке target
                            }
                            
                            // Если ветка активна, добавляем target в очередь
                            int tIdx = body.Instructions.IndexOf(target);
                            if (tIdx != -1 && !visited.Contains(tIdx))
                            {
                                stateMap[tIdx] = currentState;
                                queue.Enqueue(tIdx);
                            }
                        }
                    }
                    else
                    {
                        // Не смогли вычислить statically, идем по обеим веткам (консервативно)
                        if (instr.Operand is Instruction t)
                        {
                            int tIdx = body.Instructions.IndexOf(t);
                            if (tIdx != -1 && !visited.Contains(tIdx))
                            {
                                stateMap[tIdx] = currentState; // Передаем стейт, хотя он может измениться в пути
                                queue.Enqueue(tIdx);
                            }
                        }
                    }
                }

                // Обновление состояния при записи в переменную
                if (IsStloc(instr, out int sIdx) && sIdx == stateVarIndex.Value)
                {
                    if (currentIndex > 0)
                    {
                        var val = GetConstantValue(body.Instructions[currentIndex - 1]);
                        if (val != null)
                        {
                            int nextIdx = currentIndex + 1;
                            if (nextIdx < body.Instructions.Count)
                            {
                                stateMap[nextIdx] = val;
                                queue.Enqueue(nextIdx);
                            }
                        }
                    }
                }
                else
                {
                    // Обычный поток
                    if (instr.OpCode.FlowControl == FlowControl.Next)
                    {
                        int nextIdx = currentIndex + 1;
                        if (nextIdx < body.Instructions.Count && !visited.Contains(nextIdx))
                        {
                            stateMap[nextIdx] = currentState;
                            queue.Enqueue(nextIdx);
                        }
                    }
                }
            }

            if (changed)
            {
                body.UpdateInstructionOffsets();
            }

            return changed;
        }

        /// <summary>
        /// Упрощение простых условий без явной переменной состояния.
        /// </summary>
        private bool SimplifySimpleBranches(MethodDef method)
        {
            var body = method.Body;
            bool changed = false;
            
            // Простой проход: если видим ldc + brtrue/brfalse
            for (int i = 0; i < body.Instructions.Count - 1; i++)
            {
                var instr = body.Instructions[i];
                if ((instr.OpCode.Code == Code.Brtrue || instr.OpCode.Code == Code.Brtrue_S ||
                     instr.OpCode.Code == Code.Brfalse || instr.OpCode.Code == Code.Brfalse_S))
                {
                    if (i > 0)
                    {
                        var prev = body.Instructions[i - 1];
                        var val = GetConstantValue(prev);
                        if (val != null)
                        {
                            bool isZero = Convert.ToDouble(val) == 0;
                            bool jumpTaken = (instr.OpCode.Code == Code.Brtrue || instr.OpCode.Code == Code.Brtrue_S) ? !isZero : isZero;

                            if (jumpTaken)
                            {
                                instr.OpCode = GetShortBranchOp(OpCodes.Br);
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
            
            if (changed) body.UpdateInstructionOffsets();
            return changed;
        }

        /// <summary>
        /// Очистка мертвого кода с учетом безопасности стека.
        /// </summary>
        private void CleanupAll()
        {
            foreach (var type in _module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody) continue;
                    try
                    {
                        RemoveUnreachableBlocksSafe(method);
                        CleanupNops(method);
                    }
                    catch (Exception ex)
                    {
                        Log($"[!] Cleanup error in {method.Name}: {ex.Message}");
                    }
                }
            }
        }

        private void RemoveUnreachableBlocksSafe(MethodDef method)
        {
            var body = method.Body;
            var instrs = body.Instructions;
            if (instrs.Count == 0) return;

            // 1. Построение графа достижимости
            var reachable = new HashSet<Instruction>();
            var queue = new Queue<Instruction>();

            queue.Enqueue(instrs[0]);
            reachable.Add(instrs[0]);

            while (queue.Count > 0)
            {
                var curr = queue.Dequeue();
                int idx = instrs.IndexOf(curr);
                if (idx == -1) continue;

                // Переход к следующей инструкции, если поток не прерывается
                if (curr.OpCode.FlowControl != FlowControl.Branch &&
                    curr.OpCode.FlowControl != FlowControl.Ret &&
                    curr.OpCode.FlowControl != FlowControl.Throw)
                {
                    if (idx + 1 < instrs.Count)
                    {
                        var next = instrs[idx + 1];
                        if (reachable.Add(next)) queue.Enqueue(next);
                    }
                }

                // Цели переходов
                if (curr.Operand is Instruction t)
                {
                    if (reachable.Add(t)) queue.Enqueue(t);
                }
                else if (curr.Operand is Instruction[] ts)
                {
                    foreach (var x in ts) if (reachable.Add(x)) queue.Enqueue(x);
                }
            }

            // 2. Замена недостижимого кода
            // ВАЖНО: Мы не удаляем инструкции физически сразу, чтобы не сбить индексы и стек.
            // Мы заменяем их на NOP, но с осторожностью для инструкций, влияющих на стек.
            
            bool modified = false;
            for (int i = instrs.Count - 1; i >= 0; i--)
            {
                var instr = instrs[i];
                if (!reachable.Contains(instr))
                {
                    // Проверка влияния на стек
                    // Если инструкция кладет что-то на стек (Push > 0), просто заменить на NOP нельзя,
                    // так как следующий код ожидает это значение.
                    // Однако, если блок недостижим, то и код после него (в этом блоке) тоже недостижим.
                    // Проблема возникает, если недостижимый блок "проваливается" в достижимый.
                    // Но по определению достижимости, если мы сюда не попали, то и в следующий достижимый блок
                    // из этого места мы не попадем (если только это не цель перехода извне, но тогда она была бы в reachable).
                    
                    // Единственный случай опасности: если эта инструкция является целью перехода из ДОСТИЖИМОГО блока,
                    // но сама инструкция помечена как недостижимая? Нет, логика выше это исключает.
                    
                    // Значит, можно смело заменять на NOP, ЕСЛИ это не нарушает баланс внутри самого блока мусора.
                    // Но так как весь блок мусора, баланс внутри него не важен для внешнего мира.
                    
                    // Исключение: если это последняя инструкция перед выходом из мусора в рабочий код?
                    // Нет, если выход есть, то целевая инструкция выхода уже в reachable, а переход к ней - тоже.
                    
                    instr.OpCode = OpCodes.Nop;
                    instr.Operand = null;
                    modified = true;
                }
            }

            if (modified)
            {
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

        #region Helpers

        private int? FindStateVariable(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            var usageCount = new Dictionary<int, int>();

            for (int i = 0; i < instructions.Count - 3; i++)
            {
                if (IsLdloc(instructions[i], out int idx) &&
                    (instructions[i+1].OpCode.Code == Code.Ldc_I4 || instructions[i+1].OpCode.Code == Code.Ldc_I8) &&
                    (instructions[i+2].OpCode.Code == Code.Ceq || instructions[i+2].OpCode.Code == Code.Cgt || instructions[i+2].OpCode.Code == Code.Clt))
                {
                    if (!usageCount.ContainsKey(idx)) usageCount[idx] = 0;
                    usageCount[idx]++;
                }
            }

            foreach (var kvp in usageCount)
            {
                if (kvp.Value >= 2) return kvp.Key;
            }
            return null;
        }

        private bool? EvaluateConditionAt(CilBody body, int ip, object? currentState, int stateVarIndex)
        {
            if (ip < 1) return null;
            var branch = body.Instructions[ip];
            
            // Паттерн: ldloc(state), ldc(val), ceq, br...
            if (ip >= 3)
            {
                var cmp = body.Instructions[ip - 1];
                var ldc = body.Instructions[ip - 2];
                var ldloc = body.Instructions[ip - 3];

                if (IsLdloc(ldloc, out int idx) && idx == stateVarIndex &&
                    (cmp.OpCode.Code == Code.Ceq || cmp.OpCode.Code == Code.Cgt || cmp.OpCode.Code == Code.Clt))
                {
                    var constVal = GetConstantValue(ldc);
                    if (currentState != null && constVal != null)
                    {
                        return CompareValues(currentState, constVal, cmp.OpCode.Code);
                    }
                }
            }

            // Паттерн: ldloc(state), brtrue...
            if (ip >= 2)
            {
                var ldloc = body.Instructions[ip - 1];
                if (IsLdloc(ldloc, out int idx) && idx == stateVarIndex)
                {
                    if (currentState != null)
                    {
                        bool isZero = Convert.ToDouble(currentState) == 0;
                        return (branch.OpCode.Code == Code.Brtrue || branch.OpCode.Code == Code.Brtrue_S) ? !isZero : isZero;
                    }
                }
            }

            return null;
        }

        private bool IsStloc(Instruction i, out int idx)
        {
            idx = -1;
            if (i.Operand is Local l) idx = l.Index;
            else if (i.OpCode.Code == Code.Stloc_0) idx = 0;
            else if (i.OpCode.Code == Code.Stloc_1) idx = 1;
            else if (i.OpCode.Code == Code.Stloc_2) idx = 2;
            else if (i.OpCode.Code == Code.Stloc_3) idx = 3;
            return idx != -1;
        }

        private bool IsLdloc(Instruction i, out int idx)
        {
            idx = -1;
            if (i.Operand is Local l) idx = l.Index;
            else if (i.OpCode.Code == Code.Ldloc_0) idx = 0;
            else if (i.OpCode.Code == Code.Ldloc_1) idx = 1;
            else if (i.OpCode.Code == Code.Ldloc_2) idx = 2;
            else if (i.OpCode.Code == Code.Ldloc_3) idx = 3;
            return idx != -1;
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

        private OpCode GetShortBranchOp(OpCode longOp)
        {
            return longOp.Code == Code.Br ? OpCodes.Br_S : OpCodes.Br;
        }

        #endregion

        private void RenameObfuscatedItems()
        {
            bool useAi = _aiConfig.Enabled;
            AiAssistant? ai = null;

            if (useAi)
            {
                // Попытка подключения
                try 
                {
                    ai = new AiAssistant(_aiConfig);
                    if (!ai.IsConnected)
                    {
                        Console.WriteLine("[!] AI connection failed. Falling back to simple renaming.");
                        useAi = false;
                        ai.Dispose();
                        ai = null;
                    }
                    else
                    {
                        // Дополнительная проверка модели (если поддерживается API)
                        Console.WriteLine($"[+] AI Connected. Model: {_aiConfig.ModelName}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[!] AI Error: {ex.Message}. Using simple renaming.");
                    useAi = false;
                    ai?.Dispose();
                    ai = null;
                }
            }

            int renamedCount = 0;

            foreach (var type in _module.GetTypes())
            {
                if (IsObfuscatedName(type.Name))
                {
                    type.Name = GenerateSimpleTypeName();
                    renamedCount++;
                }

                foreach (var method in type.Methods)
                {
                    if (method.Name == ".cctor" || method.Name == ".ctor") continue;
                    if (IsObfuscatedName(method.Name))
                    {
                        string newName = "Method_" + _obfCounter++;
                        
                        if (useAi && ai != null)
                        {
                            try 
                            {
                                string snippet = GetMethodSnippet(method);
                                string retType = method.ReturnType?.ToString() ?? "void";
                                string aiName = ai.GetSuggestedName(method.Name, snippet, retType);
                                if (!string.IsNullOrEmpty(aiName) && aiName != method.Name && IsValidIdentifier(aiName))
                                {
                                    newName = aiName;
                                }
                            }
                            catch { /* Ignore AI errors per method */ }
                        }
                        
                        method.Name = newName;
                        renamedCount++;
                    }
                }
            }

            Console.WriteLine($"[+] Renamed {renamedCount} items.");
            ai?.Dispose();
        }

        private string GenerateSimpleTypeName() => "Class_" + (_obfCounter++);
        
        private string GetMethodSnippet(MethodDef m)
        {
            if (!m.HasBody) return "";
            var sb = new System.Text.StringBuilder();
            int count = Math.Min(10, m.Body.Instructions.Count);
            for (int i = 0; i < count; i++)
            {
                sb.AppendLine(m.Body.Instructions[i].ToString());
            }
            return sb.ToString();
        }

        private bool IsObfuscatedName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.StartsWith("<")) return false;
            if (name.Length <= 2 && name.All(char.IsLetter)) return true;
            if (Regex.IsMatch(name, @"^[a-z]{1,2}\d+$")) return true;
            // Добавим проверку на странные unicode символы, часто используемые в обфускации
            if (name.Any(c => c > 128)) return true; 
            return false;
        }

        private bool IsValidIdentifier(string n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            if (!char.IsLetter(n[0]) && n[0] != '_') return false;
            foreach (var c in n) if (!char.IsLetterOrDigit(c) && c != '_') return false;
            return true;
        }

        public void Save(string path)
        {
            Console.WriteLine($"[*] Saving to: {path}");
            Log($"Saving to: {path}");
            
            var opts = new ModuleWriterOptions(_module)
            {
                Logger = DummyLogger.NoThrowInstance,
                MetadataOptions = new MetadataOptions { Flags = MetadataFlags.KeepOldMaxStack }
            };
            _module.Write(path, opts);
            
            Console.WriteLine("[+] Done.");
            Log("Saved successfully.");
        }

        public void Dispose()
        {
            _logWriter?.Close();
            _module.Dispose();
        }
    }
}
