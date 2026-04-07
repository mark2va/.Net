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
        private readonly AiConfig _aiConfig;
        private readonly bool _debugMode;
        private StreamWriter? _logWriter;

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
            Log("Phase 1: Control Flow Unpacking (State Machine)");
            Console.WriteLine("[*] Analyzing and unraveling control flow...");
            
            int unraveledCount = 0;
            int failedCount = 0;

            // Проходим по всем типам и методам
            foreach (var type in _module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody || !method.Body.IsIL) continue;
                    if (method.Body.Instructions.Count < 5) continue;

                    try
                    {
                        // Попытка распутать метод
                        if (UnravelStateMachine(method))
                        {
                            unraveledCount++;
                            Log($"[OK] Unraveled: {method.FullName}");
                        }
                    }
                    catch (Exception ex)
                    {
                        failedCount++;
                        Log($"[ERR] Failed to unravel {method.FullName}: {ex.Message}");
                    }
                }
            }

            Console.WriteLine($"[+] Unraveled {unraveledCount} methods. ({failedCount} failed/skipped)");

            Log("Phase 2: Cleanup (NOPs & Unreachable)");
            Console.WriteLine("[*] Cleaning up dead code...");
            CleanupAll();

            Log("Phase 3: Renaming");
            Console.WriteLine("[*] Renaming obfuscated items...");
            RenameObfuscatedItems();

            Log("=== Deobfuscation Finished ===");
        }

        /// <summary>
        /// Основной алгоритм распутывания State Machine.
        /// </summary>
        private bool UnravelStateMachine(MethodDef method)
        {
            var body = method.Body;
            var instrs = body.Instructions;
            
            // 1. Поиск переменной состояния (State Variable)
            // Ищем локаль, которая часто используется в паттернах: stloc, ldloc + ldc + ceq
            int? stateVarIndex = FindStateVariable(method);
            
            if (!stateVarIndex.HasValue)
            {
                // Если явной переменной состояния нет, пробуем упрощенный анализ потока (для простых случаев)
                return SimplifyControlFlowBasic(method);
            }

            Log($"  Found state variable: V_{stateVarIndex.Value}");

            // 2. Символьное выполнение для вычисления значений состояния
            // Возвращает словарь: Индекс инструкции -> Значение состояния ПЕРЕД этой инструкцией
            var stateMap = SymbolicExecuteState(method, stateVarIndex.Value);

            if (stateMap.Count == 0)
            {
                Log("  Symbolic execution yielded no states. Skipping.");
                return false;
            }

            bool changed = false;

            // 3. Упрощение ветвлений на основе карты состояний
            // Проходим с конца, чтобы безопасно удалять/менять инструкции
            for (int i = instrs.Count - 1; i >= 0; i--)
            {
                var instr = instrs[i];

                // А. Удаляем записи в переменную состояния (они больше не нужны)
                if (IsStloc(instr, out int sIdx) && sIdx == stateVarIndex.Value)
                {
                    // Также удаляем константу перед записью, если она есть
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

                // Б. Упрощаем условные переходы, зависящие от состояния
                // Паттерн: ldloc(state), ldc(val), ceq/cgt/clt, brtrue/brfalse
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
                // Финальная очистка NOP и обновление оффсетов
                CleanupNops(method);
                RemoveUnreachableBlocks(method);
                body.UpdateInstructionOffsets();
            }

            return changed;
        }

        /// <summary>
        /// Находит индекс локальной переменной, используемой как состояние.
        /// </summary>
        private int? FindStateVariable(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            var usageCount = new Dictionary<int, int>();

            for (int i = 0; i < instructions.Count - 3; i++)
            {
                // Паттерн: ldloc X, ldc Y, ceq, br...
                if (IsLdloc(instructions[i], out int idx))
                {
                    var next = instructions[i + 1];
                    var next2 = instructions[i + 2];

                    if (IsLdc(next) && 
                        (next2.OpCode.Code == Code.Ceq || next2.OpCode.Code == Code.Cgt || next2.OpCode.Code == Code.Clt || next2.OpCode.Code == Code.Cgt_Un || next2.OpCode.Code == Code.Clt_Un))
                    {
                        if (!usageCount.ContainsKey(idx)) usageCount[idx] = 0;
                        usageCount[idx]++;
                    }
                }
            }

            // Переменная считается состоянием, если участвует в >= 3 таких сравнениях
            foreach (var kvp in usageCount)
            {
                if (kvp.Value >= 3) return kvp.Key;
            }

            return null;
        }

        /// <summary>
        /// Символьное выполнение: определяет значение переменной состояния перед каждой инструкцией.
        /// </summary>
        private Dictionary<int, object?> SymbolicExecuteState(MethodDef method, int stateVarIndex)
        {
            var stateValues = new Dictionary<int, object?>();
            var instructions = method.Body.Instructions;
            
            // Очередь для обхода графа: (Индекс инструкции, Текущее значение состояния)
            var queue = new Queue<Tuple<int, object?>>();
            
            // Попытка найти начальное значение (первое присваивание стейту)
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
            
            var visited = new HashSet<int>(); // Посещенные IP

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                int ip = current.Item1;
                object? currentState = current.Item2;

                if (ip < 0 || ip >= instructions.Count) continue;
                if (visited.Contains(ip)) continue;
                visited.Add(ip);

                // Сохраняем состояние для этой точки
                if (!stateValues.ContainsKey(ip))
                    stateValues[ip] = currentState;

                var instr = instructions[ip];

                // Эмуляция перехода
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
                    // В символьном выполнении мы идем по ОБЕИМ веткам, так как условие может быть истинным или ложным
                    // Но значение состояния остается тем же до точки изменения.
                    
                    // Ветка 1: Target
                    if (instr.Operand is Instruction target)
                    {
                        int targetIp = instructions.IndexOf(target);
                        if (targetIp != -1) queue.Enqueue(Tuple.Create(targetIp, currentState));
                    }
                    // Ветка 2: Next
                    int nextIp = ip + 1;
                    if (nextIp < instructions.Count) queue.Enqueue(Tuple.Create(nextIp, currentState));
                }
                else if (instr.OpCode.FlowControl == FlowControl.Ret || instr.OpCode.FlowControl == FlowControl.Throw)
                {
                    // Конец пути
                }
                else
                {
                    // Обычная инструкция. Проверка на изменение состояния.
                    object? nextState = currentState;
                    
                    // Если это stloc state, обновляем состояние для следующей инструкции
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

        /// <summary>
        /// Пытается разрешить условный переход, если условие зависит от известного состояния.
        /// </summary>
        private bool TryResolveBranch(IList<Instruction> instrs, int branchIp, Dictionary<int, object?> stateMap, int stateVarIndex)
        {
            var branchInstr = instrs[branchIp];
            
            // Нам нужно найти условие перед ветвлением.
            // Обычно это: ... ldloc(state), ldc(val), ceq, brtrue ...
            // Ищем назад от branchInstr
            int lookback = 0;
            int cmpIp = -1;
            int ldcIp = -1;
            int ldlocIp = -1;

            // Простой парсер стека назад
            // Ожидаем: [Branch] <- [Cmp] <- [Ldc] <- [Ldloc(State)]
            if (branchIp >= 3)
            {
                var i1 = instrs[branchIp - 1]; // Cmp?
                var i2 = instrs[branchIp - 2]; // Ldc?
                var i3 = instrs[branchIp - 3]; // Ldloc?

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

            // Получаем известное значение состояния перед ldloc
            if (!stateMap.TryGetValue(ldlocIp, out object? currentState))
                return false;

            var compareValue = GetConstantValue(instrs[ldcIp]);
            if (compareValue == null || currentState == null)
                return false;

            // Вычисляем результат сравнения
            bool? result = CompareValues(currentState, compareValue, instrs[cmpIp].OpCode.Code);

            if (result.HasValue)
            {
                if (result.Value)
                {
                    // Условие истинно -> превращаем в безусловный переход
                    Log($"    Resolved branch at {branchIp}: TRUE -> br");
                    branchInstr.OpCode = OpCodes.Br;
                    if (branchInstr.OpCode.Code == Code.Brtrue_S || branchInstr.OpCode.Code == Code.Brfalse_S)
                         branchInstr.OpCode = OpCodes.Br_S;
                    else branchInstr.OpCode = OpCodes.Br;
                    
                    // Очищаем инструкции условия (ldloc, ldc, ceq) - они больше не нужны
                    instrs[ldlocIp].OpCode = OpCodes.Nop;
                    instrs[ldlocIp].Operand = null;
                    instrs[ldcIp].OpCode = OpCodes.Nop;
                    instrs[ldcIp].Operand = null;
                    instrs[cmpIp].OpCode = OpCodes.Nop;
                    instrs[cmpIp].Operand = null;
                }
                else
                {
                    // Условие ложно -> превращаем в NOP (переход не сработает)
                    Log($"    Resolved branch at {branchIp}: FALSE -> nop");
                    branchInstr.OpCode = OpCodes.Nop;
                    branchInstr.Operand = null;
                    
                    // Очищаем инструкции условия (ldloc, ldc, ceq) - они больше не нужны
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

        /// <summary>
        /// Базовое упрощение для методов без явной переменной состояния (упрощение констант).
        /// </summary>
        private bool SimplifyControlFlowBasic(MethodDef method)
        {
            // Здесь можно добавить логику для простых замен констант, если нужно.
            // Для начала полагаемся на основной алгоритм.
            return false;
        }

        private void CleanupAll()
        {
            foreach (var type in _module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (method.HasBody)
                    {
                        CleanupNops(method);
                        RemoveUnreachableBlocks(method);
                        method.Body.UpdateInstructionOffsets();
                    }
                }
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
                        // Проверяем, не является ли эта инструкция целью перехода
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

                // Следующая инструкция
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

                // Цели переходов
                if (curr.Operand is Instruction t)
                {
                    if (reachable.Add(t)) q.Enqueue(t);
                }
                else if (curr.Operand is Instruction[] ts)
                {
                    foreach (var x in ts) if (reachable.Add(x)) q.Enqueue(x);
                }
            }

            // Замена недостижимого на NOP
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

        #region Renaming Logic

        private void RenameObfuscatedItems()
        {
            bool useAi = _aiConfig.Enabled;
            AiAssistant? ai = null;

            if (useAi)
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
                    Console.WriteLine($"[+] AI Connected. Model: {_aiConfig.ModelName}");
                }
            }

            int renamedCount = 0;
            int classCounter = 1;
            int methodCounter = 1;

            foreach (var type in _module.GetTypes())
            {
                // Переименование типов
                if (IsObfuscatedName(type.Name))
                {
                    string newName = useAi && ai != null ? GenerateAiName(ai, type.Name, "Class", "") : $"Class_{classCounter++}";
                    type.Name = newName;
                    renamedCount++;
                }

                foreach (var method in type.Methods)
                {
                    if (method.Name == ".cctor" || method.Name == ".ctor") continue;
                    if (IsObfuscatedName(method.Name))
                    {
                        string snippet = GetMethodSnippet(method);
                        string retType = method.ReturnType?.ToString() ?? "void";
                        
                        string newName = $"Method_{methodCounter++}";
                        if (useAi && ai != null)
                        {
                            string aiName = ai.GetSuggestedName(method.Name, snippet, retType);
                            if (!string.IsNullOrEmpty(aiName) && aiName != method.Name && IsValidIdentifier(aiName))
                                newName = aiName;
                        }
                        
                        method.Name = newName;
                        renamedCount++;
                    }
                }
            }

            Console.WriteLine($"[+] Renamed {renamedCount} items.");
            ai?.Dispose();
        }

        private string GenerateAiName(AiAssistant ai, string oldName, string type, string context)
        {
            return $"Item_{oldName.GetHashCode().ToString("X").Substring(0, 4)}"; // Заглушка, если нужно
        }

        private bool IsObfuscatedName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.StartsWith("<")) return false; // Сгенерированные компилятором (свойства, лямбды)
            
            // Если имя содержит символы вне диапазона ASCII (латиница, цифры, _), это признак обфускации
            // Особенно актуально для случаев с арабскими/персидскими символами и прочими Unicode-знаками
            foreach (char c in name)
            {
                if (c > 127) return true; // Любой символ с кодом > 127 считаем подозрительным
                if (c == '_' || char.IsDigit(c)) continue;
                if (!char.IsLetter(c)) return true; // Странные символы в ASCII диапазоне
            }

            // Короткие имена из 1-2 букв (a, b, aa, a1) тоже часто признак обфускации
            if (name.Length <= 2 && name.All(char.IsLetter)) return true;
            
            // Паттерны типа a1, b99
            if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-z]{1,2}\d+$")) return true;
            
            return false;
        }

        private string GetMethodSnippet(MethodDef m)
        {
            if (!m.HasBody) return "";
            var sb = new System.Text.StringBuilder();
            int count = Math.Min(15, m.Body.Instructions.Count);
            for (int i = 0; i < count; i++)
            {
                sb.AppendLine(m.Body.Instructions[i].ToString());
            }
            return sb.ToString();
        }

        private bool IsValidIdentifier(string n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            if (!char.IsLetter(n[0]) && n[0] != '_') return false;
            foreach (var c in n) if (!char.IsLetterOrDigit(c) && c != '_') return false;
            return true;
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

        private bool IsLdc(Instruction i)
        {
            return i.OpCode.Code == Code.Ldc_I4 || i.OpCode.Code == Code.Ldc_I8 ||
                   i.OpCode.Code == Code.Ldc_R4 || i.OpCode.Code == Code.Ldc_R8 ||
                   (i.OpCode.Code >= Code.Ldc_I4_0 && i.OpCode.Code <= Code.Ldc_I4_M1);
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
                // Поддержка double и float
                if (a is double da && b is double db)
                {
                    switch (op)
                    {
                        case Code.Ceq: return da == db;
                        case Code.Cgt: case Code.Cgt_Un: return da > db;
                        case Code.Clt: case Code.Clt_Un: return da < db;
                    }
                }
                else if (a is float fa && b is float fb)
                {
                    switch (op)
                    {
                        case Code.Ceq: return fa == fb;
                        case Code.Cgt: case Code.Cgt_Un: return fa > fb;
                        case Code.Clt: case Code.Clt_Un: return fa < fb;
                    }
                }
                else
                {
                    // fallback to double для int/long
                    double da = Convert.ToDouble(a);
                    double db = Convert.ToDouble(b);
                    switch (op)
                    {
                        case Code.Ceq: return da == db;
                        case Code.Cgt: case Code.Cgt_Un: return da > db;
                        case Code.Clt: case Code.Clt_Un: return da < db;
                    }
                }
            } catch { }
            return null;
        }

        #endregion

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
