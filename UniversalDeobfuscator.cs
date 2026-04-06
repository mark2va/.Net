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
                Log("=== Deobfuscation Session Started ===");
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
            Console.WriteLine("[*] Phase 1: Unraveling Control Flow...");
            Log("Starting Control Flow Unraveling...");
            int unraveledCount = UnravelAllControlFlow();
            Console.WriteLine($"[+] Unraveled {unraveledCount} methods.");
            Log($"Unraveled {unraveledCount} methods.");

            Console.WriteLine("[*] Phase 2: Cleaning Dead Code & Nops...");
            Log("Starting Cleanup...");
            int cleanedCount = CleanupAll();
            Console.WriteLine($"[+] Cleaned {cleanedCount} methods.");
            
            Console.WriteLine("[*] Phase 3: Renaming Obfuscated Items...");
            Log("Starting Renaming...");
            RenameObfuscatedItems();

            Log("=== Deobfuscation Finished ===");
        }

        /// <summary>
        /// Основной метод распутывания всех методов в модуле.
        /// </summary>
        private int UnravelAllControlFlow()
        {
            int count = 0;
            foreach (var type in _module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody || !method.Body.IsIL) continue;
                    if (method.Body.Instructions.Count < 5) continue;

                    try
                    {
                        if (UnravelMethod(method))
                        {
                            count++;
                            Log($"Unraveled: {method.FullName}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Error unraveling {method.FullName}: {ex.Message}");
                    }
                }
            }
            return count;
        }

        /// <summary>
        /// Пытается распутать конкретный метод, используя символьную эмуляцию.
        /// Алгоритм:
        /// 1. Находим переменную состояния (state variable).
        /// 2. Эмулируем выполнение, вычисляя значения состояния.
        /// 3. Перестраиваем инструкции, удаляя фейковые переходы.
        /// </summary>
        private bool UnravelMethod(MethodDef method)
        {
            var body = method.Body;
            if (body == null) return false;

            // 1. Поиск переменной состояния
            // Ищем локаль, которая часто используется в паттернах сравнения (ldloc, ldc, ceq/cgt/clt, br)
            int? stateVarIndex = FindStateVariable(method);
            
            if (!stateVarIndex.HasValue)
            {
                // Если явной переменной состояния нет, пробуем упрощенную очистку мусора
                return SimplifyJunkInstructions(method);
            }

            Log($"  Found state variable: V_{stateVarIndex.Value}");

            // 2. Символьное выполнение для построения карты переходов
            // Мы не меняем код сразу, а строим новый список инструкций
            var newInstructions = EmulateAndRebuild(method, stateVarIndex.Value);

            if (newInstructions == null || newInstructions.Count == 0)
                return false;

            // 3. Замена тела метода
            ReplaceBody(method, newInstructions);
            return true;
        }

        private int? FindStateVariable(MethodDef method)
        {
            var instrs = method.Body.Instructions;
            var usageCounts = new Dictionary<int, int>();

            for (int i = 0; i < instrs.Count - 3; i++)
            {
                // Паттерн: ldloc X, ldc Y, ceq/cgt/clt, br...
                if (IsLdloc(instrs[i], out int idx))
                {
                    var nextOp = instrs[i + 1].OpCode.Code;
                    var nextNextOp = instrs[i + 2].OpCode.Code;

                    if ((nextOp == Code.Ldc_I4 || nextOp == Code.Ldc_I8 || nextOp == Code.Ldc_R4 || nextOp == Code.Ldc_R8) &&
                        (nextNextOp == Code.Ceq || nextNextOp == Code.Cgt || nextNextOp == Code.Clt || 
                         nextNextOp == Code.Cgt_Un || nextNextOp == Code.Clt_Un))
                    {
                        if (!usageCounts.ContainsKey(idx)) usageCounts[idx] = 0;
                        usageCounts[idx]++;
                    }
                }
            }

            // Переменная считается состоянием, если участвует в >= 3 таких сравнениях
            foreach (var kvp in usageCounts)
            {
                if (kvp.Value >= 3) return kvp.Key;
            }

            return null;
        }

        /// <summary>
        /// Эмулирует выполнение метода, игнорируя обфусцированные переходы, и возвращает линейный список инструкций.
        /// </summary>
        private List<Instruction>? EmulateAndRebuild(MethodDef method, int stateVarIndex)
        {
            var body = method.Body;
            var oldInstrs = body.Instructions;
            var newInstrs = new List<Instruction>();
            
            // Состояние эмуляции
            var localValues = new object?[body.Variables.Count];
            var stack = new Stack<object?>();
            var visited = new HashSet<int>(); // Индексы инструкций, которые мы уже обработали в этом проходе
            
            // Попытка найти начальное значение состояния (обычно в начале метода)
            object? currentState = null;
            for (int i = 0; i < Math.Min(20, oldInstrs.Count); i++)
            {
                if (IsStloc(oldInstrs[i], out int idx) && idx == stateVarIndex)
                {
                    if (i > 0)
                    {
                        currentState = GetConstant(oldInstrs[i - 1]);
                        if (currentState != null)
                        {
                            localValues[stateVarIndex] = currentState;
                            break;
                        }
                    }
                }
            }

            if (currentState == null) return null; // Не удалось инициировать эмуляцию

            var queue = new Queue<int>(); // Очередь индексов инструкций для обработки
            queue.Enqueue(0);
            
            // Для предотвращения бесконечных циклов в эмуляции используем счетчик шагов
            int maxSteps = oldInstrs.Count * 100;
            int steps = 0;

            // Множество уже добавленных в новый список инструкций (по оригинальному индексу)
            var addedToNewList = new HashSet<int>();

            while (queue.Count > 0 && steps < maxSteps)
            {
                steps++;
                int ip = queue.Dequeue();

                if (ip < 0 || ip >= oldInstrs.Count) continue;
                if (addedToNewList.Contains(ip)) continue; // Уже обработали эту точку входа

                // Если мы перепрыгнули через кусок кода из-за ветвления, нам нужно пометить пропущенные как недостижимые?
                // В данной реализации мы просто идем по пути выполнения.
                
                int currentIp = ip;
                bool blockEnded = false;

                while (!blockEnded && currentIp < oldInstrs.Count && steps < maxSteps)
                {
                    steps++;
                    var instr = oldInstrs[currentIp];

                    // Пропускаем инструкции, относящиеся только к механизму обфускации (чтение/запись стейта и сравнения)
                    if (IsStateJunk(instr, stateVarIndex, oldInstrs, currentIp))
                    {
                        // Но перед пропуском, если это запись stloc, обновляем наше эмулированное состояние
                        if (IsStloc(instr, out int sIdx) && sIdx == stateVarIndex)
                        {
                            // Значение должно быть на стеке эмуляции или взято из предыдущей ldc
                            // Упрощенно: смотрим назад на ldc
                            if (currentIp > 0)
                            {
                                var val = GetConstant(oldInstrs[currentIp - 1]);
                                if (val != null)
                                {
                                    localValues[stateVarIndex] = val;
                                    currentState = val;
                                }
                            }
                        }
                        
                        currentIp++;
                        continue; 
                    }

                    // Добавляем инструкцию в новый список
                    // Клонируем, чтобы разорвать связи старых операндов-инструкций
                    var newInstr = CloneInstruction(instr);
                    newInstrs.Add(newInstr);
                    addedToNewList.Add(currentIp);

                    // Обработка потока управления
                    if (instr.OpCode.FlowControl == FlowControl.Branch)
                    {
                        if (instr.Operand is Instruction target)
                        {
                            int targetIdx = oldInstrs.IndexOf(target);
                            if (targetIdx != -1) queue.Enqueue(targetIdx);
                        }
                        blockEnded = true;
                    }
                    else if (instr.OpCode.FlowControl == FlowControl.Cond_Branch)
                    {
                        // Пытаемся вычислить условие на основе текущего состояния эмуляции
                        bool? conditionResult = EvaluateConditionAt(method, currentIp, localValues, stack, stateVarIndex);

                        if (conditionResult.HasValue)
                        {
                            if (instr.Operand is Instruction target)
                            {
                                int targetIdx = oldInstrs.IndexOf(target);
                                if (conditionResult.Value)
                                {
                                    if (targetIdx != -1) queue.Enqueue(targetIdx);
                                }
                                else
                                {
                                    // Ветка не выполняется, идем дальше (следующая инструкция)
                                    // Но иногда cond_branch может иметь сложную логику, здесь мы предполагаем стандартный if
                                }
                            }
                            blockEnded = true;
                        }
                        else
                        {
                            // Не смогли вычислить (реальное условие?). 
                            // Сохраняем ветвление как есть, но переключаемся на обычный режим для этого блока
                            // Для простоты в этой версии считаем, что если не вычислили - идём по обоим путям (保守тивно)
                            if (instr.Operand is Instruction target)
                            {
                                int targetIdx = oldInstrs.IndexOf(target);
                                if (targetIdx != -1) queue.Enqueue(targetIdx);
                            }
                            // И продолжаем выполнение следующей инструкции (fall-through)
                            currentIp++;
                            blockEnded = true; // Прерываем линейный проход, так как добавили задачу в очередь
                        }
                    }
                    else if (instr.OpCode.FlowControl == FlowControl.Ret || 
                             instr.OpCode.FlowControl == FlowControl.Throw)
                    {
                        blockEnded = true;
                    }
                    else
                    {
                        // Эмуляция стека для будущих условий
                        SimulateStackEffect(instr, localValues, stack);
                        currentIp++;
                    }
                }
            }

            if (newInstrs.Count == 0) return null;
            return newInstrs;
        }

        /// <summary>
        /// Определяет, является ли инструкция частью мусорного кода обфуксации состояния.
        /// </summary>
        private bool IsStateJunk(Instruction instr, int stateVarIndex, IList<Instruction> allInstrs, int currentIndex)
        {
            // Запись в переменную состояния: stloc stateVar
            if (IsStloc(instr, out int sIdx) && sIdx == stateVarIndex) return true;

            // Чтение переменной состояния для сравнения: ldloc stateVar
            if (IsLdloc(instr, out int lIdx) && lIdx == stateVarIndex)
            {
                // Проверяем, идет ли за ним сравнение
                if (currentIndex + 2 < allInstrs.Count)
                {
                    var next = allInstrs[currentIndex + 1];
                    var next2 = allInstrs[currentIndex + 2];
                    if ((next.OpCode.Code == Code.Ldc_I4 || next.OpCode.Code == Code.Ldc_I8) &&
                        (next2.OpCode.Code == Code.Ceq || next2.OpCode.Code == Code.Cgt || next2.OpCode.Code == Code.Clt))
                    {
                        return true;
                    }
                }
            }

            // Константы, используемые только для сравнения со стейтом (ldc перед ceq после ldloc state)
            if (instr.OpCode.Code == Code.Ldc_I4 || instr.OpCode.Code == Code.Ldc_I8)
            {
                if (currentIndex > 0 && currentIndex + 1 < allInstrs.Count)
                {
                    var prev = allInstrs[currentIndex - 1];
                    var next = allInstrs[currentIndex + 1];
                    if (IsLdloc(prev, out int plIdx) && plIdx == stateVarIndex &&
                        (next.OpCode.Code == Code.Ceq || next.OpCode.Code == Code.Cgt || next.OpCode.Code == Code.Clt))
                    {
                        return true;
                    }
                }
            }

            // Сами операции сравнения (ceq, cgt...), если они работают со стейтом
            if (instr.OpCode.Code == Code.Ceq || instr.OpCode.Code == Code.Cgt || instr.OpCode.Code == Code.Clt ||
                instr.OpCode.Code == Code.Cgt_Un || instr.OpCode.Code == Code.Clt_Un)
            {
                 if (currentIndex >= 2)
                 {
                     var prev = allInstrs[currentIndex - 2];
                     if (IsLdloc(prev, out int plIdx) && plIdx == stateVarIndex) return true;
                 }
            }

            return false;
        }

        private bool? EvaluateConditionAt(MethodDef method, int ip, object?[] locals, Stack<object?> stack, int stateVarIndex)
        {
            var instrs = method.Body.Instructions;
            if (ip < 2) return null;

            var branch = instrs[ip];
            var prev1 = instrs[ip - 1]; // ceq/cgt...
            var prev2 = instrs[ip - 2]; // ldc
            var prev3 = (ip >= 3) ? instrs[ip - 3] : null; // ldloc

            // Паттерн: ldloc(state), ldc(val), ceq, br...
            if (prev3 != null && IsLdloc(prev3, out int lIdx) && lIdx == stateVarIndex)
            {
                var constVal = GetConstant(prev2);
                var stateVal = locals[stateVarIndex];

                if (constVal != null && stateVal != null)
                {
                    bool res = Compare(stateVal, constVal, prev1.OpCode.Code);
                    
                    // Если ветвление brtrue/brfalse, инвертируем логикуIfNeeded?
                    // Нет, compare возвращает результат сравнения (true/false).
                    // brtrue переходит, если стек (результат сравнения) != 0 (true).
                    // brfalse переходит, если стек == 0 (false).
                    
                    if (branch.OpCode.Code == Code.Brfalse || branch.OpCode.Code == Code.Brfalse_S)
                        return !res;
                    
                    return res;
                }
            }
            
            // Паттерн: ldloc(state), brtrue... (проверка на ноль)
            if (prev2 != null && IsLdloc(prev2, out int lIdx) && lIdx == stateVarIndex)
            {
                var val = locals[stateVarIndex];
                if (val != null)
                {
                    bool isZero = Convert.ToDouble(val) == 0;
                    if (branch.OpCode.Code == Code.Brtrue || branch.OpCode.Code == Code.Brtrue_S)
                        return !isZero;
                    return isZero;
                }
            }

            return null;
        }

        private void SimulateStackEffect(Instruction instr, object?[] locals, Stack<object?> stack)
        {
            try
            {
                switch (instr.OpCode.Code)
                {
                    case Code.Ldc_I4: case Code.Ldc_I4_0: case Code.Ldc_I4_1: case Code.Ldc_I4_2: 
                    case Code.Ldc_I4_3: case Code.Ldc_I4_4: case Code.Ldc_I4_5: case Code.Ldc_I4_6: 
                    case Code.Ldc_I4_7: case Code.Ldc_I4_8: case Code.Ldc_I4_M1:
                    case Code.Ldc_I8: case Code.Ldc_R4: case Code.Ldc_R8:
                    case Code.Ldnull: case Code.Ldstr:
                        stack.Push(GetConstant(instr));
                        break;
                    case Code.Ldloc: case Code.Ldloc_S: case Code.Ldloc_0: case Code.Ldloc_1: case Code.Ldloc_2: case Code.Ldloc_3:
                        int li = GetLocalIndex(instr);
                        if (li >= 0 && li < locals.Length) stack.Push(locals[li]);
                        break;
                    case Code.Stloc: case Code.Stloc_S: case Code.Stloc_0: case Code.Stloc_1: case Code.Stloc_2: case Code.Stloc_3:
                        int si = GetLocalIndex(instr);
                        if (si >= 0 && si < locals.Length && stack.Count > 0)
                        {
                            locals[si] = stack.Pop();
                        }
                        break;
                    case Code.Pop: if (stack.Count > 0) stack.Pop(); break;
                    case Code.Dup: if (stack.Count > 0) stack.Push(stack.Peek()); break;
                    case Code.Add: if (stack.Count >= 2) { var b=stack.Pop(); var a=stack.Pop(); stack.Push(Add(a,b)); } break;
                    case Code.Sub: if (stack.Count >= 2) { var b=stack.Pop(); var a=stack.Pop(); stack.Push(Sub(a,b)); } break;
                }
            }
            catch { /* Ignore emulation errors */ }
        }

        private bool SimplifyJunkInstructions(MethodDef method)
        {
            // Удаление очевидного мусора, если не найден сложный state machine
            var body = method.Body;
            bool changed = false;
            for (int i = body.Instructions.Count - 1; i >= 0; i--)
            {
                var instr = body.Instructions[i];
                if (instr.OpCode.Code == Code.Nop)
                {
                    // Проверка, не цель ли это перехода
                    bool isTarget = false;
                    foreach(var x in body.Instructions) {
                        if (x.Operand == instr || (x.Operand is Instruction[] arr && arr.Contains(instr))) {
                            isTarget = true; break;
                        }
                    }
                    if (!isTarget) {
                        body.Instructions.RemoveAt(i);
                        changed = true;
                    }
                }
            }
            if (changed) body.UpdateInstructionOffsets();
            return changed;
        }

        private void ReplaceBody(MethodDef method, List<Instruction> newInstructions)
        {
            var body = method.Body;
            body.Instructions.Clear();
            foreach (var instr in newInstructions)
                body.Instructions.Add(instr);
            
            body.UpdateInstructionOffsets();
            body.SimplifyMacros(method.Parameters);
            
            // Пересчет исключений и локалей может потребоваться в сложных случаях,
            // но для базового unpacker достаточно обновления оффсетов.
        }

        private int CleanupAll()
        {
            int count = 0;
            foreach (var type in _module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody) continue;
                    RemoveUnreachableBlocks(method);
                    RemoveNops(method);
                    count++;
                }
            }
            return count;
        }

        private void RemoveUnreachableBlocks(MethodDef method)
        {
            var body = method.Body;
            if (body.Instructions.Count == 0) return;

            var reachable = new HashSet<Instruction>();
            var queue = new Queue<Instruction>();

            queue.Enqueue(body.Instructions[0]);
            reachable.Add(body.Instructions[0]);

            while (queue.Count > 0)
            {
                var curr = queue.Dequeue();
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
            if (changed) RemoveNops(method);
        }

        private void RemoveNops(MethodDef method)
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
            body.UpdateInstructionOffsets();
        }

        private void RenameObfuscatedItems()
        {
            bool useAi = _aiConfig.Enabled;
            AiAssistant? ai = null;

            if (useAi)
            {
                Console.WriteLine("[*] Checking AI connection...");
                ai = new AiAssistant(_aiConfig);
                
                if (!ai.IsConnected)
                {
                    Console.WriteLine("[!] AI Connection Failed. Falling back to heuristic renaming.");
                    useAi = false;
                    ai.Dispose();
                    ai = null;
                }
                else
                {
                    // Дополнительная проверка модели (попытка мини-запроса)
                    Console.WriteLine($"[+] AI Connected successfully. Model: {_aiConfig.ModelName}");
                }
            }

            int renamedCount = 0;

            foreach (var type in _module.GetTypes())
            {
                if (IsObfuscatedName(type.Name))
                {
                    string newName = useAi && ai != null ? ai.GetSuggestedName(type.Name, "Class definition", "Class") : GenerateSimpleTypeName();
                    if (!string.IsNullOrEmpty(newName) && newName != type.Name)
                    {
                        type.Name = newName;
                        renamedCount++;
                        Log($"Renamed Type: {type.Name}");
                    }
                }

                foreach (var method in type.Methods)
                {
                    if (method.IsConstructor || method.IsStaticConstructor) continue;
                    if (IsObfuscatedName(method.Name))
                    {
                        string snippet = GetMethodSnippet(method);
                        string retType = method.ReturnType?.ToString() ?? "void";
                        
                        string newName = GenerateSimpleMethodName();
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

        #region Helpers

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

        private int GetLocalIndex(Instruction i)
        {
            if (IsLdloc(i, out int idx) || IsStloc(i, out idx)) return idx;
            return -1;
        }

        private object? GetConstant(Instruction i)
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
                case Code.Ldstr: return i.Operand as string;
                case Code.Ldnull: return null;
                default: return null;
            }
        }

        private bool Compare(object? a, object? b, Code op)
        {
            if (a == null || b == null) return false;
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
            return false;
        }

        private object? Add(object? a, object? b)
        {
            if (a is double da && b is double db) return da + db;
            if (a is long la && b is long lb) return la + lb;
            if (a is int ia && b is int ib) return ia + ib;
            return null;
        }

        private object? Sub(object? a, object? b)
        {
            if (a is double da && b is double db) return da - db;
            if (a is long la && b is long lb) return la - lb;
            if (a is int ia && b is int ib) return ia - ib;
            return null;
        }

        private Instruction CloneInstruction(Instruction orig)
        {
            var op = orig.OpCode;
            var operand = orig.Operand;
            if (operand is Local l) return Instruction.Create(op, l);
            if (operand is Parameter p) return Instruction.Create(op, p);
            if (operand is Instruction t) return Instruction.Create(op, t); // Внимание: связь может сохраниться, но для нового списка это ок
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

        private bool IsObfuscatedName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.StartsWith("<")) return false;
            if (name.Length <= 2 && name.All(char.IsLetter)) return true;
            if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-z]{1,2}\d+$")) return true;
            if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Z]{1}\d+$")) return true;
            return false;
        }

        private string GenerateSimpleTypeName() => "Class_" + (_renameCounter++);
        private string GenerateSimpleMethodName() => "Method_" + (_renameCounter++);

        private string GetMethodSnippet(MethodDef m)
        {
            if (!m.HasBody) return "";
            var sb = new System.Text.StringBuilder();
            int count = Math.Min(15, m.Body.Instructions.Count);
            for (int i = 0; i < count; i++)
                sb.AppendLine(m.Body.Instructions[i].ToString());
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
