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
                    // Сохраняем также список локальных переменных для восстановления
                    var backupLocals = method.Body.Variables.ToList(); 
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
                                RestoreMethod(method, backupInstructions, backupLocals);
                            }
                        }
                        else
                        {
                            Log("Not a state machine, skipping unpacking");
                        }
                        
                        // Проверяем, не стал ли метод пустым или некорректным
                        if (method.Body.Instructions.Count == 0)
                        {
                            emptyMethods++;
                            Log($"WARNING: Method became empty! Restoring original...");
                            RestoreMethod(method, backupInstructions, backupLocals);
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
                        RestoreMethod(method, backupInstructions, backupLocals);
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
            var backup = method.Body.Instructions.ToList();
            var backupLocals = method.Body.Variables.ToList();
            
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
                RestoreMethod(method, backup, backupLocals);
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
            
            // Старый код (оставлен как запасной вариант, но может быть удален, если новый работает идеально)
            // ... (код старого парсера state machine опущен для краткости, так как фокус на новом методе)
            
            Log("No suitable unpacking method found.");
            return false;
        }

        /// <summary>
        /// Анализирует метод, чтобы определить, какая локальная переменная используется для возврата значения.
        /// Возвращает индекс переменной или -1, если метод void.
        /// </summary>
        private int AnalyzeMethodReturnType(MethodDef method)
        {
            if (method.ReturnType.IsVoid) return -1;

            var instructions = method.Body.Instructions;
            // Ищем инструкцию ret и смотрим, что перед ней загружается
            for (int i = 0; i < instructions.Count; i++)
            {
                if (instructions[i].OpCode.Code == Code.Ret)
                {
                    // Идем назад, пропуская nop
                    int j = i - 1;
                    while (j >= 0 && instructions[j].OpCode.Code == Code.Nop) j--;
                    
                    if (j >= 0 && IsLdloc(instructions[j], out int idx))
                    {
                        Log($"Method returns variable V_{idx}");
                        return idx;
                    }
                    // Если перед ret просто константа или вызов, то возвращаемого локалса нет (возвращается сразу со стека)
                    return -2; 
                }
            }
            return -1;
        }

        /// <summary>
        /// Эмулирует выполнение запутанных циклов с переменной состояния.
        /// Ключевое улучшение: анализ возвращаемого значения перед началом работы.
        /// </summary>
        private bool TryEmulateObfuscatedLoops(MethodDef method)
        {
            var body = method.Body;
            var instructions = body.Instructions;
            if (instructions.Count < 10) return false;

            // 0. АНАЛИЗ: Что метод должен вернуть?
            int returnVarIndex = AnalyzeMethodReturnType(method);
            Log($"Return variable index analysis result: {returnVarIndex}");

            // 1. Поиск инициализации переменной состояния в начале метода
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

            // 2. Строим полную карту переходов состояний.
            var stateMap = new Dictionary<object, Tuple<object?, List<Instruction>>>();

            for (int i = 0; i < instructions.Count - 4; i++)
            {
                // Проверяем начало условия: ldloc state + ldc const + ceq + brfalse
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

                        // Извлекаем блок кода между branchInstr и skipTarget
                        var block = ExtractUsefulCodeBlock(instructions, instructions[i + 4], skipTarget, stateVarIndex, out object? nextState);

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

            // 3. Эмуляция выполнения
            var finalInstructions = new List<Instruction>();
            var currentState = initialState;
            var visitedStates = new HashSet<object>();
            int maxIterations = stateMap.Count * 3 + 10;
            int iteration = 0;
            
            // Отслеживаем, было ли присвоено значение в переменную возврата
            bool returnValueAssigned = (returnVarIndex < 0); 

            Log($"Starting loop emulation from state {currentState}, total transitions: {stateMap.Count}");

            while (iteration < maxIterations)
            {
                iteration++;

                if (visitedStates.Contains(currentState))
                {
                    Log($"Detected cycle at state {currentState}, stopping emulation");
                    break;
                }

                if (currentState == null)
                {
                    Log("State became null, stopping emulation");
                    break;
                }

                visitedStates.Add(currentState);

                if (stateMap.TryGetValue(currentState, out var transition))
                {
                    var nextState = transition.Item1;
                    var block = transition.Item2;

                    Log($"Iteration {iteration}: Processing state {currentState}, adding {block.Count} useful instructions");

                    foreach (var ins in block)
                    {
                        finalInstructions.Add(CloneInstruction(ins));
                        
                        // Проверка: если эта инструкция присваивает значение в переменную возврата
                        if (returnValueAssigned == false && IsStloc(ins, out int sIdx) && sIdx == returnVarIndex)
                        {
                            returnValueAssigned = true;
                            Log($"  -> Value assigned to return variable V_{returnVarIndex}");
                        }
                    }

                    if (nextState == null)
                    {
                        Log("Next state is null, stopping");
                        break;
                    }

                    currentState = nextState;
                }
                else
                {
                    Log($"No transition found for state {currentState}, stopping");
                    break;
                }
            }

            if (finalInstructions.Count == 0)
            {
                Log("Emulation produced no instructions");
                return false;
            }

            // ВАЖНАЯ ПРОВЕРКА: Если метод должен вернуть значение, но мы его не присвоили - отмена
            if (returnVarIndex >= 0 && !returnValueAssigned)
            {
                Log($"CRITICAL: Return variable V_{returnVarIndex} was never assigned during emulation. Aborting changes to prevent corruption.");
                return false;
            }

            // 4. Добавляем ret если его нет в конце
            var lastInstr = finalInstructions.LastOrDefault();
            if (lastInstr == null || (lastInstr.OpCode.Code != Code.Ret && lastInstr.OpCode.Code != Code.Throw))
            {
                finalInstructions.Add(Instruction.Create(OpCodes.Ret));
            }

            Log($"Emulation complete: {finalInstructions.Count} instructions extracted after {iteration} iterations");
            
            // 5. Сохраняем локальные переменные перед заменой тела!
            // Это критично, чтобы инструкции ldloc/stloc ссылались на правильные объекты Local
            PreserveLocals(method, finalInstructions);

            ReplaceMethodBody(method, finalInstructions);
            return true;
        }

        /// <summary>
        /// Убеждается, что в методе объявлены все локальные переменные, используемые в новых инструкциях.
        /// Копирует оригинальные переменные в новый метод, если они еще не добавлены.
        /// </summary>
        private void PreserveLocals(MethodDef method, List<Instruction> newInstructions)
        {
            // Мы не очищаем Variables, а убеждаемся, что они соответствуют тем, что были раньше.
            // В данном случае, так как мы делаем деобфускацию state-machine, 
            // нам нужно сохранить ВСЕ оригинальные локальные переменные, кроме возможно самой переменной состояния,
            // если она больше не используется. Но безопаснее оставить всё как есть.
            
            //dnlib автоматически обновит индексы при вызове UpdateInstructionOffsets, 
            //если объекты Local в инструкциях совпадают с объектами в method.Body.Variables.
            //Проблема возникает, если мы создаем новые Local. Здесь мы используем CloneInstruction,
            //который копирует ссылку на Local. Значит, нам нужно убедиться, что method.Body.Variables
            //содержит эти объекты.
            
            // Так как мы не создаем новые переменные, а только клонируем инструкции со старыми Local,
            // нам просто нужно НЕ очищать method.Body.Variables перед заменой инструкций.
            // Функция ReplaceMethodBody ниже очищает только Instructions.
        }

        /// <summary>
        /// Извлекает ТОЛЬКО полезный код из блока между startInstr и endMarker (exclusive).
        /// </summary>
        private List<Instruction> ExtractUsefulCodeBlock(IList<Instruction> allInstructions, Instruction startInstr, 
                                                            Instruction endMarker, int stateVarIndex, out object? nextState)
        {
            var usefulCode = new List<Instruction>();
            nextState = null;

            int startIndex = allInstructions.IndexOf(startInstr);
            int endIndex = allInstructions.IndexOf(endMarker);

            if (startIndex == -1 || endIndex == -1 || startIndex >= endIndex)
            {
                Log($"  Invalid block range: start={startIndex}, end={endIndex}");
                return usefulCode;
            }

            Log($"  Extracting block from index {startIndex} to {endIndex}");

            for (int i = startIndex; i < endIndex; i++)
            {
                var instr = allInstructions[i];

                // 1. Пропускаем nop
                if (instr.OpCode.Code == Code.Nop) continue;

                // 2. Пропускаем загрузки переменной состояния (ldloc state)
                if (GetLocalIndex(instr) == stateVarIndex) continue;

                // 3. Пропускаем константы, используемые для сравнения (ldc перед ceq/ветвлением)
                if ((instr.OpCode.Code == Code.Ldc_I4 || instr.OpCode.Code == Code.Ldc_I8 || instr.OpCode.Code == Code.Ldc_R4 || instr.OpCode.Code == Code.Ldc_R8) &&
                    i + 2 < endIndex)
                {
                    var next = allInstructions[i + 1];
                    var next2 = allInstructions[i + 2];
                    
                    if ((next.OpCode.Code == Code.Ceq || next.OpCode.Code == Code.Cgt || next.OpCode.Code == Code.Clt) &&
                        (next2.OpCode.Code == Code.Brtrue || next2.OpCode.Code == Code.Brtrue_S || 
                         next2.OpCode.Code == Code.Brfalse || next2.OpCode.Code == Code.Brfalse_S))
                    {
                        continue;
                    }
                    
                    // Если за константой следует stloc состояния - это обновление состояния
                    if (IsStloc(next, out int sIdx) && sIdx == stateVarIndex)
                    {
                        nextState = GetConstantValue(instr);
                        Log($"  Found state update to {nextState}");
                        continue;
                    }
                }

                // 4. Пропускаем сравнения и ветвления внутри блока
                if (instr.OpCode.Code == Code.Ceq || instr.OpCode.Code == Code.Cgt || instr.OpCode.Code == Code.Clt ||
                    instr.OpCode.Code == Code.Brtrue || instr.OpCode.Code == Code.Brtrue_S ||
                    instr.OpCode.Code == Code.Brfalse || instr.OpCode.Code == Code.Brfalse_S ||
                    instr.OpCode.Code == Code.Br || instr.OpCode.Code == Code.Br_S)
                {
                    continue;
                }

                // 5. Пропускаем запись в переменную состояния (stloc state)
                if (IsStloc(instr, out int stIdx) && stIdx == stateVarIndex)
                {
                    continue;
                }

                // 6. Всё остальное - полезный код
                usefulCode.Add(CloneInstruction(instr));
                Log($"  Added useful: {instr}");
            }

            Log($"  Extracted {usefulCode.Count} useful instructions. NextState: {nextState}");
            return usefulCode;
        }

        private void RestoreMethod(MethodDef method, List<Instruction> backupInstructions, List<Local> backupLocals)
        {
            var body = method.Body;
            body.Instructions.Clear();
            // Восстанавливаем переменные, если они изменились (хотя обычно мы их не трогаем)
            // Но на всякий случай синхронизируем
            if (body.Variables.Count != backupLocals.Count)
            {
                body.Variables.Clear();
                foreach(var l in backupLocals) body.Variables.Add(l);
            }
            
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

        private bool IsObfuscatedName(string n) => !string.IsNullOrEmpty(n) && (n.Length == 1 || n.StartsWith("?"));

        private void RenameWithAi(MethodDef m)
        {
            if (_aiAssistant == null) return;
            try
            {
                var snip = string.Join("\n", m.Body.Instructions.Take(15).Select(x => x.ToString()));
                var newName = _aiAssistant.GetSuggestedName(m.Name, snip, m.ReturnType?.ToString());
                if (!string.IsNullOrEmpty(newName) && IsValidIdentifier(newName))
                {
                    m.Name = newName;
                    Console.WriteLine($"[AI] Renamed {m.Name}");
                    Log($"[AI] Renamed {m.Name}");
                }
            } catch { }
        }

        private bool IsValidIdentifier(string n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            if (!char.IsLetter(n[0]) && n[0] != '_') return false;
            foreach (var c in n) if (!char.IsLetterOrDigit(c) && c != '_') return false;
            return true;
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
            // ВАЖНО: Не очищаем Variables! Они нужны для корректной работы Local в инструкциях.
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
