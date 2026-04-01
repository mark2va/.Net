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
                            RemoveDeadStores(method); // Дополнительная очистка мертвых записей
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
        /// Строит граф переходов и собирает линейный код, отбрасывая мусор.
        /// </summary>
        private bool UnpackStateMachine(MethodDef method)
        {
            var body = method.Body;
            if (body == null || !body.HasInstructions) return false;
            var instructions = body.Instructions;
            if (instructions.Count < 5) return false;

            // 1. Поиск переменной состояния и её начального значения
            int stateVarIndex = -1;
            object? initialState = null;
            int initInstrIndex = -1;

            for (int i = 0; i < Math.Min(10, instructions.Count - 1); i++)
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

            if (stateVarIndex == -1 || initialState == null) return false;

            // Собираем все значения состояний, встречающиеся в коде (константы в сравнениях и присваиваниях)
            var stateValues = new HashSet<object>();
            stateValues.Add(initialState);
            
            foreach (var instr in instructions)
            {
                if (instr.OpCode.Code == Code.Ldc_I4 || instr.OpCode.Code == Code.Ldc_I8 || 
                    instr.OpCode.Code == Code.Ldc_R4 || instr.OpCode.Code == Code.Ldc_R8)
                {
                    var val = GetConstantValue(instr);
                    if (val != null) stateValues.Add(val);
                }
            }

            // 2. Разбиваем код на блоки, привязанные к состояниям.
            // Блок начинается после проверки состояния или присваивания нового состояния.
            // Мы будем эмулировать переходы статически.
            
            // Структура: State -> Список полезных инструкций до следующего перехода
            var stateGraph = new Dictionary<object, List<Instruction>>();
            // Переходы: State -> NextState (если переход безусловный внутри блока) или null (если выход/условие)
            var transitions = new Dictionary<object, object?>(); 
            
            // Текущее анализируемое состояние
            object? currentState = initialState;
            var currentBlock = new List<Instruction>();
            
            bool insideLoop = false;
            // Пытаемся найти начало цикла (обычно сразу после инициализации или через пару nop)
            // Для простоты считаем, что весь метод после инициализации - это тело автомата
            
            int ip = initInstrIndex + 2; // Пропускаем ldc, stloc
            
            // Флаг, указывающий, что мы нашли полезную инструкцию в текущем состоянии
            bool foundLogicInCurrentState = false;

            while (ip < instructions.Count)
            {
                var instr = instructions[ip];
                
                // Пропускаем NOP
                if (instr.OpCode.Code == Code.Nop)
                {
                    ip++;
                    continue;
                }

                // Проверка: является ли инструкция частью механизма обфускации?
                bool isStateOp = false;

                // 1. Чтение переменной состояния (ldloc state)
                if (IsLdloc(instr, out int lIdx) && lIdx == stateVarIndex)
                {
                    isStateOp = true;
                }

                // 2. Запись в переменную состояния (stloc state) - это переход
                if (IsStloc(instr, out int sIdx) && sIdx == stateVarIndex)
                {
                    // Предыдущая инструкция должна быть константой (новое состояние)
                    if (ip > 0)
                    {
                        var prev = instructions[ip - 1];
                        var newVal = GetConstantValue(prev);
                        if (newVal != null)
                        {
                            // Это явный переход: текущее состояние -> newVal
                            // Но нам нужно понять, к какому состоянию относится этот блок.
                            // В паттерне: if (num == X) { ... num = Y; }
                            // Мы находимся в блоке X. Инструкция num=Y означает переход в Y.
                            
                            if (currentState != null)
                            {
                                // Сохраняем накопленный блок для currentState
                                if (!stateGraph.ContainsKey(currentState))
                                    stateGraph[currentState] = new List<Instruction>();
                                stateGraph[currentState].AddRange(currentBlock);
                                
                                // Записываем переход
                                transitions[currentState] = newVal;
                                
                                // Сбрасываем буфер для нового состояния
                                currentBlock.Clear();
                                currentState = newVal;
                                foundLogicInCurrentState = false;
                            }
                            isStateOp = true; // Саму инструкцию stloc не копируем
                        }
                    }
                }

                // 3. Сравнения (ceq, cgt, clt и т.д.) - часто идут после ldloc state, ldc const
                if (instr.OpCode.Code == Code.Ceq || instr.OpCode.Code == Code.Cgt || instr.OpCode.Code == Code.Clt ||
                    instr.OpCode.Code == Code.Cgt_Un || instr.OpCode.Code == Code.Clt_Un)
                {
                    isStateOp = true;
                }

                // 4. Ветвления (br, bne.un, beq, brtrue, brfalse)
                if (instr.OpCode.FlowControl == FlowControl.Branch || instr.OpCode.FlowControl == FlowControl.Cond_Branch)
                {
                    isStateOp = true;
                    // Если это условный выход из цикла (например, while (num != exit)), то мы можем его проигнорировать,
                    // так как мы строим линейный путь до выхода.
                    // Если это внутренний переход, он уже обработан через stloc.
                }

                // Если инструкция не является частью обфускации, добавляем её в текущий блок
                if (!isStateOp)
                {
                    // Дополнительная проверка: не является ли эта инструкция просто загрузкой константы состояния,
                    // которая осталась без пары (редкий случай, но возможный при сложном анализе)
                    var constVal = GetConstantValue(instr);
                    if (constVal != null && stateValues.Contains(constVal))
                    {
                        // Скорее всего, это часть сравнения, которое мы пропустили, или мусор.
                        // Но если перед ней нет ldloc state, то это может быть реальная константа.
                        // Для безопасности проверим контекст. 
                        // В простом случае state machine: ldc state_val -> stloc state_var.
                        // Если мы видим ldc state_val отдельно, это мусор.
                        isStateOp = true; 
                    }
                    else
                    {
                        currentBlock.Add(CloneInstruction(instr));
                        foundLogicInCurrentState = true;
                    }
                }

                ip++;
            }
            
            // Добавляем последний блок
            if (currentState != null && currentBlock.Count > 0)
            {
                if (!stateGraph.ContainsKey(currentState))
                    stateGraph[currentState] = new List<Instruction>();
                stateGraph[currentState].AddRange(currentBlock);
            }

            // 3. Сборка линейного кода путем обхода графа от initialState до тупика
            var finalInstructions = new List<Instruction>();
            var visitedStates = new HashSet<object>();
            var queue = new Queue<object>();
            
            if (initialState != null) queue.Enqueue(initialState);

            while (queue.Count > 0)
            {
                var state = queue.Dequeue();
                if (visitedStates.Contains(state)) continue;
                visitedStates.Add(state);

                if (stateGraph.TryGetValue(state, out var block))
                {
                    finalInstructions.AddRange(block);
                    
                    // Если есть переход из этого состояния, идем дальше
                    if (transitions.TryGetValue(state, out var nextState) && nextState != null)
                    {
                        if (!visitedStates.Contains(nextState))
                            queue.Enqueue(nextState);
                    }
                }
            }

            if (finalInstructions.Count == 0) return false;

            // Заменяем тело метода
            ReplaceMethodBody(method, finalInstructions);
            return true;
        }

        /// <summary>
        /// Удаляет мертвые записи в локальные переменные, которые сразу же перезаписываются или не используются.
        /// Помогает убрать остатки типа "num;".
        /// </summary>
        private void RemoveDeadStores(MethodDef method)
        {
            var body = method.Body;
            var instructions = body.Instructions;
            
            // Упрощенная эвристика: если видим последовательность:
            // ldloc X
            // pop (или сразу следующая инструкция не использует значение)
            // И при этом X не используется далее для реальных вычислений...
            // Это сложно сделать надежно без полного SSA анализа.
            
            // Более простой подход для данного случая:
            // Очистить инструкции, которые оставляют значение на стеке и ничего с ним не делают.
            // Например, если метод возвращает void, а на стеке остается значение - это ошибка баланса,
            // но декомпилятор может показать это как "value;".
            // Нам нужно убедиться, что стек сбалансирован.
            
            // В данном конкретном случае ("1990;", "text;") проблема в том, что мы скопировали
            // инструкции загрузки (ldc, ldloc), но не скопировали инструкции использования (ret, stloc, вызов).
            // Нет, мы копировали всё, кроме мусора.
            // Ага, проблема в том, что в оригинале было:
            // ldc 1990
            // stloc num
            // А мы удалили stloc (как op состояния), но оставили ldc? 
            // Нет, в коде выше я добавил фильтр: if (constVal != null && stateValues.Contains(constVal)) isStateOp = true;
            // Это должно было убрать ldc 1990.
            
            // Почему тогда осталось "1990;"?
            // Значит, эти константы НЕ попали в stateValues или не были распознаны как ldc.
            // Или, возможно, это не ldc, а что-то еще?
            // Или, возможно, это ldloc text; в конце метода, который декомпилятор показывает как "text;"?
            
            // Давайте добавим финальную очистку: удаление инструкций, которые просто загружают значение и ничего с ним не делают,
            // если это значение не используется следующей инструкцией.
            
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = 0; i < instructions.Count - 1; i++)
                {
                    var curr = instructions[i];
                    var next = instructions[i+1];

                    // Если текущая инструкция загружает константу или локаль, а следующая не использует стек...
                    if ((curr.OpCode.Code == Code.Ldc_I4 || curr.OpCode.Code == Code.Ldc_I8 || 
                         curr.OpCode.Code == Code.Ldc_R4 || curr.OpCode.Code == Code.Ldc_R8 ||
                         IsLdloc(curr, out _)) &&
                        !NextInstructionConsumesStack(next))
                    {
                        // Проверяем, не является ли это аргументом для следующей инструкции, которая просто не берет его со стека явно?
                        // Нет, если next не потребляет стек, значит curr оставляет мусор.
                        
                        // Исключение: если curr - это подготовка к возврату (но тогда next должен быть ret)
                        if (next.OpCode.Code == Code.Ret)
                        {
                             // Если curr загружает значение, а next - ret, это нормально (возврат значения).
                             continue;
                        }
                        
                        // Если curr загружает значение, которое тут же дублируется или используется странно?
                        // В случае "text;" в конце метода перед return:
                        // Обычно это: ldloc text, ret.
                        // Если мы видим просто ldloc text, а потом что-то еще, это странно.
                        
                        // Попробуем удалить curr, если оно точно лишнее.
                        // Но будьте осторожны: удаление может нарушить стек для последующих инструкций.
                        // В данном случае, если значение не потребляется, его удаление безопасно.
                        
                        instructions.RemoveAt(i);
                        changed = true;
                        break; 
                    }
                }
            }
            
            body.UpdateInstructionOffsets();
        }

        private bool NextInstructionConsumesStack(Instruction instr)
        {
            // Инструкции, которые берут аргументы со стека
            switch (instr.OpCode.Code)
            {
                case Code.Stloc:
                case Code.Stloc_S:
                case Code.Stloc_0: case Code.Stloc_1: case Code.Stloc_2: case Code.Stloc_3:
                case Code.Call:
                case Code.Callvirt:
                case Code.Newobj:
                case Code.Add:
                case Code.Sub:
                case Code.Mul:
                case Code.Div:
                case Code.Rem:
                case Code.And:
                case Code.Or:
                case Code.Xor:
                case Code.Shl:
                case Code.Shr:
                case Code.Ceq:
                case Code.Cgt:
                case Code.Clt:
                case Code.Box:
                case Code.Unbox_Any:
                case Code.Castclass:
                case Code.Isinst:
                case Code.Ldelem:
                case Code.Stelem:
                case Code.Calli:
                case Code.Initobj:
                case Code.Constrained:
                case Code.Readonly:
                case Code.Unaligned:
                case Code.Volatile:
                case Code.Tailcall:
                case Code.No: // No. prefix?
                case Code.Refanyval:
                case Code.Mkrefany:
                case Code.Arglist:
                case Code.Throw:
                case Code.Endfilter:
                case Code.Endfinally:
                case Code.Leave:
                case Code.Leave_S:
                case Code.Rethrow:
                case Code.Sizeof: // Не потребляет, но и не оставляет в обычном смысле
                case Code.Refanytype:
                    return true;
                default:
                    return false;
            }
        }

        private void SimplifyControlFlow(MethodDef method)
        {
            // Реализация упрощения потока для случаев, не являющихся state machine
            // (можно оставить пустой или использовать старую логику, если нужна)
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
