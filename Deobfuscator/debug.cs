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
                        
                        // Пробуем новый подход: поиск и удаление мусорных state-машин
                        if (originalCount > 10)
                        {
                            Log("Analyzing for obfuscated state machine patterns...");
                            bool unpacked = UnpackStateMachineByContent(method);
                            
                            if (unpacked)
                            {
                                wasModified = true;
                                Log($"State machine unpacked by content analysis. Instructions: {originalCount} -> {method.Body.Instructions.Count}");
                            }
                            else
                            {
                                Log("No valid state machine pattern found or extraction failed.");
                            }
                        }
                        
                        // Проверяем, не стал ли метод пустым или битым
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

        /// <summary>
        /// НОВЫЙ ПОДХОД: 
        /// 1. Находим переменную состояния.
        /// 2. Ищем все блоки кода, привязанные к условиям этой переменной.
        /// 3. Анализируем содержимое блоков: если там есть вызовы методов (call) или возврат (ret/stloc результата) - блок полезный.
        /// 4. Если блок содержит только математику над переменной состояния - это мусор (переход).
        /// 5. Собираем новый метод только из полезных блоков в порядке логического выполнения.
        /// </summary>
        private bool UnpackStateMachineByContent(MethodDef method)
        {
            var body = method.Body;
            var instructions = body.Instructions;
            if (instructions.Count < 15) return false;

            // 1. Поиск переменной состояния (обычно инициализируется в начале)
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
                        Log($"Found state variable: V_{stateVarIndex}, Initial: {initialState}");
                        break;
                    }
                }
            }

            if (stateVarIndex == -1 || initialState == null)
            {
                Log("No clear state variable initialization found.");
                return false;
            }

            // 2. Карта состояний: StateValue -> (NextStateValue, List<UsefulInstructions>)
            var stateMap = new Dictionary<object, Tuple<object?, List<Instruction>>>();
            
            // Проходим по всем инструкциям в поисках паттерна проверки состояния
            // Паттерн: ldloc state, ldc checkVal, ceq/clt/cgt, brfalse target_skip
            for (int i = 0; i < instructions.Count - 4; i++)
            {
                if (GetLocalIndex(instructions[i]) == stateVarIndex &&
                    GetConstantValue(instructions[i + 1]) is object checkVal)
                {
                    var cmp = instructions[i + 2];
                    var branch = instructions[i + 3];

                    if ((cmp.OpCode.Code == Code.Ceq || cmp.OpCode.Code == Code.Cgt || cmp.OpCode.Code == Code.Clt) &&
                        (branch.OpCode.Code == Code.Brfalse || branch.OpCode.Code == Code.Brfalse_S))
                    {
                        var skipTarget = branch.Operand as Instruction;
                        if (skipTarget == null) continue;

                        // Блок кода находится между инструкцией после ветвления (i+4) и целевой инструкцией пропуска
                        int blockStartIdx = i + 4;
                        int blockEndIdx = instructions.IndexOf(skipTarget);

                        if (blockStartIdx >= blockEndIdx || blockStartIdx >= instructions.Count) continue;

                        // Анализируем блок
                        var blockInstrs = new List<Instruction>();
                        for (int k = blockStartIdx; k < blockEndIdx; k++)
                        {
                            blockInstrs.Add(instructions[k]);
                        }

                        // ПРОВЕРКА НА ПОЛЕЗНОСТЬ
                        // Блок полезен, если содержит: call, callvirt, ret, stloc (не в переменную состояния)
                        bool isUseful = false;
                        object? nextStateUpdate = null;

                        foreach (var ins in blockInstrs)
                        {
                            if (ins.OpCode.Code == Code.Call || ins.OpCode.Code == Code.Callvirt || ins.OpCode.Code == Code.Newobj)
                            {
                                isUseful = true;
                                Log($"  State {checkVal}: Found CALL instruction -> Useful block detected.");
                            }
                            
                            // Проверка на возврат значения
                            if (ins.OpCode.Code == Code.Ret)
                            {
                                isUseful = true;
                            }

                            // Проверка на запись в локальную переменную (кроме переменной состояния)
                            if (IsStloc(ins, out int sIdx))
                            {
                                if (sIdx != stateVarIndex)
                                {
                                    isUseful = true; // Запись в другую переменную - это полезно
                                }
                                else
                                {
                                    // Это обновление состояния, запоминаем следующее значение
                                    // Ищем константу перед этим stloc
                                    int idxInBlock = blockInstrs.IndexOf(ins);
                                    if (idxInBlock > 0)
                                    {
                                        var prev = blockInstrs[idxInBlock - 1];
                                        nextStateUpdate = GetConstantValue(prev);
                                    }
                                }
                            }
                        }

                        // Если блок НЕ полезен (нет вызовов, нет записей в другие переменные), но есть переход состояния
                        // Это значит, что блок был чисто для запутывания (мусорное условие), но нам нужно знать переход
                        if (!isUseful && nextStateUpdate != null)
                        {
                            Log($"  State {checkVal}: Useless logic block (only state transition to {nextStateUpdate}). Extracting empty.");
                            stateMap[checkVal] = Tuple.Create(nextStateUpdate, new List<Instruction>());
                        }
                        else if (isUseful)
                        {
                            // Извлекаем только полезные инструкции, выкидывая мусор внутри блока
                            var cleanBlock = FilterJunkFromBlock(blockInstrs, stateVarIndex, out object? nextValFromClean);
                            
                            // Приоритет: если нашли nextStateUpdate при сканировании всего блока, используем его. 
                            // Иначе берем то, что нашел фильтр.
                            var finalNextState = nextStateUpdate ?? nextValFromClean;

                            stateMap[checkVal] = Tuple.Create(finalNextState, cleanBlock);
                            Log($"  State {checkVal}: Useful block extracted ({cleanBlock.Count} instrs). Next: {finalNextState}");
                        }
                    }
                }
            }

            if (stateMap.Count == 0)
            {
                Log("No state transitions with recognizable patterns found.");
                return false;
            }

            // 3. Сборка линейного кода путем прохода по цепочке состояний
            var finalInstructions = new List<Instruction>();
            var currentState = initialState;
            var visited = new HashSet<object>();
            int maxIter = stateMap.Count * 3 + 10;
            int iter = 0;

            Log($"Reconstructing flow starting from {initialState}...");

            while (iter < maxIter)
            {
                iter++;
                if (currentState == null || visited.Contains(currentState))
                {
                    Log($"Loop detected or null state at {currentState}. Stopping.");
                    break;
                }
                visited.Add(currentState);

                if (stateMap.TryGetValue(currentState, out var data))
                {
                    var nextSt = data.Item1;
                    var block = data.Item2;

                    // Добавляем полезные инструкции
                    foreach (var ins in block)
                    {
                        finalInstructions.Add(CloneInstruction(ins));
                    }

                    if (nextSt == null) break; // Конец цепочки
                    currentState = nextSt;
                }
                else
                {
                    Log($"State {currentState} not found in map. Stopping.");
                    break;
                }
            }

            if (finalInstructions.Count == 0)
            {
                Log("Resulting method is empty. Aborting changes.");
                return false;
            }

            // Добавляем ret, если нет
            var last = finalInstructions.LastOrDefault();
            if (last == null || (last.OpCode.Code != Code.Ret && last.OpCode.Code != Code.Throw))
            {
                finalInstructions.Add(Instruction.Create(OpCodes.Ret));
            }

            // ВАЖНО: Сохраняем оригинальные локальные переменные, чтобы индексы совпадали
            ReplaceMethodBodyPreservingLocals(method, finalInstructions);
            
            return true;
        }

        /// <summary>
        /// Очищает блок от внутреннего мусора (вложенных проверок состояния, nop, лишних загрузок)
        /// Возвращает список чистых инструкций и найденное следующее состояние (если есть stloc state)
        /// </summary>
        private List<Instruction> FilterJunkFromBlock(List<Instruction> block, int stateVarIndex, out object? nextState)
        {
            var clean = new List<Instruction>();
            nextState = null;

            for (int i = 0; i < block.Count; i++)
            {
                var ins = block[i];

                // Пропускаем Nop
                if (ins.OpCode.Code == Code.Nop) continue;

                // Пропускаем загрузки переменной состояния
                if (GetLocalIndex(ins) == stateVarIndex) continue;

                // Пропускаем константы, если они часть паттерна сравнения внутри блока
                // (иногда обфускатор вставляет лишние проверки внутри полезного блока)
                if ((ins.OpCode.Code == Code.Ldc_I4 || ins.OpCode.Code == Code.Ldc_R8) && i + 2 < block.Count)
                {
                    var n1 = block[i+1];
                    var n2 = block[i+2];
                    if ((n1.OpCode.Code == Code.Ceq || n1.OpCode.Code == Code.Cgt) &&
                        (n2.OpCode.Code == Code.Brtrue || n2.OpCode.Code == Code.Brfalse))
                    {
                        // Это мусорная проверка, пропускаем всю тройку
                        i += 2; 
                        continue;
                    }
                    
                    // Если константа идет перед stloc состояния - это значение перехода
                    if (IsStloc(n1, out int sIdx) && sIdx == stateVarIndex)
                    {
                        nextState = GetConstantValue(ins);
                        i += 1; // Пропускаем и константу, и stloc
                        continue;
                    }
                }

                // Пропускаем сами сравнения и ветвления (они уже обработаны внешней логикой)
                if (ins.OpCode.Code == Code.Ceq || ins.OpCode.Code == Code.Cgt || ins.OpCode.Code == Code.Clt ||
                    ins.OpCode.Code == Code.Brtrue || ins.OpCode.Code == Code.Brfalse ||
                    ins.OpCode.Code == Code.Br || ins.OpCode.Code == Code.Br_S)
                {
                    continue;
                }

                // Пропускаем сток состояния (мы уже вытащили значение выше)
                if (IsStloc(ins, out int stIdx) && stIdx == stateVarIndex)
                {
                    continue;
                }

                // Всё остальное - полезно
                clean.Add(CloneInstruction(ins));
            }

            return clean;
        }

        private void ReplaceMethodBodyPreservingLocals(MethodDef method, List<Instruction> newInstrs)
        {
            // Не трогаем method.Body.Variables, оставляем как есть.
            // Это критично, так как наши клонированные инструкции ссылаются на те же объекты Local.
            
            var body = method.Body;
            body.Instructions.Clear();
            foreach (var i in newInstrs) body.Instructions.Add(i);
            
            body.UpdateInstructionOffsets();
            body.SimplifyMacros(method.Parameters);
        }

        private void RestoreMethod(MethodDef method, List<Instruction> backupInstructions, List<Local> backupLocals)
        {
            var body = method.Body;
            body.Instructions.Clear();
            foreach (var instr in backupInstructions)
            {
                body.Instructions.Add(CloneInstruction(instr));
            }
            // Восстанавливаем локальные, если они были изменены (на всякий случай)
            body.Variables.Clear();
            foreach (var loc in backupLocals)
            {
                body.Variables.Add(loc);
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
            // Важно: если операнд - Local, мы должны передать тот же самый объект Local из метода,
            // а не создавать новый. dnlib обычно хранит ссылки на объекты Local в методе.
            // Если мы клонируем инструкцию из того же метода (или backup), ссылка должна остаться той же.
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
