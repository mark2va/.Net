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

        public UniversalDeobfuscator(string filePath)
        {
            _module = ModuleDefMD.Load(filePath);
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
                        // 1. Эмуляция и распутывание потока
                        if (EmulateAndUnravel(method))
                        {
                            // 2. Удаление мусора (переменных состояния, сравнений, NOP)
                            RemoveStateVariableGarbage(method);
                            
                            // 3. Финальная очистка
                            CleanupMethod(method);
                            count++;
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
/*
        /// <summary>
        /// Эмулирует выполнение метода, чтобы построить линейный поток инструкций,
        /// игнорируя обфусцированные переходы по состоянию.
        /// </summary>
        private void EmulateAndUnravel(MethodDef method)
        {
            var body = method.Body;
            if (body == null || !body.HasInstructions) return;
            // 1. Находим переменную состояния
            var stateVarInfo = FindStateVariable(method);
            if (stateVarInfo == null) return; // Не похоже на этот тип обфускации
            int stateVarIndex = stateVarInfo.Value.Index;
            
            // 2. Эмулируем поток, собирая только нужные инструкции
            var linearInstructions = new List<Instruction>();
            var instructionMap = new Dictionary<int, Instruction>(); // Оригинал -> Клон
            
            // Создаем клоны всех инструкций заранее, чтобы перемапить ветвления
            foreach (var instr in body.Instructions)
            {
                instructionMap[instr.Offset] = CloneInstruction(instr);
            }
            // Эмуляция
            var ipStack = new Stack<int>();
            var visitedOffsets = new HashSet<int>();
            
            // Точка входа
            ipStack.Push(0);
            while (ipStack.Count > 0)
            {
                int currentOffset = ipStack.Pop();
                if (!instructionMap.ContainsKey(currentOffset)) continue;
                if (visitedOffsets.Contains(currentOffset)) continue;
                
                visitedOffsets.Add(currentOffset);
                
                // Получаем оригинальную инструкцию по оффсету (примерно)
                // Так как оффсеты могут быть неточными после клонирования, ищем по индексу или совпадению
                var originalInstr = body.Instructions.FirstOrDefault(x => x.Offset == currentOffset);
                if (originalInstr == null) continue;
                var clonedInstr = instructionMap[currentOffset];
                
                // Пропускаем инструкции, связанные с состоянием (запись в state var)
                if (IsStloc(originalInstr, out int setIdx) && setIdx == stateVarIndex)
                {
                    // Не добавляем в результат, но продолжаем выполнение
                    // Обновляем наше "виртуальное" состояние, если нужно для логики, 
                    // но в данном случае мы просто игнорируем изменение флага
                    goto NextInstruction;
                }
                // Пропускаем загрузку константы, если следующая инструкция - запись в state var
                if (IsConstantLoad(originalInstr))
                {
                    var nextInstr = GetNextInstruction(body, originalInstr);
                    if (nextInstr != null && IsStloc(nextInstr, out int nextIdx) && nextIdx == stateVarIndex)
                    {
                        goto NextInstruction;
                    }
                }
                // Обработка ветвлений
                if (clonedInstr.OpCode.FlowControl == FlowControl.Cond_Branch)
                {
                    // Пытаемся определить, куда идти, эмулируя условие
                    // В простейшем случае, если это сравнение с состоянием, мы уже решили путь выше.
                    // Здесь мы просто берем первый путь (true) или второй, в зависимости от контекста.
                    // Но так как мы идем линейно, нам нужно решить, какое ветвление правильное.
                    
                    // Хак: если это obfuscated flow, обычно одно ветвление ведет на "мусор" (другие кейсы),
                    // а другое на продолжение. Мы уже отфильтровали мусор через visitedOffsets? Нет.
                    
                    // Правильный подход для этого типа обфускации:
                    // Мы НЕ должны добавлять само условное ветвление в чистый код, если оно зависит от state var.
                    // Вместо этого мы должны подставить безусловный переход (Br) в нужный блок или убрать его.
                    
                    // Проверяем, зависит ли условие от state var
                    bool dependsOnState = CheckDependsOnState(body, originalInstr, stateVarIndex);
                    
                    if (dependsOnState)
                    {
                        // Решаем, куда идти, основываясь на логике обфускатора (обычно fall-through или конкретный target)
                        // В данном примере мы просто идём по пути, который ведет к новым инструкциям, 
                        // которые ещё не посещены и не являются "ловушками".
                        // Для простоты: берем target, если он ведет вперед, иначе следующий.
                        
                        var target = clonedInstr.Operand as Instruction;
                        if (target != null)
                        {
                            // Маппинг таргета
                            var targetOffset = GetOriginalOffset(body, target);
                            if (targetOffset.HasValue && !visitedOffsets.Contains(targetOffset.Value))
                            {
                                // Заменяем условный переход на безусловный, если это единственный путь
                                clonedInstr.OpCode = OpCodes.Br;
                                // Нужно найти клон таргета
                                var clonedTarget = FindClonedInstruction(linearInstructions, instructionMap, target);
                                if (clonedTarget != null) clonedInstr.Operand = clonedTarget;
                                else ipStack.Push(targetOffset.Value);
                            }
                            else
                            {
                                // Переход в уже посещенное или назад - убираем переход (continue)
                                clonedInstr.OpCode = OpCodes.Nop;
                                clonedInstr.Operand = null;
                            }
                        }
                    }
                    else
                    {
                        // Обычное ветвление (не обфускация состояния) - оставляем как есть и планируем оба пути
                        var target = clonedInstr.Operand as Instruction;
                        if (target != null)
                        {
                             var targetOffset = GetOriginalOffset(body, target);
                             if (targetOffset.HasValue) ipStack.Push(targetOffset.Value);
                        }
                        
                        var nextOrig = GetNextInstruction(body, originalInstr);
                        if (nextOrig != null)
                        {
                            ipStack.Push(nextOrig.Offset);
                        }
                    }
                }
                else if (clonedInstr.OpCode.Code == Code.Br || clonedInstr.OpCode.Code == Code.Leave || clonedInstr.OpCode.Code == Code.Leave_S)
                {
                    var target = clonedInstr.Operand as Instruction;
                    if (target != null)
                    {
                        var targetOffset = GetOriginalOffset(body, target);
                        if (targetOffset.HasValue) ipStack.Push(targetOffset.Value);
                    }
                }
                else if (clonedInstr.OpCode.Code == Code.Ret || clonedInstr.OpCode.Code == Code.Throw)
                {
                    // Конец пути
                }
                else
                {
                    // Обычная инструкция: планируем следующую
                    var nextOrig = GetNextInstruction(body, originalInstr);
                    if (nextOrig != null)
                    {
                        ipStack.Push(nextOrig.Offset);
                    }
                }
                linearInstructions.Add(clonedInstr);
                NextInstruction:;
            }
            if (linearInstructions.Count > 0)
            {
                ReplaceMethodBody(method, linearInstructions);
            }
            return false;
        }
*/
/// <summary>
        /// Эмулирует выполнение метода, вычисляя значения условий на лету
        /// и убирая весь мусор обфускации.
        /// </summary>
        private void EmulateAndUnravel(MethodDef method)
        {
            var body = method.Body;
            if (body == null) return;
            // 1. Находим переменную состояния
            var stateVarInfo = FindStateVariable(method);
            if (stateVarInfo == null) return;
            int stateVarIndex = stateVarInfo.Value.Index;
            object? currentState = stateVarInfo.Value.InitialValue;
            var instructions = body.Instructions;
            bool changed = true;
            int maxPasses = 50;
            int pass = 0;
            while (changed && pass < maxPasses)
            {
                changed = false;
                pass++;
                // Обновляем тело на каждой итерации, если были изменения
                if (pass > 1) instructions = body.Instructions;
                for (int i = 0; i < instructions.Count; i++)
                {
                    var instr = instructions[i];
                    // --- Шаг А: Обновление состояния (ldc + stloc) ---
                    if (IsStloc(instr, out int storeIdx) && storeIdx == stateVarIndex)
                    {
                        if (i > 0)
                        {
                            var prev = instructions[i - 1];
                            var val = GetConstantValue(prev);
                            if (val != null)
                            {
                                currentState = val;
                                // Помечаем эти две инструкции на удаление
                                prev.OpCode = OpCodes.Nop;
                                prev.Operand = null;
                                instr.OpCode = OpCodes.Nop;
                                instr.Operand = null;
                                changed = true;
                                continue;
                            }
                        }
                    }
                    // --- Шаг Б: Упрощение условий ---
                    // Если видим проверку переменной состояния: ldloc + ldc + ceq + br...
                    // Мы можем вычислить результат прямо сейчас.
                    if (instr.OpCode.FlowControl == FlowControl.Cond_Branch)
                    {
                        // Пытаемся найти паттерн сравнения перед этим ветвлением
                        bool? result = TryEvaluateCondition(instructions, i, stateVarIndex, currentState);
                        
                        if (result.HasValue)
                        {
                            if (result.Value)
                            {
                                // Условие истинно: заменяем на безусловный переход
                                instr.OpCode = OpCodes.Br;
                                changed = true;
                            }
                            else
                            {
                                // Условие ложно: удаляем переход (превращаем в nop)
                                instr.OpCode = OpCodes.Nop;
                                instr.Operand = null;
                                changed = true;
                            }
                        }
                    }
                }
                if (changed)
                {
                    body.UpdateInstructionOffsets();
                }
            }
            // Финальная очистка: убираем все NOP и пересчитываем оффсеты
            CleanupMethod(method);
            
            // Удаляем саму переменную состояния, если она больше не используется
            RemoveStateVariable(method, stateVarIndex);
        }
        /// <summary>
        /// Пытается вычислить результат условия, анализируя стек и переменные.
        /// </summary>
        private bool? TryEvaluateCondition(IList<Instruction> instructions, int branchIndex, int stateVarIndex, object? currentState)
        {
            // Идем назад от ветвления, чтобы найти сравнение
            // Ожидаемый паттерн: ... ldc.X, ldloc.s state, ceq, brtrue/brfalse ...
            // Или: ldloc, ldc, cgt, br...
            
            int idx = branchIndex - 1;
            Instruction? cmpInstr = null;
            Instruction? constInstr = null;
            // Ищем операцию сравнения (ceq, cgt, clt)
            while (idx >= 0)
            {
                var curr = instructions[idx];
                if (curr.OpCode.Code == Code.Nop) { idx--; continue; }
                if (curr.OpCode.Code == Code.Ceq || curr.OpCode.Code == Code.Cgt || curr.OpCode.Code == Code.Clt ||
                    curr.OpCode.Code == Code.Cgt_Un || curr.OpCode.Code == Code.Clt_Un)
                {
                    cmpInstr = curr;
                    break;
                }
                // Если встретили другое ветвление или возврат, останавливаемся
                if (curr.OpCode.FlowControl == FlowControl.Cond_Branch || 
                    curr.OpCode.Code == Code.Ret || curr.OpCode.Code == Code.Throw)
                    break;
                idx--;
            }
            if (cmpInstr == null) return null;
            // Теперь ищем константу и загрузку переменной перед сравнением
            // Стек перед cmp: [Value1, Value2]. Один из них - наша переменная состояния.
            int searchIdx = cmpInstr.Index - 1;
            bool foundStateLoad = false;
            object? foundConst = null;
            // Проходим пару инструкций назад
            int count = 0;
            while (searchIdx >= 0 && count < 6)
            {
                var curr = instructions[searchIdx];
                if (curr.OpCode.Code == Code.Nop) { searchIdx--; continue; }
                if (IsLdloc(curr, out int lIdx) && lIdx == stateVarIndex)
                {
                    foundStateLoad = true;
                }
                else if (!foundStateLoad) // Ищем константу, если еще не нашли загрузку состояния (она может быть ниже в стеке или выше)
                {
                    var val = GetConstantValue(curr);
                    if (val != null)
                    {
                        foundConst = val;
                    }
                }
                
                // Если нашли и то и другое
                if (foundStateLoad && foundConst != null) break;
                searchIdx--;
                count++;
            }
            // Если мы знаем currentState, и нашли константу для сравнения
            if (foundStateLoad && foundConst != null && currentState != null)
            {
                return CompareValues(currentState, foundConst, cmpInstr.OpCode.Code);
            }
            
            // Особый случай: проверка на истинность (brtrue) без явного сравнения с константой
            // Например, если в стеке просто результат предыдущего вычисления.
            // Но в данном типе обфускации почти всегда есть явное сравнение num == X.
            
            return null;
        }
        /// <summary>
        /// Удаляет переменную состояния и связанные с ней инструкции после распутывания.
        /// </summary>
        private void RemoveStateVariableGarbage(MethodDef method)
        {
            var body = method.Body;
            var stateVarIndex = FindStateVariableIndex(method);
            if (stateVarIndex == -1) return;

            var instructions = body.Instructions;
            var toRemove = new HashSet<Instruction>();

            for (int i = 0; i < instructions.Count; i++)
            {
                var instr = instructions[i];

                // 1. Удаление инициализации: ldc.X / stloc.s V_0
                if (IsStloc(instr, stateVarIndex) && i > 0)
                {
                    if (GetInt64Value(instructions[i-1]).HasValue)
                    {
                        toRemove.Add(instructions[i-1]); // ldc
                        toRemove.Add(instr);             // stloc
                        continue;
                    }
                }

                // 2. Удаление обновлений состояния внутри цикла
                if (IsStloc(instr, stateVarIndex) && i > 0)
                {
                     if (GetInt64Value(instructions[i-1]).HasValue)
                     {
                         toRemove.Add(instructions[i-1]);
                         toRemove.Add(instr);
                         continue;
                     }
                }

                // 3. Удаление проверок: ldloc.s V_0 / ldc.X / bne.un
                if (instr.OpCode.FlowControl == FlowControl.Cond_Branch && i >= 2)
                {
                    if (IsLdloc(instructions[i-2], stateVarIndex) && GetInt64Value(instructions[i-1]).HasValue)
                    {
                        toRemove.Add(instructions[i-2]); // ldloc
                        toRemove.Add(instructions[i-1]); // ldc
                        toRemove.Add(instr);             // branch
                        continue;
                    }
                }
                
                // 4. Удаление одиночных загрузок состояния, если они ни на что не влияют (редкий случай)
                if (IsLdloc(instr, stateVarIndex))
                {
                    // Проверяем, используется ли результат. Если следующая инструкция не потребляет стек, это мусор?
                    // Сложнее, пока пропустим, чтобы не сломать логику, если переменная используется реально.
                }
            }

            // Удаляем собранные инструкции
            for (int i = instructions.Count - 1; i >= 0; i--)
            {
                if (toRemove.Contains(instructions[i]))
                {
                    instructions.RemoveAt(i);
                }
            }
            
            if (toRemove.Count > 0)
            {
                body.UpdateInstructionOffsets();
                body.SimplifyMacros(method.Parameters);
            }
        }

        /// <summary>
        /// Простое упрощение условных переходов, если нет явной переменной состояния.
        /// </summary>
        private bool SimplifyConditionalJumps(MethodDef method)
        {
            var body = method.Body;
            bool changed = true;
            int maxPasses = 10;
            int pass = 0;

            while (changed && pass < maxPasses)
            {
                changed = false;
                pass++;
                
                // Эмуляция значений стека (очень примитивная)
                var knownLocals = new Dictionary<int, object?>();
                var instructions = body.Instructions;

                for (int i = 0; i < instructions.Count; i++)
                {
                    var instr = instructions[i];

                    // Отслеживание присваиваний констант локалям
                    if (IsStloc(instr, out int lIdx) && i > 0)
                    {
                        var val = GetConstantValue(instructions[i - 1]);
                        if (val != null) knownLocals[lIdx] = val;
                    }

                    // Разрешение ветвлений
                    if (instr.OpCode.FlowControl == FlowControl.Cond_Branch && i >= 2)
                    {
                        var prev1 = instructions[i - 1];
                        var prev2 = instructions[i - 2];

                        if (IsLdloc(prev2, out int checkIdx) && knownLocals.ContainsKey(checkIdx))
                        {
                            var actualVal = knownLocals[checkIdx];
                            var compareVal = GetConstantValue(prev1);

                            if (compareVal != null)
                            {
                                var result = CompareValues(actualVal, compareVal, instr.OpCode.Code);
                                if (result.HasValue)
                                {
                                    if (result.Value)
                                    {
                                        instr.OpCode = OpCodes.Br;
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
                }
                
                if (changed) RemoveUnreachableBlocks(method);
            }
            return changed;
        }

        #region Helpers

        private int FindStateVariableIndex(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            // Ищем паттерн: ldc.X / stloc.s V_0 в начале метода
            for (int i = 0; i < Math.Min(10, instructions.Count - 1); i++)
            {
                if (IsStloc(instructions[i+1], out int idx))
                {
                    if (GetInt64Value(instructions[i]).HasValue)
                    {
                        // Проверяем, используется ли эта переменная в сравнениях далее
                        bool usedInComparison = false;
                        for (int j = i + 2; j < instructions.Count; j++)
                        {
                            if (IsLdloc(instructions[j], idx) && j + 2 < instructions.Count)
                            {
                                if (GetInt64Value(instructions[j+1]).HasValue && 
                                    instructions[j+2].OpCode.FlowControl == FlowControl.Cond_Branch)
                                {
                                    usedInComparison = true;
                                    break;
                                }
                            }
                        }
                        if (usedInComparison) return idx;
                    }
                }
            }
            return -1;
        }

        private bool IsStloc(Instruction instr, int index)
        {
            if (instr.Operand is Local l) return l.Index == index;
            if (index == 0 && instr.OpCode.Code == Code.Stloc_0) return true;
            if (index == 1 && instr.OpCode.Code == Code.Stloc_1) return true;
            if (index == 2 && instr.OpCode.Code == Code.Stloc_2) return true;
            if (index == 3 && instr.OpCode.Code == Code.Stloc_3) return true;
            if (instr.OpCode.Code == Code.Stloc_S || instr.OpCode.Code == Code.Stloc)
            {
                if (instr.Operand is Local loc) return loc.Index == index;
            }
            return false;
        }

        private bool IsStloc(Instruction instr, out int index)
        {
            index = -1;
            if (instr.Operand is Local l) index = l.Index;
            else if (instr.OpCode.Code == Code.Stloc_0) index = 0;
            else if (instr.OpCode.Code == Code.Stloc_1) index = 1;
            else if (instr.OpCode.Code == Code.Stloc_2) index = 2;
            else if (instr.OpCode.Code == Code.Stloc_3) index = 3;
            return index != -1;
        }

        private bool IsLdloc(Instruction instr, int index)
        {
            if (instr.Operand is Local l) return l.Index == index;
            if (index == 0 && instr.OpCode.Code == Code.Ldloc_0) return true;
            if (index == 1 && instr.OpCode.Code == Code.Ldloc_1) return true;
            if (index == 2 && instr.OpCode.Code == Code.Ldloc_2) return true;
            if (index == 3 && instr.OpCode.Code == Code.Ldloc_3) return true;
            if (instr.OpCode.Code == Code.Ldloc_S || instr.OpCode.Code == Code.Ldloc)
            {
                if (instr.Operand is Local loc) return loc.Index == index;
            }
            return false;
        }

        private bool IsLdloc(Instruction instr, out int index)
        {
            index = -1;
            if (instr.Operand is Local l) index = l.Index;
            else if (instr.OpCode.Code == Code.Ldloc_0) index = 0;
            else if (instr.OpCode.Code == Code.Ldloc_1) index = 1;
            else if (instr.OpCode.Code == Code.Ldloc_2) index = 2;
            else if (instr.OpCode.Code == Code.Ldloc_3) index = 3;
            return index != -1;
        }

        private long? GetInt64Value(Instruction instr)
        {
            switch (instr.OpCode.Code)
            {
                case Code.Ldc_I4: return (int)(instr.Operand ?? 0);
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
                case Code.Ldc_I8: return (long)(instr.Operand ?? 0);
                case Code.Ldc_R4: return Convert.ToInt64((float)(instr.Operand ?? 0));
                case Code.Ldc_R8: return Convert.ToInt64((double)(instr.Operand ?? 0));
                default: return null;
            }
        }

        private object? GetConstantValue(Instruction instr)
        {
            var val = GetInt64Value(instr);
            if (val.HasValue) return val.Value;
            if (instr.OpCode.Code == Code.Ldstr) return instr.Operand as string;
            if (instr.OpCode.Code == Code.Ldnull) return null;
            return null;
        }

        private bool EvaluateBranch(long currentState, long compareValue, Code opCode)
        {
            switch (opCode)
            {
                case Code.Beq: case Code.Beq_S: return currentState == compareValue;
                case Code.Bne_Un: case Code.Bne_Un_S: return currentState != compareValue;
                case Code.Bgt: case Code.Bgt_S: case Code.Bgt_Un: case Code.Bgt_Un_S: return currentState > compareValue;
                case Code.Bge: case Code.Bge_S: case Code.Bge_Un: case Code.Bge_Un_S: return currentState >= compareValue;
                case Code.Blt: case Code.Blt_S: case Code.Blt_Un: case Code.Blt_Un_S: return currentState < compareValue;
                case Code.Ble: case Code.Ble_S: case Code.Ble_Un: case Code.Ble_Un_S: return currentState <= compareValue;
                case Code.Brtrue: case Code.Brtrue_S: return currentState != 0;
                case Code.Brfalse: case Code.Brfalse_S: return currentState == 0;
            }
            return false;
        }

        private bool? CompareValues(object? actual, object? expected, Code opCode)
        {
            if (actual == null || expected == null) return null;
            long lActual = Convert.ToInt64(actual);
            long lExpected = Convert.ToInt64(expected);
            return EvaluateBranch(lActual, lExpected, opCode);
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
                        if (reachable.Add(next)) queue.Enqueue(next);
                    }
                }

                if (curr.Operand is Instruction target)
                {
                    if (reachable.Add(target)) queue.Enqueue(target);
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

        private void CleanupMethod(MethodDef method)
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

        private Instruction CloneInstruction(Instruction orig)
        {
            var opCode = orig.OpCode;
            var operand = orig.Operand;

            if (operand is Local local) return Instruction.Create(opCode, local);
            if (operand is Parameter param) return Instruction.Create(opCode, param);
            if (operand is Instruction target) return Instruction.Create(opCode, target);
            if (operand is Instruction[] targets) return Instruction.Create(opCode, targets);
            if (operand is string str) return Instruction.Create(opCode, str);
            if (operand is int i) return Instruction.Create(opCode, i);
            if (operand is long l) return Instruction.Create(opCode, l);
            if (operand is float f) return Instruction.Create(opCode, f);
            if (operand is double d) return Instruction.Create(opCode, d);
            if (operand is ITypeDefOrRef type) return Instruction.Create(opCode, type);
            if (operand is MethodDef md) return Instruction.Create(opCode, md);
            if (operand is FieldDef fd) return Instruction.Create(opCode, fd);
            if (operand is MemberRef mr) return Instruction.Create(opCode, mr);
            
            return new Instruction(opCode, operand);
        }

        private void ReplaceMethodBody(MethodDef method, List<Instruction> newInstructions)
        {
            var body = method.Body;
            body.Instructions.Clear();
            foreach (var instr in newInstructions) body.Instructions.Add(instr);
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
            _module.Dispose();
        }
    }
}
