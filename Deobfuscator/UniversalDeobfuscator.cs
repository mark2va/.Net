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

        /// <summary>
        /// Эмулирует выполнение для построения правильного линейного потока.
        /// </summary>
        private bool EmulateAndUnravel(MethodDef method)
        {
            var body = method.Body;
            if (body.Instructions.Count == 0) return false;

            // Попытка найти переменную состояния
            var stateVarIndex = FindStateVariableIndex(method);
            
            // Если это не похоже на state machine, пробуем простое упрощение ветвлений
            if (stateVarIndex == -1)
            {
                return SimplifyConditionalJumps(method);
            }

            // Эмуляция state machine
            var newInstructions = new List<Instruction>();
            var visitedStates = new HashSet<int>(); // Для предотвращения зацикливания при эмуляции
            
            // Стек для хранения точек возврата (если бы были подпрограммы), здесь просто очередь инструкций
            var queue = new Queue<Instruction>();
            queue.Enqueue(body.Instructions[0]);

            // Карта: Инструкция -> Следующая инструкция (после разрешения переходов)
            // В данном случае мы просто идем по пути выполнения
            
            // Инициализация эмуляции
            // Нам нужно найти первое присваивание константы переменной состояния
            long? currentState = null;
            
            // Первый проход: найдем начальное значение
            for (int i = 0; i < body.Instructions.Count - 1; i++)
            {
                if (IsStloc(body.Instructions[i+1], stateVarIndex))
                {
                    var val = GetInt64Value(body.Instructions[i]);
                    if (val.HasValue)
                    {
                        currentState = val.Value;
                        break;
                    }
                }
            }

            if (!currentState.HasValue) return false;

            // Основной цикл эмуляции
            // Мы будем сканировать инструкции и выполнять только те блоки, куда ведет логика
            var processedInstructions = new HashSet<Instruction>();
            var workList = new List<Instruction> { body.Instructions[0] };
            
            while(workList.Count > 0)
            {
                var currentInstr = workList[0];
                workList.RemoveAt(0);

                if (processedInstructions.Contains(currentInstr)) continue;
                
                // Простой линейный проход от currentInstr до конца метода или пока не встретим управляющую конструкцию
                int idx = body.Instructions.IndexOf(currentInstr);
                if (idx == -1) continue;

                while (idx < body.Instructions.Count)
                {
                    var instr = body.Instructions[idx];
                    
                    if (processedInstructions.Contains(instr)) 
                    {
                        // Если мы наткнулись на уже обработанную инструкцию (цикл), проверяем, нужно ли идти дальше
                        break; 
                    }

                    // Анализ инструкций
                    if (instr.OpCode.Code == Code.Nop)
                    {
                        idx++;
                        continue;
                    }

                    // Проверка на изменение состояния: num = X
                    if (IsStloc(instr, stateVarIndex) && idx > 0)
                    {
                        var prev = body.Instructions[idx - 1];
                        var newVal = GetInt64Value(prev);
                        if (newVal.HasValue)
                        {
                            // Это просто обновление состояния, саму инструкцию пропускаем (она мусор в линейном коде)
                            // Но нам нужно понять, куда идти дальше. 
                            // Обычно после stloc идет либо проверка (ldloc + bne), либо конец блока
                            idx++;
                            continue; 
                        }
                    }

                    // Проверка на условие выхода из цикла состояния: while (num != X) или if (num == X)
                    if (instr.OpCode.FlowControl == FlowControl.Cond_Branch)
                    {
                        // Пытаемся разрешить переход
                        if (idx >= 2)
                        {
                            var prev1 = body.Instructions[idx - 1]; // Значение для сравнения (константа)
                            var prev2 = body.Instructions[idx - 2]; // Загрузка переменной (ldloc)

                            if (IsLdloc(prev2, stateVarIndex))
                            {
                                var compareVal = GetInt64Value(prev1);
                                if (compareVal.HasValue)
                                {
                                    bool takeJump = EvaluateBranch(currentState.Value, compareVal.Value, instr.OpCode.Code);
                                    
                                    Instruction nextInstr = takeJump ? (Instruction)instr.Operand : body.Instructions[idx + 1];
                                    
                                    if (nextInstr != null && !processedInstructions.Contains(nextInstr))
                                    {
                                        workList.Add(nextInstr);
                                    }
                                    
                                    // Условие resolved, саму инструкцию ветвления не добавляем в новый код
                                    idx++;
                                    continue;
                                }
                            }
                        }
                    }

                    // Если это не управляющая инструкция состояния, добавляем её в результат
                    // Но сначала проверим, не является ли она частью логики состояния (ldloc, ldc)
                    bool isStateLogic = false;
                    
                    // Пропускаем ldc, если следующая stloc переменной состояния
                    if (idx + 1 < body.Instructions.Count && IsStloc(body.Instructions[idx+1], stateVarIndex))
                    {
                         if (GetInt64Value(instr).HasValue) isStateLogic = true;
                    }
                    // Пропускаем ldloc переменной состояния, если используется для сравнения
                    if (IsLdloc(instr, stateVarIndex) && idx + 2 < body.Instructions.Count)
                    {
                        var next = body.Instructions[idx+1];
                        var next2 = body.Instructions[idx+2];
                        if (GetInt64Value(next).HasValue && next2.OpCode.FlowControl == FlowControl.Cond_Branch)
                        {
                            isStateLogic = true;
                        }
                    }

                    if (!isStateLogic)
                    {
                        newInstructions.Add(CloneInstruction(instr));
                    }

                    processedInstructions.Add(instr);
                    idx++;
                    
                    // Если встретили безусловный переход или возврат
                    if (instr.OpCode.FlowControl == FlowControl.Branch || 
                        instr.OpCode.FlowControl == FlowControl.Ret ||
                        instr.OpCode.FlowControl == FlowControl.Throw)
                    {
                        if (instr.Operand is Instruction target)
                        {
                            if (!processedInstructions.Contains(target)) workList.Add(target);
                        }
                        break; 
                    }
                }
            }

            if (newInstructions.Count > 0)
            {
                ReplaceMethodBody(method, newInstructions);
                return true;
            }

            return false;
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
