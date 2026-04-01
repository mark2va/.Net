using System;
using System.Collections.Generic;
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

        public UniversalDeobfuscator(string filePath, AiConfig aiConfig)
        {
            _module = ModuleDefMD.Load(filePath);
            _aiAssistant = aiConfig.Enabled ? new AiAssistant(aiConfig) : null;
        }

        public void Deobfuscate()
        {
            Console.WriteLine("[*] Starting deep deobfuscation...");
            int count = 0;
            
            foreach (var type in _module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody || !method.Body.IsIL) continue;

                    try
                    {
                        // Попытка распутать state machine
                        if (EmulateAndUnravel(method))
                        {
                            // Если удалось распутать, делаем дополнительную очистку от мертвого кода
                            CleanupDeadCode(method);
                            count++;
                        }
                        else
                        {
                            // Если не получилось распутать полностью, пробуем упростить поток
                            SimplifyControlFlow(method);
                        }

                        // Финальная очистка NOP
                        CleanupNops(method);

                        if (_aiAssistant != null && IsObfuscatedName(method.Name))
                        {
                            RenameWithAi(method);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[!] Error in {method.FullName}: {ex.Message}");
                    }
                }
            }
            Console.WriteLine($"[*] Processed {count} methods.");
        }

        /// <summary>
        /// Эмулирует выполнение метода для определения реального потока управления
        /// и удаляет обфускацию на основе переменной состояния.
        /// </summary>
        private bool EmulateAndUnravel(MethodDef method)
        {
            var body = method.Body;
            if (body == null || !body.HasInstructions) return false;

            var instructions = body.Instructions;
            if (instructions.Count < 3) return false;

            // 1. Поиск переменной состояния (обычно первая инструкция - ldc, вторая - stloc)
            int stateVarIndex = -1;
            object? initialState = null;

            for (int i = 0; i < Math.Min(5, instructions.Count - 1); i++)
            {
                var ldc = instructions[i];
                var stloc = instructions[i + 1];

                if (IsStloc(stloc, out int idx))
                {
                    var val = GetConstantValue(ldc);
                    if (val != null)
                    {
                        stateVarIndex = idx;
                        initialState = val;
                        break;
                    }
                }
            }

            if (stateVarIndex == -1 || initialState == null)
            {
                // Если нет явной переменной состояния, пробуем упростить поток без эмуляции
                return false;
            }

            // 2. Эмуляция выполнения для сбора реального пути
            var realPath = new List<Instruction>();
            var visited = new HashSet<int>(); // Индексы инструкций в оригинальном списке
            var stack = new Stack<object?>();
            var locals = new object?[method.Body.Variables.Count];
            
            // Инициализация переменной состояния
            locals[stateVarIndex] = initialState;

            int ip = 0; // Instruction Pointer (индекс в списке instructions)
            int maxIterations = instructions.Count * 100; // Защита от бесконечных циклов
            int iterations = 0;

            while (ip >= 0 && ip < instructions.Count && iterations < maxIterations)
            {
                iterations++;
                var instr = instructions[ip];

                // Если мы уже были в этой точке с тем же состоянием стека/локалей (упрощенно - просто по IP для линейного кода)
                // В сложной обфускации иногда нужны более хитрые проверки, но для начала хватит IP
                if (visited.Contains(ip) && instr.OpCode.Code != Code.Nop)
                {
                     // Если зациклились на том же месте и это не конец цикла, а обфускация - выходим
                     // Но если это реальный цикл (например, for), нам нужно его сохранить.
                     // Для state machine обфускации повторный вход в тот же блок состояния обычно означает конец или ошибку.
                     // Здесь мы предполагаем, что state machine разворачивается линейно.
                     if (instr.OpCode.FlowControl == FlowControl.Branch || instr.OpCode.FlowControl == FlowControl.Cond_Branch)
                     {
                         break; 
                     }
                }
                
                // Пропускаем инструкции работы с переменной состояния, если они чисто обфусцирующие
                bool isStateOp = IsStateVariableOperation(instr, stateVarIndex);
                
                if (!isStateOp)
                {
                    // Клонируем инструкцию, если она не является частью обфускации
                    // Но нужно быть осторожным: если это условие, которое всегда true/false, его надо заменить на br/nop
                    realPath.Add(CloneInstruction(instr));
                }

                // Эмуляция логики перехода
                if (instr.OpCode.Code == Code.Nop)
                {
                    ip++;
                    continue;
                }

                if (instr.OpCode.FlowControl == FlowControl.Branch)
                {
                    if (instr.Operand is Instruction target)
                    {
                        ip = instructions.IndexOf(target);
                        continue;
                    }
                }

                if (instr.OpCode.FlowControl == FlowControl.Cond_Branch)
                {
                    // Пытаемся вычислить условие
                    bool? conditionResult = EvaluateConditionAt(method, ip, locals, stack, stateVarIndex);
                    
                    if (conditionResult.HasValue)
                    {
                        if (instr.Operand is Instruction target)
                        {
                            if (conditionResult.Value)
                            {
                                ip = instructions.IndexOf(target);
                            }
                            else
                            {
                                ip++;
                            }
                        }
                        continue;
                    }
                    else
                    {
                        // Не смогли вычислить, значит это реальное условие, оставляем как есть (но оно уже добавлено в realPath выше)
                        // В state machine обфускации условия обычно зависят от state var, которую мы знаем.
                        // Если не вычислилось, возможно, это защита или сложная логика.
                        ip++; 
                    }
                    continue;
                }

                if (instr.OpCode.FlowControl == FlowControl.Return || 
                    instr.OpCode.FlowControl == FlowControl.Throw)
                {
                    break;
                }

                // Эмуляция простых операций со стеком и локалями для поддержки вычисления условий
                SimulateInstruction(instr, locals, stack, stateVarIndex);

                ip++;
            }

            if (realPath.Count == 0) return false;

            // Заменяем тело метода на раскрученный путь
            ReplaceMethodBody(method, realPath);
            return true;
        }

        /// <summary>
        /// Проверяет, является ли инструкция операцией над переменной состояния (обфускация).
        /// </summary>
        private bool IsStateVariableOperation(Instruction instr, int stateVarIndex)
        {
            // Ldloc stateVar
            if (instr.OpCode.Code == Code.Ldloc || instr.OpCode.Code == Code.Ldloc_S ||
                (instr.OpCode.Code >= Code.Ldloc_0 && instr.OpCode.Code <= Code.Ldloc_3))
            {
                if (GetLocalIndex(instr) == stateVarIndex) return true;
            }

            // Stloc stateVar
            if (instr.OpCode.Code == Code.Stloc || instr.OpCode.Code == Code.Stloc_S ||
                (instr.OpCode.Code >= Code.Stloc_0 && instr.OpCode.Code <= Code.Stloc_3))
            {
                if (GetLocalIndex(instr) == stateVarIndex) return true;
            }

            // Ldc (часто используется перед стором в state var, но сам по себе не мусор, если не часть паттерна)
            // Мы помечаем как мусор только если это часть последовательности обновления состояния.
            // Для простоты пока считаем мусором только явные чтения/записи в state var внутри цикла обфускации.
            
            return false;
        }

        /// <summary>
        /// Симулирует выполнение инструкции для обновления стека и локалей (упрощенно).
        /// </summary>
        private void SimulateInstruction(Instruction instr, object?[] locals, Stack<object?> stack, int stateVarIndex)
        {
            try
            {
                switch (instr.OpCode.Code)
                {
                    case Code.Ldc_I4:
                    case Code.Ldc_I4_0: case Code.Ldc_I4_1: case Code.Ldc_I4_2: case Code.Ldc_I4_3:
                    case Code.Ldc_I4_4: case Code.Ldc_I4_5: case Code.Ldc_I4_6: case Code.Ldc_I4_7:
                    case Code.Ldc_I4_8: case Code.Ldc_I4_M1:
                    case Code.Ldc_I8:
                    case Code.Ldc_R4:
                    case Code.Ldc_R8:
                        stack.Push(GetConstantValue(instr));
                        break;

                    case Code.Ldloc:
                    case Code.Ldloc_S:
                    case Code.Ldloc_0: case Code.Ldloc_1: case Code.Ldloc_2: case Code.Ldloc_3:
                        int idx = GetLocalIndex(instr);
                        if (idx >= 0 && idx < locals.Length)
                            stack.Push(locals[idx]);
                        break;

                    case Code.Stloc:
                    case Code.Stloc_S:
                    case Code.Stloc_0: case Code.Stloc_1: case Code.Stloc_2: case Code.Stloc_3:
                        int sIdx = GetLocalIndex(instr);
                        if (sIdx >= 0 && sIdx < locals.Length && stack.Count > 0)
                            locals[sIdx] = stack.Pop();
                        break;
                    
                    case Code.Add:
                        if (stack.Count >= 2)
                        {
                            var v2 = stack.Pop();
                            var v1 = stack.Pop();
                            stack.Push(AddValues(v1, v2));
                        }
                        break;
                    
                    case Code.Sub:
                        if (stack.Count >= 2)
                        {
                            var v2 = stack.Pop();
                            var v1 = stack.Pop();
                            stack.Push(SubValues(v1, v2));
                        }
                        break;

                    // Можно добавить другие арифметические операции при необходимости
                }
            }
            catch
            {
                // Ошибка эмуляции, игнорируем
            }
        }

        private object? AddValues(object? a, object? b)
        {
            if (a is double da && b is double db) return da + db;
            if (a is long la && b is long lb) return la + lb;
            if (a is int ia && b is int ib) return ia + ib;
            return null;
        }

        private object? SubValues(object? a, object? b)
        {
            if (a is double da && b is double db) return da - db;
            if (a is long la && b is long lb) return la - lb;
            if (a is int ia && b is int ib) return ia - ib;
            return null;
        }

        /// <summary>
        /// Вычисляет результат условного перехода в данной точке.
        /// Возвращает null, если вычислить невозможно (реальное условие).
        /// </summary>
        private bool? EvaluateConditionAt(MethodDef method, int ip, object?[] locals, Stack<object?> stack, int stateVarIndex)
        {
            var instructions = method.Body.Instructions;
            var instr = instructions[ip];

            // Нам нужно посмотреть назад, чтобы восстановить стек или найти сравнение с константой
            // Паттерн: ldloc(state), ldc(value), ceq/cgt/clt, brtrue/brfalse
            
            // Упрощенный подход: ищем сравнение переменной состояния с константой
            // Обычно перед branch идет ceq/cgt/clt, а перед ним ldloc и ldc
            
            if (ip < 2) return null;

            var prev1 = instructions[ip - 1]; // usually ceq/cgt/clt or direct value check
            var prev2 = instructions[ip - 2]; // usually ldc or ldloc

            // Обработка паттерна: ldloc(state), ldc(const), cXX, br...
            if ((prev1.OpCode.Code == Code.Ceq || prev1.OpCode.Code == Code.Cgt || prev1.OpCode.Code == Code.Clt ||
                 prev1.OpCode.Code == Code.Cgt_Un || prev1.OpCode.Code == Code.Clt_Un))
            {
                if (ip < 3) return null;
                var prev3 = instructions[ip - 3]; // ldc
                var prev4 = (ip >= 4) ? instructions[ip - 4] : null; // ldloc

                if (prev4 != null && IsLdloc(prev4, out int loadIdx) && loadIdx == stateVarIndex)
                {
                    var constVal = GetConstantValue(prev3);
                    var stateVal = locals[stateVarIndex];

                    if (constVal != null && stateVal != null)
                    {
                        return CompareValues(stateVal, constVal, prev1.OpCode.Code);
                    }
                }
            }
            
            // Обработка паттерна: ldloc(state), brtrue/brfalse (проверка на ноль)
            if (instr.OpCode.Code == Code.Brtrue || instr.OpCode.Code == Code.Brtrue_S ||
                instr.OpCode.Code == Code.Brfalse || instr.OpCode.Code == Code.Brfalse_S)
            {
                 if (IsLdloc(prev2, out int lIdx) && lIdx == stateVarIndex)
                 {
                     var val = locals[stateVarIndex];
                     if (val != null)
                     {
                         bool isZero = Convert.ToDouble(val) == 0;
                         if (instr.OpCode.Code == Code.Brtrue || instr.OpCode.Code == Code.Brtrue_S)
                             return !isZero;
                         else
                             return isZero;
                     }
                 }
            }

            return null;
        }

        private bool? CompareValues(object? actual, object? expected, Code opCode)
        {
            if (actual == null || expected == null) return null;

            try 
            {
                double dActual = Convert.ToDouble(actual);
                double dExpected = Convert.ToDouble(expected);

                switch (opCode)
                {
                    case Code.Ceq: return dActual == dExpected;
                    case Code.Cgt: return dActual > dExpected;
                    case Code.Cgt_Un: return dActual > dExpected;
                    case Code.Clt: return dActual < dExpected;
                    case Code.Clt_Un: return dActual < dExpected;
                }
            }
            catch { }
            
            return null;
        }

        /// <summary>
        /// Упрощает поток управления, заменяя условия, которые можно вычислить статически.
        /// Работает даже если полная эмуляция не удалась.
        /// </summary>
        private void SimplifyControlFlow(MethodDef method)
        {
            var body = method.Body;
            var instructions = body.Instructions;
            bool changed = true;
            int maxPasses = 20;
            int pass = 0;

            while (changed && pass < maxPasses)
            {
                changed = false;
                pass++;

                // Отслеживание известных значений локалей в текущем блоке
                var knownValues = new Dictionary<int, object?>();

                for (int i = 0; i < instructions.Count; i++)
                {
                    var instr = instructions[i];

                    // Обновление известных значений
                    if (i + 1 < instructions.Count)
                    {
                        var next = instructions[i + 1];
                        if (IsStloc(next, out int localIdx))
                        {
                            var val = GetConstantValue(instr);
                            if (val != null)
                            {
                                knownValues[localIdx] = val;
                            }
                            else if (instr.OpCode.Code == Code.Ldloc || instr.OpCode.Code == Code.Ldloc_S || 
                                     (instr.OpCode.Code >= Code.Ldloc_0 && instr.OpCode.Code <= Code.Ldloc_3))
                            {
                                // Копирование значения из другой локали
                                if (GetLocalIndex(instr, out int srcIdx) && knownValues.ContainsKey(srcIdx))
                                {
                                    knownValues[localIdx] = knownValues[srcIdx];
                                }
                            }
                        }
                    }

                    // Анализ условий
                    if (instr.OpCode.FlowControl == FlowControl.Cond_Branch)
                    {
                        // Попытка вычислить условие, глядя назад
                        bool? result = TryEvaluateSimpleCondition(instructions, i, knownValues);
                        
                        if (result.HasValue)
                        {
                            if (instr.Operand is Instruction target)
                            {
                                if (result.Value)
                                {
                                    instr.OpCode = OpCodes.Br;
                                    instr.Operand = target;
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

                if (changed)
                {
                    body.UpdateInstructionOffsets();
                }
            }

            RemoveUnreachableBlocks(method);
        }

        private bool? TryEvaluateSimpleCondition(IList<Instruction> instructions, int branchIndex, Dictionary<int, object?> knownValues)
        {
            if (branchIndex < 2) return null;

            var branchInstr = instructions[branchIndex];
            var prev1 = instructions[branchIndex - 1];
            var prev2 = instructions[branchIndex - 2];

            // Паттерн: ldloc, ldc, cXX, br...
            if ((prev1.OpCode.Code == Code.Ceq || prev1.OpCode.Code == Code.Cgt || prev1.OpCode.Code == Code.Clt ||
                 prev1.OpCode.Code == Code.Cgt_Un || prev1.OpCode.Code == Code.Clt_Un))
            {
                if (branchIndex < 3) return null;
                var ldc = instructions[branchIndex - 3];
                var ldloc = (branchIndex >= 4) ? instructions[branchIndex - 4] : null;

                if (ldloc != null && IsLdloc(ldloc, out int localIdx))
                {
                    object? val1 = null;
                    object? val2 = GetConstantValue(ldc);

                    if (knownValues.ContainsKey(localIdx))
                        val1 = knownValues[localIdx];
                    
                    if (val1 != null && val2 != null)
                    {
                        return CompareValues(val1, val2, prev1.OpCode.Code);
                    }
                }
            }

            // Паттерн: ldloc, brtrue/brfalse
            if (branchInstr.OpCode.Code == Code.Brtrue || branchInstr.OpCode.Code == Code.Brtrue_S ||
                branchInstr.OpCode.Code == Code.Brfalse || branchInstr.OpCode.Code == Code.Brfalse_S)
            {
                if (IsLdloc(prev2, out int localIdx) && knownValues.ContainsKey(localIdx))
                {
                    var val = knownValues[localIdx];
                    if (val != null)
                    {
                        bool isZero = Convert.ToDouble(val) == 0;
                        if (branchInstr.OpCode.Code == Code.Brtrue || branchInstr.OpCode.Code == Code.Brtrue_S)
                            return !isZero;
                        else
                            return isZero;
                    }
                }
            }

            return null;
        }

        private void RemoveUnreachableBlocks(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            if (instructions.Count == 0) return;

            var reachable = new HashSet<Instruction>();
            var queue = new Queue<Instruction>();

            queue.Enqueue(instructions[0]);
            reachable.Add(instructions[0]);

            while (queue.Count > 0)
            {
                var curr = queue.Dequeue();
                int idx = instructions.IndexOf(curr);
                if (idx == -1) continue;

                if (curr.OpCode.Code != Code.Br && curr.OpCode.Code != Code.Ret && curr.OpCode.Code != Code.Throw)
                {
                    if (idx + 1 < instructions.Count)
                    {
                        var next = instructions[idx + 1];
                        if (reachable.Add(next))
                            queue.Enqueue(next);
                    }
                }

                if (curr.Operand is Instruction target)
                {
                    if (reachable.Add(target))
                        queue.Enqueue(target);
                }
                else if (curr.Operand is Instruction[] targets)
                {
                    foreach (var t in targets)
                        if (reachable.Add(t)) queue.Enqueue(t);
                }
            }

            bool changed = false;
            for (int i = instructions.Count - 1; i >= 0; i--)
            {
                if (!reachable.Contains(instructions[i]))
                {
                    instructions[i].OpCode = OpCodes.Nop;
                    instructions[i].Operand = null;
                    changed = true;
                }
            }

            if (changed)
            {
                method.Body.UpdateInstructionOffsets();
            }
        }

        /// <summary>
        /// Удаляет переменные состояния и связанный с ними мусор после распутывания.
        /// </summary>
        private void CleanupDeadCode(MethodDef method)
        {
            var body = method.Body;
            var instructions = body.Instructions;
            
            // Находим все локальные переменные, которые используются только для хранения констант и сравнения
            // Это эвристика: если переменная только читается и сравнивается с константами, и результат сравнения всегда известен (потому что мы уже распутали),
            // то такие инструкции можно удалить.
            
            // В данном случае, так как мы уже заменили тело метода на linear path в EmulateAndUnravel,
            // там не должно остаться инструкций загрузки/сохранения state var, если мы их правильно отфильтровали.
            // Но могут остаться "хвосты" от старых переходов, если эмуляция была частичной.
            
            // Дополнительная очистка: удаление последовательностей ldloc, ldc, cXX, pop (если результат сравнения не используется)
            // Но в нашем случае результат сравнения использовался для ветвления, которое мы уже разрешили.
            // Значит, сами инструкции сравнения стали лишними, если перед ними стоит безусловный переход или они ведут в никуда.
            
            // Самый простой способ: запустить RemoveUnreachableBlocks еще раз, чтобы убрать остатки.
            RemoveUnreachableBlocks(method);
        }

        private void CleanupNops(MethodDef method)
        {
            var body = method.Body;
            var instructions = body.Instructions;
            bool changed = true;

            while (changed)
            {
                changed = false;
                for (int i = instructions.Count - 1; i >= 0; i--)
                {
                    if (instructions[i].OpCode.Code == Code.Nop)
                    {
                        bool isTarget = false;
                        foreach (var instr in instructions)
                        {
                            if (instr.Operand == instructions[i]) { isTarget = true; break; }
                            if (instr.Operand is Instruction[] arr && arr.Contains(instructions[i])) { isTarget = true; break; }
                        }

                        if (!isTarget)
                        {
                            instructions.RemoveAt(i);
                            changed = true;
                        }
                    }
                }
            }
            
            body.UpdateInstructionOffsets();
            body.SimplifyMacros(method.Parameters);
        }

        #region Helpers

        private bool IsStloc(Instruction instr, out int index)
        {
            index = -1;
            if (instr.Operand is Local l) index = l.Index;
            else if (instr.OpCode.Code == Code.Stloc_0) index = 0;
            else if (instr.OpCode.Code == Code.Stloc_1) index = 1;
            else if (instr.OpCode.Code == Code.Stloc_2) index = 2;
            else if (instr.OpCode.Code == Code.Stloc_3) index = 3;

            return index != -1 && (instr.OpCode.Code == Code.Stloc || instr.OpCode.Code == Code.Stloc_S ||
                   instr.OpCode.Code == Code.Stloc_0 || instr.OpCode.Code == Code.Stloc_1 ||
                   instr.OpCode.Code == Code.Stloc_2 || instr.OpCode.Code == Code.Stloc_3);
        }

        private bool IsLdloc(Instruction instr, out int index)
        {
            index = -1;
            if (instr.Operand is Local l) index = l.Index;
            else if (instr.OpCode.Code == Code.Ldloc_0) index = 0;
            else if (instr.OpCode.Code == Code.Ldloc_1) index = 1;
            else if (instr.OpCode.Code == Code.Ldloc_2) index = 2;
            else if (instr.OpCode.Code == Code.Ldloc_3) index = 3;

            return index != -1 && (instr.OpCode.Code == Code.Ldloc || instr.OpCode.Code == Code.Ldloc_S ||
                   instr.OpCode.Code == Code.Ldloc_0 || instr.OpCode.Code == Code.Ldloc_1 ||
                   instr.OpCode.Code == Code.Ldloc_2 || instr.OpCode.Code == Code.Ldloc_3);
        }

        private bool GetLocalIndex(Instruction instr, out int index)
        {
            return IsLdloc(instr, out index) || IsStloc(instr, out index);
        }
        
        private int GetLocalIndex(Instruction instr)
        {
            if (IsLdloc(instr, out int idx) || IsStloc(instr, out idx)) return idx;
            return -1;
        }

        private object? GetConstantValue(Instruction instr)
        {
            switch (instr.OpCode.Code)
            {
                case Code.Ldc_I4: return instr.Operand as int?;
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
                case Code.Ldc_I8: return instr.Operand as long?;
                case Code.Ldc_R4: return instr.Operand as float?;
                case Code.Ldc_R8: return instr.Operand as double?;
                default: return null;
            }
        }

        private bool IsObfuscatedName(string name) => !string.IsNullOrEmpty(name) && (name.Length == 1 || name.StartsWith("?"));

        private void RenameWithAi(MethodDef method)
        {
            if (_aiAssistant == null) return;
            try
            {
                var snippet = string.Join("\n", method.Body.Instructions.Take(15).Select(x => x.ToString()));
                var newName = _aiAssistant.GetSuggestedName(method.Name, snippet, method.ReturnType?.ToString());
                if (!string.IsNullOrEmpty(newName) && IsValidIdentifier(newName))
                {
                    method.Name = newName;
                    Console.WriteLine($"[AI] Renamed {method.Name}");
                }
            }
            catch { }
        }

        private bool IsValidIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (!char.IsLetter(name[0]) && name[0] != '_') return false;
            foreach (var c in name) if (!char.IsLetterOrDigit(c) && c != '_') return false;
            return true;
        }

        private Instruction CloneInstruction(Instruction orig)
        {
            var opCode = orig.OpCode;
            var operand = orig.Operand;

            if (operand is Local local)
                return Instruction.Create(opCode, local);
            
            if (operand is Parameter param)
                return Instruction.Create(opCode, param);
            
            if (operand is Instruction target)
                return Instruction.Create(opCode, target);
            
            if (operand is Instruction[] targets)
                return Instruction.Create(opCode, targets);
            
            if (operand is string str)
                return Instruction.Create(opCode, str);
            
            if (operand is int i)
                return Instruction.Create(opCode, i);
            
            if (operand is long l)
                return Instruction.Create(opCode, l);
            
            if (operand is float f)
                return Instruction.Create(opCode, f);
            
            if (operand is double d)
                return Instruction.Create(opCode, d);
            
            if (operand is ITypeDefOrRef type)
                return Instruction.Create(opCode, type);
            
            if (operand is MethodDef method)
                return Instruction.Create(opCode, method);
            
            if (operand is FieldDef field)
                return Instruction.Create(opCode, field);
            
            if (operand is MemberRef memberRef)
                return Instruction.Create(opCode, memberRef);
            
            return new Instruction(opCode, operand);
        }

        private void ReplaceMethodBody(MethodDef method, List<Instruction> newInstructions)
        {
            var body = method.Body;
            body.Instructions.Clear();
            foreach (var instr in newInstructions)
            {
                body.Instructions.Add(instr);
            }
            body.UpdateInstructionOffsets();
            body.SimplifyMacros(method.Parameters);
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
        }

        public void Dispose()
        {
            _aiAssistant?.Dispose();
            _module.Dispose();
        }
    }
}
