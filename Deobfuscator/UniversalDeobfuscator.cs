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
                        if (UnpackStateMachine(method))
                        {
                            CleanupNops(method);
                            RemoveDeadStores(method);
                            count++;
                        }
                        else
                        {
                            // Если не state machine, пробуем упростить поток
                            SimplifyControlFlow(method);
                            CleanupNops(method);
                        }

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
        /// Распутывает обфускацию типа "State Machine".
        /// Строит граф переходов и собирает линейный код.
        /// </summary>
        private bool UnpackStateMachine(MethodDef method)
        {
            var body = method.Body;
            if (body == null || !body.HasInstructions) return false;
            var instructions = body.Instructions;
            if (instructions.Count < 5) return false;

            // 1. Поиск переменной состояния
            int stateVarIndex = -1;
            object? initialState = null;
            int initInstrIndex = -1;

            for (int i = 0; i < Math.Min(10, instructions.Count); i++)
            {
                if (i + 1 < instructions.Count)
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
                            initInstrIndex = i;
                            break;
                        }
                    }
                }
            }

            if (stateVarIndex == -1 || initialState == null) return false;

            // 2. Анализ графа переходов
            // Словарь: Значение состояния -> Список инструкций (полезная нагрузка)
            var stateBlocks = new Dictionary<object, List<Instruction>>();
            // Словарь: Значение состояния -> Следующее значение состояния (переход)
            var transitions = new Dictionary<object, object?>();
            // Множество состояний, которые являются точками выхода (break/ret)
            var exitStates = new HashSet<object>();

            // Проходим по инструкциям, чтобы собрать блоки
            // Логика: ищем паттерн "if (num == X) { ... num = Y; }"
            // В обфускаторе это часто выглядит как последовательность проверок
            
            // Упрощенный парсер для типичного паттерна:
            // do { if (num == A) { code; num = B; } if (num == C) ... } while (num != Exit);
            
            // Мы будем эмулировать "статически", проходя по списку и выявляя блоки
            int ip = 0;
            while (ip < instructions.Count)
            {
                var instr = instructions[ip];
                
                // Пропускаем инициализацию
                if (ip == initInstrIndex || ip == initInstrIndex + 1)
                {
                    ip++;
                    continue;
                }

                // Ищем проверку состояния: ldloc(state), ldc(value), ceq/cgt/clt, brtrue/brfalse
                // Или более простой вариант в некоторых обфускаторах: прямая проверка через ветвление
                
                // Попробуем найти блок, начинающийся с проверки конкретного значения
                // Паттерн: 
                // IL_X: ldloc.s V_0 (state)
                // IL_Y: ldc.i4 XXXX
                // IL_Z: ceq
                // IL_K: brtrue.s IL_Target (блок кода)
                
                if (instr.OpCode.Code == Code.Ldloc || instr.OpCode.Code == Code.Ldloc_S || 
                   (instr.OpCode.Code >= Code.Ldloc_0 && instr.OpCode.Code <= Code.Ldloc_3))
                {
                    if (GetLocalIndex(instr) == stateVarIndex)
                    {
                        if (ip + 3 < instructions.Count)
                        {
                            var ldcVal = instructions[ip + 1];
                            var cmp = instructions[ip + 2];
                            var branch = instructions[ip + 3];

                            if (GetConstantValue(ldcVal) is object checkValue &&
                                (cmp.OpCode.Code == Code.Ceq || cmp.OpCode.Code == Code.Cgt || cmp.OpCode.Code == Code.Clt) &&
                                (branch.OpCode.Code == Code.Brtrue || branch.OpCode.Code == Code.Brtrue_S || branch.OpCode.Code == Code.Brfalse || branch.OpCode.Code == Code.Brfalse_S))
                            {
                                // Нашли проверку состояния checkValue
                                // Определяем, куда идет ветка (true/false)
                                bool isBrTrue = branch.OpCode.Code == Code.Brtrue || branch.OpCode.Code == Code.Brtrue_S;
                                Instruction targetBlock = branch.Operand as Instruction;
                                
                                if (targetBlock != null)
                                {
                                    // Извлекаем инструкции из целевого блока до следующего перехода или конца метода
                                    var blockInstructions = ExtractBlock(instructions, targetBlock, stateVarIndex, out object? nextState, out bool isExit);
                                    
                                    if (!stateBlocks.ContainsKey(checkValue))
                                        stateBlocks[checkValue] = blockInstructions;
                                    
                                    transitions[checkValue] = nextState;
                                    if (isExit) exitStates.Add(checkValue);
                                    
                                    // Перемещаем IP за эту конструкцию проверки, чтобы не дублировать
                                    // Находим следующую инструкцию после блока ветвления
                                    // Это сложно сделать точно без полного CFG, поэтому просто идем дальше
                                    // В данном типе обфускации блоки обычно идут последовательно в IL
                                }
                            }
                        }
                    }
                }
                
                // Также ищем условие выхода из цикла do-while / for
                // Обычно это сравнение state var с конечным значением и branch назад
                if (instr.OpCode.FlowControl == FlowControl.Cond_Branch)
                {
                     // Проверка на выход: если ветка ведет назад на начало цикла, а условие зависит от state var
                     // Это обрабатывается логикой exitStates
                }

                ip++;
            }

            // 3. Сборка линейного кода
            if (stateBlocks.Count == 0) return false;

            var finalInstructions = new List<Instruction>();
            var visitedStates = new HashSet<object>();
            var queue = new Queue<object>();

            if (initialState != null) queue.Enqueue(initialState);

            while (queue.Count > 0)
            {
                var currentState = queue.Dequeue();
                if (visitedStates.Contains(currentState)) continue;
                visitedStates.Add(currentState);

                if (stateBlocks.TryGetValue(currentState, out var block))
                {
                    foreach (var ins in block)
                    {
                        finalInstructions.Add(CloneInstruction(ins));
                    }
                }

                if (transitions.TryGetValue(currentState, out var nextVal) && nextVal != null)
                {
                    if (!visitedStates.Contains(nextVal))
                        queue.Enqueue(nextVal);
                }
            }

            if (finalInstructions.Count == 0) return false;

            // Добавляем возврат, если его нет (на случай если последний блок не имел ret)
            if (method.ReturnType.FullName != "System.Void")
            {
                // Проверка, есть ли уже ret
                bool hasRet = finalInstructions.Any(i => i.OpCode.Code == Code.Ret);
                if (!hasRet)
                {
                    // Это упрощение, в реальном коде нужно отслеживать стек
                    // Но для данного типа обфускации обычно ret есть внутри блоков
                }
            }

            ReplaceMethodBody(method, finalInstructions);
            return true;
        }

        /// <summary>
        /// Извлекает инструкции блока кода для конкретного состояния.
        /// Останавливается на следующем переходе состояния или выходе.
        /// </summary>
        private List<Instruction> ExtractBlock(IList<Instruction> allInstructions, Instruction startInstr, int stateVarIndex, out object? nextState, out bool isExit)
        {
            var block = new List<Instruction>();
            nextState = null;
            isExit = false;

            int startIndex = allInstructions.IndexOf(startInstr);
            if (startIndex == -1) return block;

            int ip = startIndex;
            while (ip < allInstructions.Count)
            {
                var instr = allInstructions[ip];
                
                // Пропускаем NOP
                if (instr.OpCode.Code == Code.Nop)
                {
                    ip++;
                    continue;
                }

                // Если встретили загрузку константы и сохранение в state var -> это переход
                // Паттерн: ldc.X, stloc.s (state)
                if (ip + 1 < allInstructions.Count)
                {
                    var next = allInstructions[ip + 1];
                    if (IsStloc(next, out int sIdx) && sIdx == stateVarIndex)
                    {
                        var val = GetConstantValue(instr);
                        if (val != null)
                        {
                            nextState = val;
                            // Не добавляем эти инструкции в блок (это служебные)
                            ip += 2;
                            continue; 
                        }
                    }
                }

                // Если встретили безусловный переход вперед (не назад в цикл) -> возможно конец блока
                if (instr.OpCode.FlowControl == FlowControl.Branch)
                {
                    if (instr.Operand is Instruction target)
                    {
                        int targetIdx = allInstructions.IndexOf(target);
                        if (targetIdx > ip) // Переход вперед
                        {
                            // Проверяем, не является ли это переходом на следующую проверку состояния
                            // В типичной обфускации после stloc идет branch на начало цикла или следующую проверку
                            // Здесь мы просто прерываем блок, считая переход концом логики этого состояния
                            ip++;
                            continue;
                        }
                        else 
                        {
                            // Переход назад (цикл) - игнорируем, это часть обфускации
                            ip++;
                            continue;
                        }
                    }
                }

                // Если встретили ret
                if (instr.OpCode.FlowControl == FlowControl.Return)
                {
                    block.Add(CloneInstruction(instr));
                    isExit = true;
                    ip++;
                    break;
                }

                // Если это инструкция загрузки константы, которая используется только для сравнения в цикле обфускации
                // (например, ldc.i4 1990 перед проверкой while), мы должны её пропустить
                // Эвристика: если за ldc следует сравнение с state var или проверка выхода
                // Для простоты пока добавляем всё, что не является явным переходом состояния
                
                // Фильтр мусора: если это ldc, за которым следует pop или ничего (оставляет мусор на стеке)
                // Но лучше отфильтровать на этапе сбора всего метода
                
                block.Add(instr);
                ip++;
                
                // Безопасный предел для блока
                if (block.Count > 50) break; 
            }

            return block;
        }

        /// <summary>
        /// Удаляет инструкции, оставляющие бесполезные значения на стеке (мусор).
        /// Например: ldc.i4 123 (без использования)
        /// </summary>
        private void RemoveDeadStores(MethodDef method)
        {
            var body = method.Body;
            if (body == null) return;
            
            bool changed = true;
            while (changed)
            {
                changed = false;
                var instructions = body.Instructions;
                
                // Анализируем стек виртуально
                // Если инструкция пушит значение, а следующее её просто попает или заменяет без использования
                // В данном случае нас интересуют одиночные ldc, за которыми следуют другие ldc или ret без использования
                
                for (int i = 0; i < instructions.Count - 1; i++)
                {
                    var curr = instructions[i];
                    var next = instructions[i+1];

                    // Паттерн мусора: ldc.X (константа состояния), за которым следует другая ldc или ret
                    // В нормальном коде после ldc должно быть использование (stloc, call, add и т.д.)
                    if (curr.OpCode.Code == Code.Ldc_I4 || curr.OpCode.Code == Code.Ldc_I8 || 
                        curr.OpCode.Code == Code.Ldc_R4 || curr.OpCode.Code == Code.Ldc_R8)
                    {
                        // Проверяем, используется ли это значение следующей инструкцией
                        bool isUsed = false;
                        
                        if (next.OpCode.Code == Code.Stloc || next.OpCode.Code == Code.Stloc_S || 
                            next.OpCode.Code == Code.Stloc_0 || next.OpCode.Code == Code.Stloc_1 ||
                            next.OpCode.Code == Code.Stloc_2 || next.OpCode.Code == Code.Stloc_3)
                            isUsed = true;
                        else if (next.OpCode.StackBehaviourPush == StackBehaviour.Pop1 || 
                                 next.OpCode.StackBehaviourPush == StackBehaviour.Pop1_pop1)
                            isUsed = true; // Потребляется
                        else if (next.OpCode.Code == Code.Pop)
                        {
                            // Явный поп мусора - можно удалить обе инструкции
                            instructions.RemoveAt(i+1); // удаляем pop
                            instructions.RemoveAt(i);   // удаляем ldc
                            changed = true;
                            break;
                        }

                        if (!isUsed && next.OpCode.Code != Code.Nop)
                        {
                            // Если значение не используется и не потребляется, и следующая инструкция не Nop
                            // Это кандидат на мусор (например, оставшееся число от обфускации)
                            // Но надо быть осторожным, чтобы не удалить аргументы для вызова метода
                            // В контексте данной обфускации это обычно голые константы состояния
                            
                            // Дополнительная проверка: если это константа, совпадающая с известными состояниями?
                            // Пока просто удаляем, если за ней сразу идет другая константа или ret
                            if ((next.OpCode.Code == Code.Ldc_I4 || next.OpCode.Code == Code.Ldc_I8 || 
                                 next.OpCode.Code == Code.Ldc_R4 || next.OpCode.Code == Code.Ldc_R8 ||
                                 next.OpCode.Code == Code.Ret))
                            {
                                instructions.RemoveAt(i);
                                changed = true;
                                break;
                            }
                        }
                    }
                }
            }
            body.UpdateInstructionOffsets();
        }

        private void SimplifyControlFlow(MethodDef method)
        {
            // Резервный метод, если основной не сработал
            var body = method.Body;
            if (body == null) return;
            
            // Удаление недостижимых блоков
            RemoveUnreachableBlocks(method);
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
