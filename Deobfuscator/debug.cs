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
                    
                    var backupInstructions = method.Body.Instructions.ToList();
                    var backupVariables = method.Body.Variables.ToList();
                    int originalCount = backupInstructions.Count;
                    Log($"Original instructions: {originalCount}");

                    try
                    {
                        bool wasModified = false;
                        
                        // Показываем первые инструкции для анализа
                        if (_debugMode && originalCount > 0)
                        {
                            Log("First 10 instructions:");
                            _indentLevel++;
                            for (int i = 0; i < Math.Min(10, originalCount); i++)
                            {
                                Log(backupInstructions[i].ToString());
                            }
                            _indentLevel--;
                        }
                        
                        // Только если метод действительно выглядит как state machine
                        if (originalCount > 10 && IsDefinitelyStateMachine(method))
                        {
                            Log("Method is definitely a state machine, attempting to unpack...");
                            bool unpacked = UnpackStateMachineSafe(method);
                            if (unpacked)
                            {
                                wasModified = true;
                                Log($"State machine unpacked. Instructions: {originalCount} -> {method.Body.Instructions.Count}");
                            }
                            else
                            {
                                Log("Unpacking failed, keeping original");
                                RestoreMethod(method, backupInstructions, backupVariables);
                            }
                        }
                        else
                        {
                            Log("Not a state machine, skipping unpacking");
                        }
                        
                        // Проверяем, не стал ли метод пустым или битым
                        if (method.Body.Instructions.Count == 0)
                        {
                            emptyMethods++;
                            Log($"WARNING: Method became empty! Restoring original...");
                            RestoreMethod(method, backupInstructions, backupVariables);
                            wasModified = false;
                        }
                        else if (wasModified)
                        {
                            count++;
                            Log($"Method successfully processed. Final instructions: {method.Body.Instructions.Count}");
                            
                            if (_debugMode && method.Body.Instructions.Count > 0)
                            {
                                Log("First 10 instructions after processing:");
                                _indentLevel++;
                                for (int i = 0; i < Math.Min(10, method.Body.Instructions.Count); i++)
                                {
                                    Log(method.Body.Instructions[i].ToString());
                                }
                                _indentLevel--;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        string err = $"[!] Error in {method.FullName}: {ex.Message}";
                        Console.WriteLine(err);
                        Log(err);
                        Log($"Restoring original method...");
                        RestoreMethod(method, backupInstructions, backupVariables);
                    }
                    
                    _indentLevel--;
                }
            }
            
            string doneMsg = $"[*] Processed {totalMethods} methods, modified {count} methods, {emptyMethods} methods were empty and restored.";
            Console.WriteLine(doneMsg);
            Log(doneMsg);
            Log("=== Deobfuscation Finished ===");
        }

        private bool IsDefinitelyStateMachine(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            if (instructions.Count < 20) return false;
            
            int statePatterns = 0;
            int switchInstructions = 0;
            
            for (int i = 0; i < instructions.Count - 3; i++)
            {
                // Паттерн: загрузка переменной состояния + сравнение + ветвление
                if (IsLdloc(instructions[i], out int idx) &&
                    GetConstantValue(instructions[i + 1]) != null &&
                    (instructions[i + 2].OpCode.Code == Code.Ceq || 
                     instructions[i + 2].OpCode.Code == Code.Cgt ||
                     instructions[i + 2].OpCode.Code == Code.Clt) &&
                    (instructions[i + 3].OpCode.Code == Code.Brtrue ||
                     instructions[i + 3].OpCode.Code == Code.Brfalse))
                {
                    statePatterns++;
                }
                
                // Паттерн: switch
                if (instructions[i].OpCode.Code == Code.Switch)
                {
                    switchInstructions++;
                }
            }
            
            return statePatterns >= 3 || switchInstructions > 0;
        }

        private bool UnpackStateMachineSafe(MethodDef method)
        {
            var backupInstr = method.Body.Instructions.ToList();
            var backupVars = method.Body.Variables.ToList();
            
            try
            {
                bool result = UnpackStateMachine(method);
                
                if (result && method.Body.Instructions.Count > 0)
                {
                    var lastInstr = method.Body.Instructions.LastOrDefault();
                    if (lastInstr != null && 
                        lastInstr.OpCode.Code != Code.Ret && 
                        lastInstr.OpCode.Code != Code.Throw)
                    {
                        Log("Method doesn't end with ret/throw, adding ret...");
                        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                        method.Body.UpdateInstructionOffsets();
                    }
                    
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log($"UnpackStateMachine failed: {ex.Message}");
                RestoreMethod(method, backupInstr, backupVars);
            }
            
            return false;
        }

        private bool UnpackStateMachine(MethodDef method)
        {
            var body = method.Body;
            if (body == null || body.Instructions.Count < 5) return false;

            var instructions = body.Instructions;
            
            // Сначала пробуем новый подход с эмуляцией запутанных циклов
            if (TryEmulateObfuscatedLoops(method))
            {
                Log("Successfully emulated obfuscated loops");
                return true;
            }
            
            // Старая логика (резерв)
            Log("New loop emulation failed, trying legacy state machine logic...");
            
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
                        var block = ExtractBlockImproved(instructions, target, stateVarIndex, out object? nextVal);
                        
                        if (!stateBlocks.ContainsKey(checkVal) && block.Count > 0)
                        {
                            stateBlocks[checkVal] = block;
                            transitions[checkVal] = nextVal;
                            Log($"Found block for state {checkVal}. Next state: {nextVal}. Instructions: {block.Count}");
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
                if (visited.Contains(currentState)) continue;
                visited.Add(currentState);
                Log($"Processing state: {currentState}");

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

        /// <summary>
        /// Эмулирует выполнение запутанных циклов с переменной состояния.
        /// Ключевое изменение: Анализ возвращаемого значения и фильтрация мусорных присваиваний.
        /// </summary>
        private bool TryEmulateObfuscatedLoops(MethodDef method)
        {
            var body = method.Body;
            var instructions = body.Instructions;
            if (instructions.Count < 10) return false;

            // 1. АНАЛИЗ ВОЗВРАЩАЕМОГО ЗНАЧЕНИЯ
            // Находим переменную, которая загружается перед ret. Это и есть результат метода.
            int? returnVarIndex = null;
            for (int i = instructions.Count - 1; i >= 0; i--)
            {
                if (instructions[i].OpCode.Code == Code.Ret)
                {
                    // Идем назад, пропуская nop
                    int j = i - 1;
                    while (j >= 0 && instructions[j].OpCode.Code == Code.Nop) j--;
                    
                    if (j >= 0 && IsLdloc(instructions[j], out int rIdx))
                    {
                        returnVarIndex = rIdx;
                        Log($"Detected return variable: V_{returnVarIndex}");
                        break;
                    }
                }
            }

            // 2. Поиск инициализации переменной состояния
            int stateVarIndex = -1;
            object? initialState = null;

            for (int i = 0; i < Math.Min(20, instructions.Count - 1); i++)
            {
                if (IsStloc(instructions[i + 1], out int idx))
                {
                    var val = GetConstantValue(instructions[i]);
                    if (val != null)
                    {
                        stateVarIndex = idx;
                        initialState = val;
                        Log($"Found loop state variable: V_{stateVarIndex}, Initial: {initialState}");
                        break;
                    }
                }
            }

            if (stateVarIndex == -1 || initialState == null)
            {
                Log("No loop state variable found.");
                return false;
            }

            // 3. Строим карту переходов
            var stateMap = new Dictionary<object, Tuple<object?, List<Instruction>>>();

            for (int i = 0; i < instructions.Count - 4; i++)
            {
                if (GetLocalIndex(instructions[i]) == stateVarIndex &&
                    GetConstantValue(instructions[i + 1]) is object checkVal)
                {
                    var cmpInstr = instructions[i + 2];
                    var branchInstr = instructions[i + 3];

                    if ((cmpInstr.OpCode.Code == Code.Ceq || cmpInstr.OpCode.Code == Code.Cgt || cmpInstr.OpCode.Code == Code.Clt) &&
                        (branchInstr.OpCode.Code == Code.Brfalse || branchInstr.OpCode.Code == Code.Brfalse_S))
                    {
                        var skipTarget = branchInstr.Operand as Instruction;
                        if (skipTarget == null) continue;

                        // Передаем returnVarIndex в экстрактор, чтобы он знал, что сохранять
                        var block = ExtractUsefulCodeBlock(instructions, instructions[i + 4], skipTarget, stateVarIndex, returnVarIndex, out object? nextState);

                        if (!stateMap.ContainsKey(checkVal))
                        {
                            stateMap[checkVal] = Tuple.Create(nextState, block);
                            Log($"Found transition: state == {checkVal} -> next: {nextState}, useful instructions: {block.Count}");

                            if (_debugMode && block.Count > 0)
                            {
                                _indentLevel++;
                                foreach (var ins in block.Take(10))
                                {
                                    Log($"  {ins}");
                                }
                                if (block.Count > 10) Log($"  ... and {block.Count - 10} more");
                                _indentLevel--;
                            }
                        }
                    }
                }
            }

            if (stateMap.Count == 0)
            {
                Log("No state transitions found in loop pattern.");
                return false;
            }

            // 4. Эмуляция выполнения
            var finalInstructions = new List<Instruction>();
            var currentState = initialState;
            var visitedStates = new HashSet<object>();
            int maxIterations = stateMap.Count * 3 + 10;
            int iteration = 0;
            bool returnValueAssigned = false;

            Log($"Starting loop emulation from state {currentState}");

            while (iteration < maxIterations)
            {
                iteration++;

                if (visitedStates.Contains(currentState) || currentState == null) break;
                visitedStates.Add(currentState);

                if (stateMap.TryGetValue(currentState, out var transition))
                {
                    var nextState = transition.Item1;
                    var block = transition.Item2;

                    foreach (var ins in block)
                    {
                        finalInstructions.Add(CloneInstruction(ins));
                        // Отслеживаем, было ли присваивание в переменную возврата
                        if (returnVarIndex.HasValue && IsStloc(ins, out int sIdx) && sIdx == returnVarIndex.Value)
                        {
                            returnValueAssigned = true;
                        }
                    }

                    if (nextState == null) break;
                    currentState = nextState;
                }
                else
                {
                    break;
                }
            }

            if (finalInstructions.Count == 0)
            {
                Log("Emulation produced no instructions");
                return false;
            }

            // ВАЖНО: Если мы ожидали возврат значения, но присваивания не произошло, значит логика извлечения неверна
            if (returnVarIndex.HasValue && !returnValueAssigned)
            {
                Log("WARNING: Return value was never assigned during emulation. Aborting to prevent corruption.");
                return false;
            }

            // Добавляем ret если нет
            var lastInstr = finalInstructions.LastOrDefault();
            if (lastInstr == null || (lastInstr.OpCode.Code != Code.Ret && lastInstr.OpCode.Code != Code.Throw))
            {
                finalInstructions.Add(Instruction.Create(OpCodes.Ret));
            }

            Log($"Emulation complete: {finalInstructions.Count} instructions extracted.");
            ReplaceMethodBody(method, finalInstructions);
            return true;
        }

        /// <summary>
        /// Извлекает полезный код, умно отфильтровывая мусор.
        /// returnVarIndex: индекс переменной, которую МЕТОД ДОЛЖЕН ВЕРНУТЬ. Её присваивания СОХРАНЯЕМ.
        /// stateVarIndex: индекс переменной цикла. Её присваивания УДАЛЯЕМ.
        /// </summary>
        private List<Instruction> ExtractUsefulCodeBlock(IList<Instruction> allInstructions, Instruction startInstr, 
                                                            Instruction endMarker, int stateVarIndex, int? returnVarIndex, out object? nextState)
        {
            var usefulCode = new List<Instruction>();
            nextState = null;

            int startIndex = allInstructions.IndexOf(startInstr);
            int endIndex = allInstructions.IndexOf(endMarker);

            if (startIndex == -1 || endIndex == -1 || startIndex >= endIndex)
            {
                return usefulCode;
            }

            // Флаг: находимся ли мы внутри последовательности "ldc ... stloc state"?
            // Если да, то ldc тоже нужно удалить, чтобы не оставить голое число на стеке.
            bool pendingStateStore = false;

            for (int i = startIndex; i < endIndex; i++)
            {
                var instr = allInstructions[i];

                // 1. Пропускаем nop
                if (instr.OpCode.Code == Code.Nop) continue;

                // 2. Обработка присваиваний (stloc)
                if (IsStloc(instr, out int stIdx))
                {
                    if (stIdx == stateVarIndex)
                    {
                        // Это обновление состояния цикла.
                        // 1. Сохраняем значение nextState.
                        // 2. НЕ добавляем инструкцию stloc в результат.
                        // 3. Помечаем, что предыдущая инструкция (если это было ldc) тоже была мусором, 
                        //    но так как мы идем вперед, мы просто не добавили её ранее (см. логику ниже).
                        
                        // Ищем значение в предыдущей инструкции, если это константа
                        if (i > startIndex)
                        {
                            var prev = allInstructions[i - 1];
                            var val = GetConstantValue(prev);
                            if (val != null) nextState = val;
                        }
                        
                        pendingStateStore = false; // Сброс флага, так как обработка завершена
                        continue; // Пропускаем stloc
                    }
                    
                    if (returnVarIndex.HasValue && stIdx == returnVarIndex.Value)
                    {
                        // Это присваивание возвращаемой переменной (например, text = call ...)
                        // СОХРАНЯЕМ обязательно.
                        usefulCode.Add(CloneInstruction(instr));
                        continue;
                    }

                    // Присваивание в какую-то третью временную переменную?
                    // Скорее всего мусор, если только это не часть сложного вычисления внутри блока.
                    // Для безопасности пока пропускаем, если это не return и не state.
                    continue;
                }

                // 3. Обработка загрузок констант (ldc)
                if (instr.OpCode.Code == Code.Ldc_I4 || instr.OpCode.Code == Code.Ldc_I8 || 
                    instr.OpCode.Code == Code.Ldc_R4 || instr.OpCode.Code == Code.Ldc_R8)
                {
                    // Смотрим вперед: если следующая значимая инструкция - это stloc stateVar,
                    // значит эта константа нужна только для обновления счетчика цикла. Убираем её.
                    bool isStateUpdateValue = false;
                    for (int k = i + 1; k < endIndex; k++)
                    {
                        var nextIns = allInstructions[k];
                        if (nextIns.OpCode.Code == Code.Nop) continue;
                        
                        if (IsStloc(nextIns, out int nextIdx) && nextIdx == stateVarIndex)
                        {
                            isStateUpdateValue = true;
                            break;
                        }
                        // Если встретилось что-то другое (call, add, stloc other), значит константа нужна
                        break;
                    }

                    if (isStateUpdateValue)
                    {
                        continue; // Пропускаем константу обновления состояния
                    }
                    
                    // Если константа нужна для чего-то другого (аргумент метода и т.д.), оставляем
                    usefulCode.Add(CloneInstruction(instr));
                    continue;
                }

                // 4. Пропускаем загрузки переменной состояния (ldloc state)
                if (GetLocalIndex(instr) == stateVarIndex)
                {
                    continue;
                }

                // 5. Пропускаем сравнения и ветвления (они уже обработаны структурой эмулятора)
                if (instr.OpCode.Code == Code.Ceq || instr.OpCode.Code == Code.Cgt || instr.OpCode.Code == Code.Clt ||
                    instr.OpCode.Code == Code.Brtrue || instr.OpCode.Code == Code.Brtrue_S ||
                    instr.OpCode.Code == Code.Brfalse || instr.OpCode.Code == Code.Brfalse_S ||
                    instr.OpCode.Code == Code.Br || instr.OpCode.Code == Code.Br_S)
                {
                    continue;
                }

                // 6. Всё остальное - полезный код (call, add, sub, ldloc других переменных)
                usefulCode.Add(CloneInstruction(instr));
            }

            return usefulCode;
        }

        private List<Instruction> ExtractBlockImproved(IList<Instruction> allInstructions, Instruction startInstr, int stateVarIndex, out object? nextState)
        {
            var block = new List<Instruction>();
            nextState = null;

            int startIndex = allInstructions.IndexOf(startInstr);
            if (startIndex == -1) return block;

            int ip = startIndex;
            int maxBlockLen = 200;

            while (ip < allInstructions.Count)
            {
                var instr = allInstructions[ip];

                if (ip + 1 < allInstructions.Count)
                {
                    var nextIns = allInstructions[ip + 1];
                    if (IsStloc(nextIns, out int sIdx) && sIdx == stateVarIndex)
                    {
                        var val = GetConstantValue(instr);
                        if (val != null) nextState = val;
                        break;
                    }
                }

                if (instr.OpCode.Code == Code.Ret || instr.OpCode.Code == Code.Throw)
                {
                    block.Add(CloneInstruction(instr));
                    break;
                }

                bool shouldSkip = false;
                if (GetLocalIndex(instr) == stateVarIndex &&
                   (instr.OpCode.Code == Code.Ldloc || instr.OpCode.Code == Code.Ldloc_S ||
                    (instr.OpCode.Code >= Code.Ldloc_0 && instr.OpCode.Code <= Code.Ldloc_3)))
                {
                    shouldSkip = true;
                }

                if (!shouldSkip)
                {
                    block.Add(CloneInstruction(instr));
                }

                ip++;
                if (block.Count > maxBlockLen) break;
            }

            return block;
        }

        private void RestoreMethod(MethodDef method, List<Instruction> backupInstructions, List<Local> backupVariables)
        {
            var body = method.Body;
            body.Instructions.Clear();
            body.Variables.Clear();
            
            foreach (var v in backupVariables) body.Variables.Add(v);
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
            // Важно: не очищаем Variables, если они нужны для новых инструкций, 
            // но в нашем случае мы полагаемся на то, что индексы совпадают с оригиналом.
            // Если бы мы меняли сигнатуру, нужно было бы мапить переменные.
            
            body.Instructions.Clear();
            foreach (var i in newInstrs) body.Instructions.Add(i);
            body.UpdateInstructionOffsets();
            body.SimplifyMacros(m.Parameters);
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
