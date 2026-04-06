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
            Console.WriteLine("[*] Phase 1: Unraveling Control Flow (State Machine Emulation)...");
            Log("Starting control flow unraveling...");
            int unraveledCount = UnravelAllStateMachines();
            Console.WriteLine($"[+] Unraveled {unraveledCount} methods.");
            Log($"Unraveled {unraveledCount} methods.");

            Console.WriteLine("[*] Phase 2: Deep Cleanup (Dead Code & Nops)...");
            Log("Starting deep cleanup...");
            CleanupAll();
            
            Console.WriteLine("[*] Phase 3: Renaming Obfuscated Symbols...");
            Log("Starting renaming process...");
            RenameObfuscatedItems();

            Log("=== Deobfuscation Finished ===");
        }

        /// <summary>
        /// Главный метод распутывания. Проходит по всем методам и применяет эмуляцию.
        /// </summary>
        private int UnravelAllStateMachines()
        {
            int count = 0;
            foreach (var type in _module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody || !method.Body.IsIL) continue;
                    
                    // Пытаемся распутать. Если метод изменился - считаем успехом.
                    if (EmulateAndSimplify(method))
                    {
                        count++;
                    }
                }
            }
            return count;
        }

        /// <summary>
        /// Эмулирует выполнение метода, чтобы восстановить реальный поток управления.
        /// Алгоритм:
        /// 1. Находим переменную состояния (обычно int/long локаль).
        /// 2. Запускаем эмулятор, который отслеживает значение этой переменной.
        /// 3. Строим новый список инструкций, игнорируя фейковые ветвления.
        /// </summary>
        private bool EmulateAndSimplify(MethodDef method)
        {
            var body = method.Body;
            if (body.Instructions.Count < 5) return false;

            // 1. Поиск переменной состояния
            // Ищем локаль, которая часто участвует в паттернах: stloc, ldloc, ceq, switch/br
            int? stateVarIndex = FindStateVariable(method);
            
            if (!stateVarIndex.HasValue)
            {
                // Если явной стейт-переменной нет, пробуем упрощенную очистку unreachable кода
                // Это может помочь при простой обфускации мусорными переходами
                RemoveUnreachableBlocks(method);
                return false; 
            }

            Log($"  Found state variable V_{stateVarIndex.Value} in {method.Name}");

            // 2. Эмуляция
            // Мы будем собирать новые инструкции в этот список
            var newInstructions = new List<Instruction>();
            
            // Стек эмулятора
            var stack = new Stack<object?>();
            // Локальные переменные эмулятора
            var locals = new object?[body.Variables.Count];
            
            // Карта: Инструкция оригинала -> Инструкция в новом списке (если добавлена)
            var instrMap = new Dictionary<Instruction, Instruction>();
            
            // Очередь для обхода графа (BFS), но с приоритетом порядка следования
            // Храним: (Индекс инструкции в оригинале, Значение стейта ПЕРЕД этой инструкцией)
            var queue = new Queue<Tuple<int, object?>>();
            
            // Отслеживаем посещенные состояния, чтобы не зациклиться в эмуляции обфускатора
            // Ключ: (Индекс инструкции, Значение стейта)
            var visitedStates = new HashSet<string>();

            // Точка входа
            queue.Enqueue(Tuple.Create(0, (object?)null));

            int maxSteps = body.Instructions.Count * 100; // Защита от бесконечности
            int steps = 0;

            while (queue.Count > 0 && steps < maxSteps)
            {
                steps++;
                var current = queue.Dequeue();
                int ip = current.Item1; // Instruction Pointer (index)
                object? currentState = current.Item2;

                if (ip < 0 || ip >= body.Instructions.Count) continue;

                string stateKey = $"{ip}:{currentState}";
                if (visitedStates.Contains(stateKey)) continue;
                visitedStates.Add(stateKey);

                // Если мы уже добавили эту инструкцию в новый список через другой путь, 
                // нам не нужно эмулировать её снова, но нужно убедиться, что переходы на неё работают.
                // Однако, для простоты эмуляции потока, мы просто идем дальше.
                
                var instr = body.Instructions[ip];

                // --- СИМВОЛИЧЕСКОЕ ВЫПОЛНЕНИЕ ---
                
                // Обработка записи в переменную состояния
                if (IsStloc(instr, out int sIdx) && sIdx == stateVarIndex.Value)
                {
                    // Значение должно быть на стеке
                    if (stack.Count > 0)
                    {
                        var val = stack.Pop();
                        locals[stateVarIndex.Value] = val;
                        // Эта инструкция (stloc) в новый код НЕ попадает, так как это часть обфускации
                        // Переходим к следующей
                        queue.Enqueue(Tuple.Create(ip + 1, val)); // Передаем новое значение стейта дальше
                        continue; 
                    }
                }

                // Обработка чтения переменной состояния (для условий)
                if (IsLdloc(instr, out int lIdx) && lIdx == stateVarIndex.Value)
                {
                    var val = locals[stateVarIndex.Value];
                    stack.Push(val);
                    // Добавляем в новый код? Обычно ldloc стейта - это часть условия обфускации.
                    // Мы постараемся удалить всё условие целиком позже, если сможем его вычислить.
                    // Пока просто пропускаем добавление в newInstructions, если это чисто служебное чтение.
                    // Но осторожно: если стейт используется в реальной логике (редко), надо оставить.
                    // Эвристика: если следующее指令 - ldc + ceq, то это мусор.
                    
                    bool isJunkRead = false;
                    if (ip + 2 < body.Instructions.Count)
                    {
                        var next = body.Instructions[ip+1];
                        var next2 = body.Instructions[ip+2];
                        if (IsConst(next) && IsCmp(next2)) isJunkRead = true;
                    }

                    if (!isJunkRead)
                    {
                        var newInstr = CloneInstruction(instr);
                        newInstructions.Add(newInstr);
                        instrMap[instr] = newInstr;
                    }
                    
                    queue.Enqueue(Tuple.Create(ip + 1, currentState));
                    continue;
                }

                // Обработка условных переходов, зависящих от стейта
                if (instr.OpCode.FlowControl == FlowControl.Cond_Branch)
                {
                    // Пытаемся вычислить условие прямо сейчас
                    bool? conditionResult = EvaluateCondition(stack, locals, stateVarIndex.Value, instr);

                    if (conditionResult.HasValue)
                    {
                        // Условие вычислено! Это фейковый переход обфускатора.
                        // Мы не добавляем само условие (ldloc, ldc, ceq, br) в новый код.
                        // Просто идем по нужной ветке.
                        
                        Instruction target;
                        if (conditionResult.Value)
                        {
                            target = instr.Operand as Instruction;
                        }
                        else
                        {
                            // Если ветка не взята, идем на следующую инструкцию (fallthrough)
                            // НО: в обфускаторах часто бывает, что "false" ведет в мусор, а "true" по коду ниже.
                            // Или наоборот. Нам нужно найти индекс следующей инструкции физически.
                            target = (ip + 1 < body.Instructions.Count) ? body.Instructions[ip + 1] : null;
                        }

                        if (target != null)
                        {
                            int targetIdx = body.Instructions.IndexOf(target);
                            // Передаем текущее значение стейта (оно не изменилось в момент перехода)
                            queue.Enqueue(Tuple.Create(targetIdx, currentState));
                        }
                        continue; // Не добавляем инструкцию перехода в новый код
                    }
                    else
                    {
                        // Не смогли вычислить (реальное условие программы?).
                        // Оставляем как есть, но мапим операнды.
                        var newInstr = CloneInstruction(instr);
                        newInstructions.Add(newInstr);
                        instrMap[instr] = newInstr;
                        
                        // Ветвим эмуляцию по обоим путям, так как не знаем результат
                        if (instr.Operand is Instruction t)
                            queue.Enqueue(Tuple.Create(body.Instructions.IndexOf(t), currentState));
                        queue.Enqueue(Tuple.Create(ip + 1, currentState));
                        continue;
                    }
                }

                // Обработка безусловного перехода
                if (instr.OpCode.FlowControl == FlowControl.Branch)
                {
                    if (instr.Operand is Instruction t)
                    {
                        int targetIdx = body.Instructions.IndexOf(t);
                        queue.Enqueue(Tuple.Create(targetIdx, currentState));
                        continue; // Сам br не нужен, если мы перепрыгиваем
                    }
                }

                // Обработка ret/throw
                if (instr.OpCode.FlowControl == FlowControl.Return || instr.OpCode.FlowControl == FlowControl.Throw)
                {
                    var newInstr = CloneInstruction(instr);
                    newInstructions.Add(newInstr);
                    instrMap[instr] = newInstr;
                    continue;
                }

                // --- ОБЫЧНЫЕ ИНСТРУКЦИИ ---
                // Эмулируем стек для поддержки вычислений
                SimulateStackOp(instr, stack, locals);

                // Добавляем инструкцию в очищенный код
                // Фильтруем явный мусор (ldc сразу перед stloc стейта мы уже обработали выше косвенно)
                // Но сюда попадают арифметика, вызовы и т.д.
                
                // Пропускаем ldc, если они идут парой со stloc стейта (мы это уже обработали в блоке Stloc)
                // Но здесь мы можем пропустить одиночные ldc, которые остались? Нет, они нужны.
                // Главное: не добавлять ldc/stloc самой переменной стейта, если они служебные.
                
                bool skipAdd = false;
                if (IsStloc(instr, out int skIdx) && skIdx == stateVarIndex.Value) skipAdd = true;
                if (IsLdloc(instr, out int lkIdx) && lkIdx == stateVarIndex.Value) skipAdd = true; // Уже обработано выше
                if (IsConst(instr) && ip + 1 < body.Instructions.Count && IsStloc(body.Instructions[ip+1], out int nsIdx) && nsIdx == stateVarIndex.Value) skipAdd = true;

                if (!skipAdd)
                {
                    var newInstr = CloneInstruction(instr);
                    newInstructions.Add(newInstr);
                    instrMap[instr] = newInstr;
                }

                // Переход к следующей
                queue.Enqueue(Tuple.Create(ip + 1, currentState));
            }

            if (newInstructions.Count == 0) return false;

            // Применяем изменения
            ReplaceMethodBody(method, newInstructions);
            return true;
        }

        #region Helpers for Emulation

        private int? FindStateVariable(MethodDef method)
        {
            var counts = new Dictionary<int, int>();
            var instrs = method.Body.Instructions;

            for (int i = 0; i < instrs.Count; i++)
            {
                if (IsStloc(instrs[i], out int idx))
                {
                    if (!counts.ContainsKey(idx)) counts[idx] = 0;
                    counts[idx]++;
                }
            }

            // Кандидат: переменная, которая пишется много раз (состояние меняется)
            // Обычно в обфускаторах это одна переменная с частыми записями
            foreach (var kvp in counts)
            {
                if (kvp.Value >= 3) return kvp.Key;
            }
            return null;
        }

        private bool? EvaluateCondition(Stack<object?> stack, object?[] locals, int stateVarIdx, Instruction branchInstr)
        {
            // Пытаемся отмотать стек назад, чтобы найти операнды условия
            // Это сложно сделать постфактум, поэтому полагаемся на то, что эмулятор поддерживал стек корректно.
            // Но в текущей реализации BFS стек сбрасывается. 
            // УПРОЩЕНИЕ: В обфускаторах условие почти всегда: ldloc(state), ldc(val), ceq/cgt/clt, br...
            // Посмотрим назад в оригинальном коде.
            
            var instrs = method.Body.Instructions; // Ошибка: method не доступен в этом контексте напрямую без передачи
            // Исправление: передавать instrs или использовать замыкание. 
            // Для простоты, реализуем проверку через локальные значения, если они известны.
            
            // В данной реализации BFS мы не храним полный стек для каждой точки входа эффективно.
            // Поэтому вернемся к стратегии: если мы знаем значение locals[stateVarIdx], 
            // и видим паттерн в коде ПОСЛЕ выполнения предыдущих шагов...
            
            // Альтернатива: Условие уже должно было быть вычислено при прохождении предыдущих инструкций?
            // Нет, в BFS мы прыгаем.
            
            // Вернемся к надежному методу: анализ паттернов вокруг branchInstr.
            // Ищем: ... ldloc(state), ldc(X), ceq, br ...
            int idx = Array.IndexOf(method.Body.Instructions.ToArray(), branchInstr);
            if (idx < 3) return null;

            var i1 = method.Body.Instructions[idx - 1]; // cmp
            var i2 = method.Body.Instructions[idx - 2]; // ldc
            var i3 = method.Body.Instructions[idx - 3]; // ldloc

            if (IsLdloc(i3, out int lIdx) && lIdx == stateVarIdx && 
                IsConst(i2, out object? constVal) && 
                IsCmp(i1, out Code cmpCode))
            {
                var currentVal = locals[stateVarIdx];
                if (currentVal != null && constVal != null)
                {
                    return Compare(currentVal, constVal, cmpCode);
                }
            }
            
            // Проверка на brtrue/brfalse от переменной
            if ((branchInstr.OpCode.Code == Code.Brtrue || branchInstr.OpCode.Code == Code.Brfalse ||
                 branchInstr.OpCode.Code == Code.Brtrue_S || branchInstr.OpCode.Code == Code.Brfalse_S))
            {
                 if (idx >= 1)
                 {
                     var prev = method.Body.Instructions[idx - 1];
                     if (IsLdloc(prev, out int lIdx2) && lIdx2 == stateVarIdx)
                     {
                         var val = locals[stateVarIdx];
                         if (val != null)
                         {
                             bool isZero = Convert.ToInt64(val) == 0;
                             bool isBrTrue = branchInstr.OpCode.Code == Code.Brtrue || branchInstr.OpCode.Code == Code.Brtrue_S;
                             return isBrTrue ? !isZero : isZero;
                         }
                     }
                 }
            }

            return null;
        }
        
        // Нужно передать массив инструкций в EvaluateCondition или иметь доступ к method
        // Исправим сигнатуру выше и передадим массив. 
        // В методе EmulateAndSimplify у нас есть access to body.Instructions.
        // Чтобы не усложнять, сделаем EvaluateCondition внутренним методом с доступом к closure или передадим массив.
        // В коде выше вызов: EvaluateCondition(stack, locals, stateVarIndex.Value, instr);
        // Добавим параметр instructions.
        
        private bool? EvaluateCondition(Stack<object?> stack, object?[] locals, int stateVarIdx, Instruction branchInstr, IList<Instruction> instructions)
        {
             int idx = -1;
             // Быстрый поиск индекса (в реальном коде лучше хранить мапу Instruction->Index)
             for(int i=0; i<instructions.Count; i++) if(instructions[i] == branchInstr) { idx = i; break; }
             
             if (idx < 3) return null;

             var i1 = instructions[idx - 1]; 
             var i2 = instructions[idx - 2]; 
             var i3 = instructions[idx - 3]; 

             if (IsLdloc(i3, out int lIdx) && lIdx == stateVarIdx && 
                 IsConst(i2, out object? constVal) && 
                 IsCmp(i1, out Code cmpCode))
             {
                 var currentVal = locals[stateVarIdx];
                 if (currentVal != null && constVal != null)
                     return Compare(currentVal, constVal, cmpCode);
             }
             
             if ((branchInstr.OpCode.Code == Code.Brtrue || branchInstr.OpCode.Code == Code.Brfalse ||
                  branchInstr.OpCode.Code == Code.Brtrue_S || branchInstr.OpCode.Code == Code.Brfalse_S))
             {
                  if (idx >= 1)
                  {
                      var prev = instructions[idx - 1];
                      if (IsLdloc(prev, out int lIdx2) && lIdx2 == stateVarIdx)
                      {
                          var val = locals[stateVarIdx];
                          if (val != null)
                          {
                              long v = Convert.ToInt64(val);
                              bool isZero = v == 0;
                              bool isBrTrue = branchInstr.OpCode.Code == Code.Brtrue || branchInstr.OpCode.Code == Code.Brtrue_S;
                              return isBrTrue ? !isZero : isZero;
                          }
                      }
                  }
             }
             return null;
        }
        // Примечание: в коде EmulateAndSimplify нужно заменить вызов на новый с параметром body.Instructions

        private void SimulateStackOp(Instruction instr, Stack<object?> stack, object?[] locals)
        {
            try
            {
                switch (instr.OpCode.Code)
                {
                    case Code.Ldc_I4: case Code.Ldc_I4_0: case Code.Ldc_I4_1: case Code.Ldc_I4_2: 
                    case Code.Ldc_I4_3: case Code.Ldc_I4_4: case Code.Ldc_I4_5: case Code.Ldc_I4_6: 
                    case Code.Ldc_I4_7: case Code.Ldc_I4_8: case Code.Ldc_I4_M1:
                    case Code.Ldc_I8: case Code.Ldc_R4: case Code.Ldc_R8:
                        stack.Push(GetConstValue(instr));
                        break;
                    case Code.Ldloc: case Code.Ldloc_S: case Code.Ldloc_0: case Code.Ldloc_1: case Code.Ldloc_2: case Code.Ldloc_3:
                        int li = GetLocalIndex(instr);
                        if (li >= 0 && li < locals.Length) stack.Push(locals[li]);
                        break;
                    case Code.Stloc: case Code.Stloc_S: case Code.Stloc_0: case Code.Stloc_1: case Code.Stloc_2: case Code.Stloc_3:
                        int si = GetLocalIndex(instr);
                        if (si >= 0 && si < locals.Length && stack.Count > 0)
                            locals[si] = stack.Pop();
                        break;
                    case Code.Add:
                        if (stack.Count >= 2) { var b = stack.Pop(); var a = stack.Pop(); stack.Push(Add(a, b)); }
                        break;
                    case Code.Sub:
                        if (stack.Count >= 2) { var b = stack.Pop(); var a = stack.Pop(); stack.Push(Sub(a, b)); }
                        break;
                    // Можно добавить больше операций для точности
                }
            }
            catch { }
        }

        #endregion

        private void CleanupAll()
        {
            foreach (var type in _module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (method.HasBody)
                    {
                        RemoveUnreachableBlocks(method);
                        CleanupNops(method);
                        SimplifyMacros(method);
                    }
                }
            }
        }

        private void RemoveUnreachableBlocks(MethodDef method)
        {
            var body = method.Body;
            var reachable = new HashSet<Instruction>();
            var q = new Queue<Instruction>();

            if (body.Instructions.Count > 0)
            {
                q.Enqueue(body.Instructions[0]);
                reachable.Add(body.Instructions[0]);
            }

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
            body.UpdateInstructionOffsets();
        }

        private void SimplifyMacros(MethodDef method)
        {
            method.Body.SimplifyMacros(method.Parameters);
            method.Body.UpdateInstructionOffsets();
        }

        private void ReplaceMethodBody(MethodDef m, List<Instruction> newInstrs)
        {
            var body = m.Body;
            body.Instructions.Clear();
            foreach (var i in newInstrs) body.Instructions.Add(i);
            body.UpdateInstructionOffsets();
            body.SimplifyMacros(m.Parameters);
        }

        private void RenameObfuscatedItems()
        {
            bool useAi = _aiConfig.Enabled;
            AiAssistant? ai = null;

            if (useAi)
            {
                // Жесткая проверка подключения и модели
                Console.WriteLine("[*] Checking AI connection and model availability...");
                ai = new AiAssistant(_aiConfig);
                
                if (!ai.IsConnected)
                {
                    Console.WriteLine("[!] AI Server unreachable. Falling back to simple renaming.");
                    useAi = false;
                    ai.Dispose();
                    ai = null;
                }
                else if (!ai.IsModelAvailable())
                {
                    Console.WriteLine($"[!] Model '{_aiConfig.ModelName}' not found on server. Falling back to simple renaming.");
                    useAi = false;
                    ai.Dispose();
                    ai = null;
                }
                else
                {
                    Console.WriteLine($"[+] AI Ready: {_aiConfig.ApiUrl} | Model: {_aiConfig.ModelName}");
                }
            }

            int renamedCount = 0;

            foreach (var type in _module.GetTypes())
            {
                if (IsObfuscatedName(type.Name))
                {
                    string newName = useAi && ai != null ? ai.GetSuggestedName(type.Name, "Class definition", "Class") : GenerateSimpleTypeName();
                    if (!string.IsNullOrEmpty(newName))
                    {
                        type.Name = newName;
                        renamedCount++;
                    }
                }

                foreach (var method in type.Methods)
                {
                    if (method.Name == ".cctor" || method.Name == ".ctor") continue;
                    if (IsObfuscatedName(method.Name))
                    {
                        string snippet = GetMethodSnippet(method);
                        string retType = method.ReturnType?.ToString() ?? "void";
                        
                        string newName = "Method_" + _obfCounter++;
                        if (useAi && ai != null)
                        {
                            string aiName = ai.GetSuggestedName(method.Name, snippet, retType);
                            if (!string.IsNullOrEmpty(aiName) && aiName != method.Name && IsValidName(aiName))
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

        private string GenerateSimpleTypeName() => "Class_" + (_obfCounter++);
        
        private string GetMethodSnippet(MethodDef m)
        {
            if (!m.HasBody) return "";
            var sb = new System.Text.StringBuilder();
            int count = Math.Min(20, m.Body.Instructions.Count);
            for (int i = 0; i < count; i++)
                sb.AppendLine(m.Body.Instructions[i].ToString());
            return sb.ToString();
        }

        private bool IsObfuscatedName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.StartsWith("<")) return false; 
            if (name.Length <= 2 && name.All(char.IsLetter)) return true;
            if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-z]{1,3}\d+$")) return true;
            if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Z]{1}\d+$")) return true;
            return false;
        }

        private bool IsValidName(string n)
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
        }

        public void Dispose()
        {
            _logWriter?.Close();
            _module.Dispose();
        }

        #region Static Helpers
        
        // Необходимо исправить вызов EvaluateCondition в EmulateAndSimplify, передав туда body.Instructions
        // Но так как это внутренний класс, проще сделать хелперы статическими или полями класса.
        // В методе EmulateAndSimplify замените вызов:
        // bool? conditionResult = EvaluateCondition(stack, locals, stateVarIndex.Value, instr, body.Instructions);

        private static bool IsStloc(Instruction i, out int idx)
        {
            idx = -1;
            if (i.Operand is Local l) idx = l.Index;
            else if (i.OpCode.Code == Code.Stloc_0) idx = 0;
            else if (i.OpCode.Code == Code.Stloc_1) idx = 1;
            else if (i.OpCode.Code == Code.Stloc_2) idx = 2;
            else if (i.OpCode.Code == Code.Stloc_3) idx = 3;
            return idx != -1;
        }

        private static bool IsLdloc(Instruction i, out int idx)
        {
            idx = -1;
            if (i.Operand is Local l) idx = l.Index;
            else if (i.OpCode.Code == Code.Ldloc_0) idx = 0;
            else if (i.OpCode.Code == Code.Ldloc_1) idx = 1;
            else if (i.OpCode.Code == Code.Ldloc_2) idx = 2;
            else if (i.OpCode.Code == Code.Ldloc_3) idx = 3;
            return idx != -1;
        }

        private static int GetLocalIndex(Instruction i)
        {
            if (IsLdloc(i, out int idx) || IsStloc(i, out idx)) return idx;
            return -1;
        }

        private static bool IsConst(Instruction i, out object? val)
        {
            val = null;
            switch (i.OpCode.Code)
            {
                case Code.Ldc_I4: val = i.Operand as int?; return true;
                case Code.Ldc_I4_0: val = 0; return true;
                case Code.Ldc_I4_1: val = 1; return true;
                case Code.Ldc_I4_2: val = 2; return true;
                case Code.Ldc_I4_3: val = 3; return true;
                case Code.Ldc_I4_4: val = 4; return true;
                case Code.Ldc_I4_5: val = 5; return true;
                case Code.Ldc_I4_6: val = 6; return true;
                case Code.Ldc_I4_7: val = 7; return true;
                case Code.Ldc_I4_8: val = 8; return true;
                case Code.Ldc_I4_M1: val = -1; return true;
                case Code.Ldc_I8: val = i.Operand as long?; return true;
                case Code.Ldc_R4: val = i.Operand as float?; return true;
                case Code.Ldc_R8: val = i.Operand as double?; return true;
                default: return false;
            }
        }
        
        private static bool IsConst(Instruction i) => IsConst(i, out _);

        private static bool IsCmp(Instruction i, out Code code)
        {
            code = i.OpCode.Code;
            return code == Code.Ceq || code == Code.Cgt || code == Code.Clt || code == Code.Cgt_Un || code == Code.Clt_Un;
        }
        
        private static bool IsCmp(Instruction i) => IsCmp(i, out _);

        private static object? GetConstValue(Instruction i)
        {
            IsConst(i, out var val);
            return val;
        }

        private static bool? Compare(object? a, object? b, Code op)
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

        private static object? Add(object? a, object? b)
        {
            if (a is double da && b is double db) return da + db;
            if (a is long la && b is long lb) return la + lb;
            if (a is int ia && b is int ib) return ia + ib;
            return null;
        }

        private static object? Sub(object? a, object? b)
        {
            if (a is double da && b is double db) return da - db;
            if (a is long la && b is long lb) return la - lb;
            if (a is int ia && b is int ib) return ia - ib;
            return null;
        }

        private static Instruction CloneInstruction(Instruction orig)
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
    }
}
