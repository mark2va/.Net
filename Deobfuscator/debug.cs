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
                        
                        // Пробуем эмулировать запутанные циклы
                        if (originalCount > 10 && TryEmulateObfuscatedLoops(method))
                        {
                            wasModified = true;
                            Log($"State machine unpacked successfully.");
                            
                            // Дополнительная очистка: убираем лишние nop в начале/конце если нужно
                            CleanupNops(method);
                        }
                        
                        if (wasModified)
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
        /// Эмулирует выполнение запутанных циклов, заменяя мусор на NOP.
        /// </summary>
        private bool TryEmulateObfuscatedLoops(MethodDef method)
        {
            var body = method.Body;
            var instructions = body.Instructions;
            if (instructions.Count < 15) return false;

            // 1. Поиск переменной состояния
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

            // 2. Строим карту переходов и определяем полезные блоки
            // Ключ: значение состояния. Значение: список инструкций, которые НУЖНО ОСТАВИТЬ (полезный код).
            var usefulBlocks = new Dictionary<object, List<Instruction>>();
            var transitions = new Dictionary<object, object?>();

            for (int i = 0; i < instructions.Count - 4; i++)
            {
                // Паттерн: ldloc state + ldc checkVal + ceq + brfalse SKIP
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

                        // Находим начало блока (сразу после ветвления)
                        Instruction blockStart = instructions[i + 4];
                        
                        // Извлекаем полезный код до метки пропуска
                        var block = ExtractUsefulCodeBlock(instructions, blockStart, skipTarget, stateVarIndex, out object? nextState);
                        
                        if (!usefulBlocks.ContainsKey(checkVal))
                        {
                            usefulBlocks[checkVal] = block;
                            transitions[checkVal] = nextState;
                            Log($"Found block for state {checkVal}. Next: {nextState}. Useful ops: {block.Count}");
                        }
                    }
                }
            }

            if (usefulBlocks.Count == 0)
            {
                Log("No valid state blocks found.");
                return false;
            }

            // 3. Генерируем новый код, эмулируя порядок выполнения
            var finalInstructions = new List<Instruction>();
            var currentState = initialState;
            var visited = new HashSet<object>();
            int maxIter = usefulBlocks.Count * 3 + 10;
            int iter = 0;

            Log($"Emulating flow from {currentState}...");

            while (iter < maxIter)
            {
                iter++;
                if (visited.Contains(currentState) || currentState == null) break;
                visited.Add(currentState);

                if (usefulBlocks.TryGetValue(currentState, out var block))
                {
                    foreach (var ins in block)
                    {
                        finalInstructions.Add(CloneInstruction(ins));
                    }
                    
                    if (transitions.TryGetValue(currentState, out var next) && next != null)
                    {
                        currentState = next;
                    }
                    else
                    {
                        break; // Конец цепочки
                    }
                }
                else
                {
                    Log($"No block for state {currentState}, stopping.");
                    break;
                }
            }

            if (finalInstructions.Count == 0)
            {
                Log("Resulting code is empty.");
                return false;
            }

            // Добавляем ret, если его нет
            var last = finalInstructions.LastOrDefault();
            if (last == null || (last.OpCode.Code != Code.Ret && last.OpCode.Code != Code.Throw))
            {
                finalInstructions.Add(Instruction.Create(OpCodes.Ret));
            }

            // 4. ВАЖНО: Полная замена тела метода с пересозданием локальных переменных
            ReplaceMethodBodySafe(method, finalInstructions);
            
            Log($"Emulation complete. New instruction count: {finalInstructions.Count}");
            return true;
        }

        /// <summary>
        /// Извлекает полезные инструкции, игнорируя управление состоянием.
        /// Возвращает список инструкций, которые нужно СОХРАНИТЬ.
        /// </summary>
        private List<Instruction> ExtractUsefulCodeBlock(IList<Instruction> allInstructions, Instruction startInstr, 
                                                            Instruction endMarker, int stateVarIndex, out object? nextState)
        {
            var result = new List<Instruction>();
            nextState = null;

            int startIdx = allInstructions.IndexOf(startInstr);
            int endIdx = allInstructions.IndexOf(endMarker);

            if (startIdx == -1 || endIdx == -1 || startIdx >= endIdx)
            {
                return result;
            }

            for (int i = startIdx; i < endIdx; i++)
            {
                var instr = allInstructions[i];
                bool isJunk = false;

                // 1. Пропускаем NOP
                if (instr.OpCode.Code == Code.Nop)
                {
                    isJunk = true;
                }
                // 2. Пропускаем загрузку переменной состояния
                else if (GetLocalIndex(instr) == stateVarIndex)
                {
                    isJunk = true;
                }
                // 3. Пропускаем константы, используемые для сравнения или обновления состояния
                else if (instr.OpCode.Code == Code.Ldc_I4 || instr.OpCode.Code == Code.Ldc_I8 || 
                         instr.OpCode.Code == Code.Ldc_R4 || instr.OpCode.Code == Code.Ldc_R8)
                {
                    // Смотрим вперед
                    if (i + 1 < endIdx)
                    {
                        var next = allInstructions[i + 1];
                        // Если дальше ceq/clt/cgt -> это условие (мусор)
                        if (next.OpCode.Code == Code.Ceq || next.OpCode.Code == Code.Cgt || next.OpCode.Code == Code.Clt)
                        {
                            isJunk = true;
                        }
                        // Если дальше stloc state -> это обновление состояния (сохраняем значение, но не инструкцию)
                        else if (IsStloc(next, out int sIdx) && sIdx == stateVarIndex)
                        {
                            nextState = GetConstantValue(instr);
                            isJunk = true; 
                        }
                    }
                }
                // 4. Пропускаем сравнения и ветвления внутри блока
                else if (instr.OpCode.Code == Code.Ceq || instr.OpCode.Code == Code.Cgt || instr.OpCode.Code == Code.Clt ||
                         instr.OpCode.Code == Code.Brtrue || instr.OpCode.Code == Code.Brtrue_S ||
                         instr.OpCode.Code == Code.Brfalse || instr.OpCode.Code == Code.Brfalse_S ||
                         instr.OpCode.Code == Code.Br || instr.OpCode.Code == Code.Br_S)
                {
                    isJunk = true;
                }
                // 5. Пропускаем само присваивание состояния (stloc state)
                else if (IsStloc(instr, out int stIdx) && stIdx == stateVarIndex)
                {
                    isJunk = true;
                }

                if (!isJunk)
                {
                    result.Add(instr);
                }
            }

            return result;
        }

        /// <summary>
        /// Безопасная замена тела метода с сохранением сигнатуры локальных переменных.
        /// </summary>
        private void ReplaceMethodBodySafe(MethodDef method, List<Instruction> newInstructions)
        {
            var body = method.Body;
            
            // Очищаем текущие инструкции
            body.Instructions.Clear();
            
            // Добавляем новые
            foreach (var ins in newInstructions)
            {
                body.Instructions.Add(ins);
            }
            
            // Пересчитываем оффсеты и упрощаем макросы
            body.UpdateInstructionOffsets();
            body.SimplifyMacros(method.Parameters);
            
            // Важно: dnSpy любит, когда стек сбалансирован. 
            // Убедимся, что нет битых переходов (хотя мы строим линейный код, так что их быть не должно)
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
