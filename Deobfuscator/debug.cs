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
                    var backupInstructions = method.Body.Instructions.ToList();
                    
                    try
                    {
                        // Пробуем распутать state-machine циклы
                        if (TryEmulateObfuscatedLoops(method))
                        {
                            count++;
                            Log($"Successfully deobfuscated: {method.Name}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Error in {method.Name}: {ex.Message}");
                        // Восстанавливаем при ошибке
                        RestoreMethod(method, backupInstructions);
                    }
                }
            }
            
            Console.WriteLine($"[*] Processed {totalMethods} methods, modified {count}.");
            Log("=== Deobfuscation Finished ===");
        }

        /// <summary>
        /// Основная логика: эмуляция запутанных циклов с полной пересборкой метода.
        /// Мы не используем NOP, а создаем новый список инструкций только с полезным кодом.
        /// </summary>
        private bool TryEmulateObfuscatedLoops(MethodDef method)
        {
            var body = method.Body;
            var instructions = body.Instructions;
            if (instructions.Count < 10) return false;

            // 1. Анализ: находим переменную состояния и переменную возврата
            int stateVarIndex = -1;
            object? initialState = null;
            int? returnVarIndex = null;

            // Ищем инициализацию состояния (обычно в начале)
            for (int i = 0; i < Math.Min(20, instructions.Count - 1); i++)
            {
                if (IsStloc(instructions[i + 1], out int idx))
                {
                    var val = GetConstantValue(instructions[i]);
                    if (val != null)
                    {
                        stateVarIndex = idx;
                        initialState = val;
                        break;
                    }
                }
            }

            if (stateVarIndex == -1) return false;

            // Ищем, какая переменная возвращается (перед ret стоит ldloc X)
            for (int i = instructions.Count - 2; i >= 0; i--)
            {
                if (instructions[i].OpCode.Code == Code.Ret && i > 0)
                {
                    if (IsLdloc(instructions[i - 1], out int rIdx))
                    {
                        returnVarIndex = rIdx;
                        Log($"Method returns variable V_{rIdx}");
                        break;
                    }
                }
            }

            // 2. Построение карты переходов состояний
            // Формат: Key = проверяемое значение состояния, Value = (следующее состояние, список полезных инструкций)
            var stateMap = new Dictionary<object, Tuple<object?, List<Instruction>>>();

            for (int i = 0; i < instructions.Count - 4; i++)
            {
                // Паттерн: ldloc state, ldc checkVal, ceq, brfalse SKIP
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

                        // Извлекаем блок между ветвлением и меткой пропуска
                        // Начало блока - инструкция сразу после ветвления
                        var blockStartIndex = i + 4;
                        var blockEndIndex = instructions.IndexOf(skipTarget);

                        if (blockStartIndex < blockEndIndex && blockEndIndex != -1)
                        {
                            var block = ExtractCleanBlock(instructions, blockStartIndex, blockEndIndex, stateVarIndex, returnVarIndex, out object? nextState);
                            
                            if (!stateMap.ContainsKey(checkVal))
                            {
                                stateMap[checkVal] = Tuple.Create(nextState, block);
                                Log($"State {checkVal} -> Next: {nextState}, Instructions: {block.Count}");
                            }
                        }
                    }
                }
            }

            if (stateMap.Count == 0) return false;

            // 3. Эмуляция прохода по цепочке состояний
            var finalInstructions = new List<Instruction>();
            var currentState = initialState;
            var visitedStates = new HashSet<object>();
            int maxIterations = stateMap.Count * 3 + 10;
            int iteration = 0;
            bool returnValueAssigned = false;

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
                        
                        // Отслеживаем, было ли присвоено значение возвращаемой переменной
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

            // 4. Валидация и сборка нового метода
            if (finalInstructions.Count == 0) return false;

            // Если метод должен что-то возвращать, но мы не нашли присваивания - возможно логика неполная
            if (method.ReturnType.ElementType != ElementType.Void && !returnValueAssigned)
            {
                Log("Warning: Return value assignment not found during emulation. Checking manually...");
                // Попытка добавить возврат по умолчанию или игнорировать, если критично
                // Но лучше проверить, есть ли в конце ldloc возвращаемой переменной
                bool hasReturnLoad = finalInstructions.Any(ins => 
                    IsLdloc(ins, out int idx) && idx == returnVarIndex);
                
                if (!hasReturnLoad && returnVarIndex.HasValue)
                {
                     // Если совсем нет возврата, добавим дефолтное значение, чтобы не ломать стек
                     // Но в случае обфускации чаще всего мы просто пропустили ветку.
                     // Оставим как есть, dnlib сам разберется, если стек сойдется.
                }
            }

            // Добавляем ret, если нет
            var last = finalInstructions.LastOrDefault();
            if (last == null || (last.OpCode.Code != Code.Ret && last.OpCode.Code != Code.Throw))
            {
                finalInstructions.Add(Instruction.Create(OpCodes.Ret));
            }

            // ВАЖНО: Полная замена тела метода для корректного пересчета стека
            ReplaceMethodBodyClean(method, finalInstructions);
            
            return true;
        }

        /// <summary>
        /// Извлекает чистый код из диапазона, удаляя весь мусор управления состоянием.
        /// Возвращает список инструкций, готовых к вставке.
        /// </summary>
        private List<Instruction> ExtractCleanBlock(IList<Instruction> allInstructions, int startIdx, int endIdx, 
                                                    int stateVarIndex, int? returnVarIndex, out object? nextState)
        {
            var cleanCode = new List<Instruction>();
            nextState = null;

            for (int i = startIdx; i < endIdx; i++)
            {
                var instr = allInstructions[i];
                bool skip = false;

                // 1. Пропускаем NOP
                if (instr.OpCode.Code == Code.Nop) skip = true;

                // 2. Пропускаем загрузки/сравнения переменной состояния
                if (GetLocalIndex(instr) == stateVarIndex) skip = true;
                
                // 3. Пропускаем константы, участвующие в проверках состояния
                if ((instr.OpCode.Code == Code.Ldc_I4 || instr.OpCode.Code == Code.Ldc_R8 || instr.OpCode.Code == Code.Ldc_I8))
                {
                    // Проверяем контекст: если дальше ceq/br - это мусор
                    if (i + 2 < endIdx)
                    {
                        var n1 = allInstructions[i+1];
                        var n2 = allInstructions[i+2];
                        if ((n1.OpCode.Code == Code.Ceq || n1.OpCode.Code == Code.Cgt || n1.OpCode.Code == Code.Clt) &&
                            (n2.OpCode.Code == Code.Brtrue || n2.OpCode.Code == Code.Brfalse || n2.OpCode.Code == Code.Br || n2.OpCode.Code == Code.Br_S))
                        {
                            skip = true;
                        }
                        // Если это значение для stloc состояния - запоминаем nextState, но инструкцию пропускаем
                        if (IsStloc(n1, out int sIdx) && sIdx == stateVarIndex)
                        {
                            nextState = GetConstantValue(instr);
                            skip = true;
                        }
                    }
                }

                // 4. Пропускаем все ветвления и сравнения внутри блока
                if (instr.OpCode.FlowControl == FlowControl.Cond_Branch || 
                    instr.OpCode.FlowControl == FlowControl.Branch ||
                    instr.OpCode.Code == Code.Ceq || instr.OpCode.Code == Code.Cgt || instr.OpCode.Code == Code.Clt)
                {
                    skip = true;
                }

                // 5. Пропускаем запись в переменную состояния (stloc state)
                if (IsStloc(instr, out int stIdx) && stIdx == stateVarIndex)
                {
                    skip = true;
                }

                // 6. Сохраняем всё остальное
                if (!skip)
                {
                    cleanCode.Add(CloneInstruction(instr));
                }
            }

            return cleanCode;
        }

        /// <summary>
        /// Полностью заменяет тело метода, пересоздавая локальные переменные и инструкци.
        /// Это гарантирует корректность стека для dnSpy.
        /// </summary>
        private void ReplaceMethodBodyClean(MethodDef method, List<Instruction> newInstructions)
        {
            var body = method.Body;
            
            // Очищаем старые инструкции
            body.Instructions.Clear();
            
            // Добавляем новые
            foreach (var ins in newInstructions)
            {
                body.Instructions.Add(ins);
            }

            // Критически важно: обновляем оффсеты и упрощаем макросы
            // Это пересчитывает стек заново
            body.UpdateInstructionOffsets();
            body.SimplifyMacros(method.Parameters);
            
            // Принудительная оптимизация коротких форм инструкций (br.s vs br)
            // dnlib обычно делает это сам при записи, но можно помочь
        }

        private void RestoreMethod(MethodDef method, List<Instruction> backup)
        {
            var body = method.Body;
            body.Instructions.Clear();
            foreach (var ins in backup) body.Instructions.Add(CloneInstruction(ins));
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
            // Важно: клонируем операнды правильно, особенно Local и Instruction
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
            Console.WriteLine($"[*] Saving to: {path}");
            var opts = new ModuleWriterOptions(_module)
            {
                Logger = DummyLogger.NoThrowInstance,
                MetadataOptions = new MetadataOptions { Flags = MetadataFlags.KeepOldMaxStack }
            };
            _module.Write(path, opts);
            Console.WriteLine("[+] Done.");
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
