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
                        
                        // Попытка применить алгоритм de4dot (Control Flow Simplification)
                        if (originalCount > 10 && TrySimplifyControlFlow(method))
                        {
                            wasModified = true;
                            Log($"Control flow simplified. Instructions: {originalCount} -> {method.Body.Instructions.Count}");
                            
                            // Дополнительная очистка nop после упрощения
                            CleanupNops(method);
                        }
                        
                        // Проверка на пустой метод
                        if (method.Body.Instructions.Count == 0)
                        {
                            Log($"WARNING: Method became empty! Restoring original...");
                            RestoreMethod(method, backupInstructions);
                            wasModified = false;
                        }
                        else if (wasModified)
                        {
                            count++;
                            Log($"Method successfully processed.");
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
        /// Реализация упрощения потока управления по мотивам de4dot-cex.
        /// 1. Разбиение на базовые блоки.
        /// 2. Символьное выполнение для вычисления условий.
        /// 3. Удаление ложных ветвей и соединение истинных.
        /// </summary>
        private bool TrySimplifyControlFlow(MethodDef method)
        {
            var body = method.Body;
            if (body.Instructions.Count < 5) return false;

            // Шаг 1: Нормализация (убедимся, что все переходы ведут на инструкции, а не в середину)
            // В dnlib это обычно уже сделано, но для надежности можно вызвать UpdateInstructionOffsets
            body.UpdateInstructionOffsets();

            // Шаг 2: Итеративное упрощение
            // Мы будем повторять процесс, пока код меняется, так как удаление одного блока может открыть возможности для другого
            bool changed = true;
            int maxIterations = 50;
            int iteration = 0;

            while (changed && iteration < maxIterations)
            {
                changed = false;
                iteration++;
                
                // Создаем карту известных значений переменных на текущий момент выполнения (упрощенно)
                // Для сложных обфускаторов вроде вашего, где всё завязано на одной переменной состояния,
                // нам нужно найти эту переменную и попробовать проследить её значения.
                
                // Попытка найти переменную состояния (state variable)
                int stateVarIndex = FindStateVariable(method);
                
                if (stateVarIndex != -1)
                {
                    // Если нашли переменную состояния, пробуем выполнить "символьную эмуляцию"
                    // Мы пройдем по коду и заменим условия, которые можно вычислить, на br или nop
                    if (SimplifyStateBasedConditions(method, stateVarIndex))
                    {
                        changed = true;
                        Log($"Iteration {iteration}: Simplified state-based conditions.");
                        // После изменения инструкций нужно пересчитать оффсеты и удалить мертвый код
                        RemoveUnreachableBlocks(method);
                        continue; // Начинаем новый проход
                    }
                }
                
                // Если специфичная оптимизация не сработала, пробуем общую очистку мертвых блоков
                if (RemoveUnreachableBlocks(method))
                {
                    changed = true;
                    Log($"Iteration {iteration}: Removed unreachable blocks.");
                }
            }

            return changed;
        }

        /// <summary>
        /// Пытается найти переменную, используемую как счетчик состояния цикла.
        /// Обычно это переменная, которой присваивается константа в начале, 
        /// а затем она сравнивается с константами в условиях.
        /// </summary>
        private int FindStateVariable(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            var localUsage = new Dictionary<int, int>(); // Index -> Count of usages

            // Подсчет использований локальных переменных в контексте загрузки и сравнения
            for (int i = 0; i < instructions.Count - 3; i++)
            {
                // Паттерн: ldloc X, ldc Y, ceq, br...
                if ((instructions[i].OpCode.Code == Code.Ldloc || instructions[i].OpCode.Code == Code.Ldloc_S || 
                     (instructions[i].OpCode.Code >= Code.Ldloc_0 && instructions[i].OpCode.Code <= Code.Ldloc_3)) &&
                    IsConstant(instructions[i+1]) &&
                    (instructions[i+2].OpCode.Code == Code.Ceq || instructions[i+2].OpCode.Code == Code.Cgt || instructions[i+2].OpCode.Code == Code.Clt))
                {
                    int idx = GetLocalIndex(instructions[i]);
                    if (idx != -1)
                    {
                        if (!localUsage.ContainsKey(idx)) localUsage[idx] = 0;
                        localUsage[idx]++;
                    }
                }
            }

            // Переменная с наибольшим количеством таких паттернов, скорее всего, является состоянием
            if (localUsage.Count > 0)
            {
                var best = localUsage.OrderByDescending(x => x.Value).First();
                // Порог: должно быть хотя бы 2 сравнения, чтобы считаться state machine
                if (best.Value >= 2) return best.Key;
            }

            return -1;
        }

        /// <summary>
        /// Выполняет упрощение условий, основанных на переменной состояния.
        /// Алгоритм:
        /// 1. Находим начальное значение state переменной.
        /// 2. Идем по коду, поддерживая текущее известное значение state.
        /// 3. Если встречаем проверку (state == X), вычисляем результат.
        /// 4. Если результат известен (true/false), заменяем ветвление на br или nop.
        /// 5. Если встречаем присваивание state = Y, обновляем текущее значение.
        /// </summary>
        private bool SimplifyStateBasedConditions(MethodDef method, int stateVarIndex)
        {
            var instructions = method.Body.Instructions;
            bool modified = false;

            // 1. Поиск начального значения
            object? currentStateValue = null;
            // Ищем в начале метода присваивание stloc stateVar, const
            for (int i = 0; i < Math.Min(20, instructions.Count - 1); i++)
            {
                if (IsStloc(instructions[i+1], out int idx) && idx == stateVarIndex)
                {
                    currentStateValue = GetConstantValue(instructions[i]);
                    break;
                }
            }

            if (currentStateValue == null) return false;

            Log($"Found state variable V_{stateVarIndex}, initial value: {currentStateValue}");

            // 2. Проход по инструкциям для вычисления условий
            // Мы не меняем код напрямую в цикле, чтобы не сломать индексы, а помечаем изменения
            var patches = new List<Action>();

            for (int i = 0; i < instructions.Count - 3; i++)
            {
                var instr = instructions[i];
                
                // Обновляем состояние, если встретили присваивание
                if (IsStloc(instr, out int sIdx) && sIdx == stateVarIndex)
                {
                    // Значение должно быть на стеке перед этой инструкцией (i-1)
                    // Но в простом линейном анализе мы можем посмотреть на предыдущую инструкцию, если это константа
                    if (i > 0 && IsConstant(instructions[i-1]))
                    {
                        currentStateValue = GetConstantValue(instructions[i-1]);
                        Log($"  Updated state to {currentStateValue} at IL_{i:X4}");
                    }
                    else
                    {
                        // Если значение неизвестно (пришло из вычислений), сбрасываем трекер
                        currentStateValue = null;
                        Log($"  State value unknown at IL_{i:X4}, stopping tracking.");
                        break; 
                    }
                    continue;
                }

                // Пропускаем загрузки состояния
                if (GetLocalIndex(instr) == stateVarIndex) continue;

                // Проверка условия: ldloc state, ldc val, ceq/clt/cgt, brtrue/brfalse
                if (GetLocalIndex(instr) == stateVarIndex && 
                    i + 3 < instructions.Count &&
                    IsConstant(instructions[i+1]))
                {
                    var cmpOp = instructions[i+2].OpCode.Code;
                    var branchOp = instructions[i+3].OpCode.Code;
                    
                    if ((cmpOp == Code.Ceq || cmpOp == Code.Cgt || cmpOp == Code.Clt || cmpOp == Code.Cgt_Un || cmpOp == Code.Clt_Un) &&
                        (branchOp == Code.Brtrue || branchOp == Code.Brtrue_S || branchOp == Code.Brfalse || branchOp == Code.Brfalse_S))
                    {
                        object? compareVal = GetConstantValue(instructions[i+1]);
                        if (currentStateValue != null && compareVal != null)
                        {
                            bool result = CompareValues(currentStateValue, compareVal, cmpOp);
                            var branchInstr = instructions[i+3];
                            var target = branchInstr.Operand as Instruction;

                            if (target != null)
                            {
                                Log($"  Evaluating condition (V_{stateVarIndex}={currentStateValue} vs {compareVal}): {result}");
                                
                                // Применяем патч
                                patches.Add(() => {
                                    if (result)
                                    {
                                        // Условие истинно: превращаем в безусловный переход
                                        branchInstr.OpCode = OpCodes.Br;
                                        branchInstr.Operand = target;
                                    }
                                    else
                                    {
                                        // Условие ложно: превращаем в nop
                                        branchInstr.OpCode = OpCodes.Nop;
                                        branchInstr.Operand = null;
                                    }
                                });
                                modified = true;
                                
                                // Пропускаем обработанные инструкции в цикле анализа, чтобы не сработать на них повторно неправильно
                                i += 3; 
                            }
                        }
                    }
                }
            }

            // Применяем все изменения
            foreach (var patch in patches)
            {
                patch();
            }

            if (modified)
            {
                method.Body.UpdateInstructionOffsets();
            }

            return modified;
        }

        /// <summary>
        /// Удаляет недостижимые блоки кода.
        /// Алгоритм:
        /// 1. Помечаем все инструкции как недостижимые.
        /// 2. Начинаем с первой инструкции, помечаем как достижимую.
        /// 3. Рекурсивно проходим по потоку управления (next, branch targets).
        /// 4. Все недостижимые инструкции заменяем на nop или удаляем.
        /// </summary>
        private bool RemoveUnreachableBlocks(MethodDef method)
        {
            var body = method.Body;
            var instructions = body.Instructions;
            if (instructions.Count == 0) return false;

            var reachable = new HashSet<Instruction>();
            var queue = new Queue<Instruction>();

            // Точка входа
            if (instructions.Count > 0)
            {
                reachable.Add(instructions[0]);
                queue.Enqueue(instructions[0]);
            }

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                int idx = instructions.IndexOf(current);
                if (idx == -1) continue;

                var instr = instructions[idx];

                // Определяем следующую инструкцию по порядку
                Instruction nextInstr = (idx + 1 < instructions.Count) ? instructions[idx + 1] : null;

                // Если это безусловный переход, следующая по порядку не выполняется (если только это не fall-through, но br всегда прыгает)
                if (instr.OpCode.Code == Code.Br || instr.OpCode.Code == Code.Br_S)
                {
                    nextInstr = null; 
                }
                // Если это ret или throw, поток обрывается
                else if (instr.OpCode.Code == Code.Ret || instr.OpCode.Code == Code.Throw)
                {
                    nextInstr = null;
                }

                // Добавляем цель перехода
                if (instr.Operand is Instruction target)
                {
                    if (reachable.Add(target))
                        queue.Enqueue(target);
                }
                else if (instr.Operand is Instruction[] targets)
                {
                    foreach (var t in targets)
                    {
                        if (reachable.Add(t))
                            queue.Enqueue(t);
                    }
                }

                // Добавляем следующую инструкцию по порядку
                if (nextInstr != null && reachable.Add(nextInstr))
                {
                    queue.Enqueue(nextInstr);
                }
            }

            // Очищаем недостижимое
            bool changed = false;
            // Идем с конца, чтобы безопасно удалять или заменять
            for (int i = instructions.Count - 1; i >= 0; i--)
            {
                if (!reachable.Contains(instructions[i]))
                {
                    // Заменяем на nop, чтобы не ломать индексы других переходов, которые могли бы сюда вести (хотя мы их уже прошли)
                    // Но безопаснее просто заменить на nop, а потом отдельным проходом убрать лишние nop
                    instructions[i].OpCode = OpCodes.Nop;
                    instructions[i].Operand = null;
                    changed = true;
                }
            }

            if (changed)
            {
                body.UpdateInstructionOffsets();
            }

            return changed;
        }

        #region Helpers

        private bool IsConstant(Instruction instr)
        {
            if (instr == null) return false;
            var code = instr.OpCode.Code;
            return code == Code.Ldc_I4 || code == Code.Ldc_I4_0 || code == Code.Ldc_I4_1 || code == Code.Ldc_I4_2 ||
                   code == Code.Ldc_I4_3 || code == Code.Ldc_I4_4 || code == Code.Ldc_I4_5 || code == Code.Ldc_I4_6 ||
                   code == Code.Ldc_I4_7 || code == Code.Ldc_I4_8 || code == Code.Ldc_I4_M1 ||
                   code == Code.Ldc_I8 || code == Code.Ldc_R4 || code == Code.Ldc_R8 || code == Code.Ldnull || code == Code.Ldstr;
        }

        private bool IsStloc(Instruction i, out int idx)
        {
            idx = -1;
            if (i == null) return false;
            if (i.Operand is Local l) idx = l.Index;
            else if (i.OpCode.Code == Code.Stloc_0) idx = 0;
            else if (i.OpCode.Code == Code.Stloc_1) idx = 1;
            else if (i.OpCode.Code == Code.Stloc_2) idx = 2;
            else if (i.OpCode.Code == Code.Stloc_3) idx = 3;
            return idx != -1 && (i.OpCode.Code == Code.Stloc || i.OpCode.Code == Code.Stloc_S ||
                   i.OpCode.Code >= Code.Stloc_0 && i.OpCode.Code <= Code.Stloc_3);
        }

        private int GetLocalIndex(Instruction i)
        {
            if (i == null) return -1;
            if (IsLdloc(i, out int idx) || IsStloc(i, out idx)) return idx;
            return -1;
        }

        private bool IsLdloc(Instruction i, out int idx)
        {
            idx = -1;
            if (i == null) return false;
            if (i.Operand is Local l) idx = l.Index;
            else if (i.OpCode.Code == Code.Ldloc_0) idx = 0;
            else if (i.OpCode.Code == Code.Ldloc_1) idx = 1;
            else if (i.OpCode.Code == Code.Ldloc_2) idx = 2;
            else if (i.OpCode.Code == Code.Ldloc_3) idx = 3;
            return idx != -1 && (i.OpCode.Code == Code.Ldloc || i.OpCode.Code == Code.Ldloc_S ||
                   i.OpCode.Code >= Code.Ldloc_0 && i.OpCode.Code <= Code.Ldloc_3);
        }

        private object? GetConstantValue(Instruction i)
        {
            if (i == null) return null;
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
                // Приводим к double для универсального сравнения чисел
                double da = Convert.ToDouble(a);
                double db = Convert.ToDouble(b);
                switch (op)
                {
                    case Code.Ceq: return da == db;
                    case Code.Cgt: 
                    case Code.Cgt_Un: return da > db;
                    case Code.Clt: 
                    case Code.Clt_Un: return da < db;
                }
            } 
            catch { }
            return null;
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
