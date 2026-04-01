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
                            RemoveUnreachableBlocks(method);
                            count++;
                            Console.WriteLine($"[+] Unpacked: {method.FullName}");
                        }
                        else
                        {
                            // Если не state machine, пробуем обычную очистку потока
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
        /// Специализированный распаковщик State Machine обфускации.
        /// Строит граф переходов по значениям переменной состояния и выстраивает линейный код.
        /// </summary>
        private bool UnpackStateMachine(MethodDef method)
        {
            var body = method.Body;
            if (body == null || !body.HasInstructions) return false;
            var instructions = body.Instructions;

            // 1. Поиск переменной состояния и её начального значения
            int stateVarIndex = -1;
            object? initialState = null;
            int initInstrIndex = -1;

            // Ищем паттерн: ldc.X / stloc.X в начале метода
            for (int i = 0; i < Math.Min(10, instructions.Count); i++)
            {
                if (IsStloc(instructions[i], out int idx))
                {
                    if (i > 0)
                    {
                        var val = GetConstantValue(instructions[i - 1]);
                        if (val != null)
                        {
                            stateVarIndex = idx;
                            initialState = val;
                            initInstrIndex = i - 1;
                            break;
                        }
                    }
                }
            }

            if (stateVarIndex == -1 || initialState == null) return false;

            // 2. Анализ блоков кода для каждого состояния
            // Структура: StateValue -> Список инструкций (без инструкций работы с state var)
            var stateBlocks = new Dictionary<object, List<Instruction>>();
            // Переходы: StateValue -> NextStateValue (если есть присваивание stloc stateVar)
            var transitions = new Dictionary<object, object?>();
            // Выход из цикла: StateValue -> true (если это условие выхода while)
            var exitStates = new HashSet<object>();

            object? currentState = initialState;
            var currentBlock = new List<Instruction>();
            
            // Флаг, показывающий, что мы внутри цикла обфускации (после инициализации и до конца метода/ретурна)
            bool inLoop = false;

            for (int i = initInstrIndex + 2; i < instructions.Count; i++)
            {
                var instr = instructions[i];

                // Пропускаем NOP
                if (instr.OpCode.Code == Code.Nop) continue;

                // Проверка на выход из метода
                if (instr.OpCode.FlowControl == FlowControl.Return || instr.OpCode.FlowControl == FlowControl.Throw)
                {
                    // Сохраняем последний блок перед возвратом, если он есть
                    if (currentState != null && currentBlock.Count > 0)
                    {
                        if (!stateBlocks.ContainsKey(currentState)) stateBlocks[currentState] = new List<Instruction>();
                        stateBlocks[currentState].AddRange(currentBlock);
                    }
                    break;
                }

                // Обработка присваивания переменной состояния (stloc stateVar)
                if (IsStloc(instr, out int storeIdx) && storeIdx == stateVarIndex)
                {
                    // Предыдущая инструкция должна быть ldc (новое значение состояния)
                    if (i > 0)
                    {
                        var nextValInstr = instructions[i - 1];
                        // Иногда между ldc и stloc может быть dup или что-то еще, но в простой обфускации обычно сразу
                        // Проверяем, является ли операнд константой или результат вычисления
                        var nextVal = GetConstantValue(nextValInstr);
                        
                        if (nextVal != null)
                        {
                            // Завершаем текущий блок
                            if (currentState != null)
                            {
                                if (!stateBlocks.ContainsKey(currentState)) stateBlocks[currentState] = new List<Instruction>();
                                stateBlocks[currentState].AddRange(currentBlock);
                                
                                // Записываем переход
                                transitions[currentState] = nextVal;
                            }
                            
                            // Начинаем новый блок
                            currentState = nextVal;
                            currentBlock.Clear();
                            inLoop = true;
                            continue; // Пропускаем саму инструкцию stloc и ldc в сборку блока
                        }
                    }
                }

                // Обработка загрузки переменной состояния (ldloc stateVar) для условий
                if (IsLdloc(instr, out int loadIdx) && loadIdx == stateVarIndex)
                {
                    // Это часть условия сравнения. Обычно идет пара: ldloc, ldc, ceq/cgt/clt, brtrue/brfalse
                    // Нам нужно определить, является ли это условием выхода из цикла или переходом внутри
                    
                    // Смотрим вперед на 2-3 инструкции
                    if (i + 3 < instructions.Count)
                    {
                        var next1 = instructions[i+1]; // обычно ldc
                        var next2 = instructions[i+2]; // обычно ceq/cgt/clt
                        var next3 = instructions[i+3]; // обычно brtrue/brfalse или beq

                        var constVal = GetConstantValue(next1);
                        
                        if (constVal != null && 
                            (next2.OpCode.Code == Code.Ceq || next2.OpCode.Code == Code.Cgt || next2.OpCode.Code == Code.Clt ||
                             next2.OpCode.Code == Code.Cgt_Un || next2.OpCode.Code == Code.Clt_Un) &&
                            (next3.OpCode.FlowControl == FlowControl.Cond_Branch))
                        {
                            // Это условие перехода.
                            // Если условие "num != exitValue" (цикл while), то при истине мы продолжаем цикл, при ложи - выход.
                            // Но в деобфускации мы уже знаем порядок.
                            // Главное здесь - не добавлять эти инструкции сравнения и ветвления в чистый код,
                            // так как порядок блоков мы уже выстроили через transitions.
                            
                            // Однако, если это условие внутри блока (не управляющее циклом), его надо сохранить.
                            // В данной обфускации все ldloc(state) служат для управления потоком.
                            // Поэтому мы просто пропускаем всю конструкцию сравнения и ветвления.
                            
                            // Пропускаем ldc, cXX, brXX
                            i += 3; 
                            continue;
                        }
                    }
                }
                
                // Если это не инструкция управления состоянием, добавляем в текущий блок
                // Но нужно фильтровать сами инструкции перехода (br), которые ведут на условия
                if (instr.OpCode.FlowControl != FlowControl.Branch && instr.OpCode.FlowControl != FlowControl.Cond_Branch)
                {
                     currentBlock.Add(instr);
                }
            }

            // Добавляем последний блок, если он есть
            if (currentState != null && currentBlock.Count > 0)
            {
                if (!stateBlocks.ContainsKey(currentState)) stateBlocks[currentState] = new List<Instruction>();
                stateBlocks[currentState].AddRange(currentBlock);
            }

            if (stateBlocks.Count == 0) return false;

            // 3. Сборка линейного кода
            var newInstructions = new List<Instruction>();
            var visited = new HashSet<object>();
            var queue = new Queue<object>();
            
            if (initialState != null) queue.Enqueue(initialState);

            while (queue.Count > 0)
            {
                var state = queue.Dequeue();
                if (visited.Contains(state)) continue;
                visited.Add(state);

                if (stateBlocks.TryGetValue(state, out var block))
                {
                    foreach (var instr in block)
                    {
                        newInstructions.Add(CloneInstruction(instr));
                    }
                }

                if (transitions.TryGetValue(state, out var nextState) && nextState != null)
                {
                    if (!visited.Contains(nextState))
                        queue.Enqueue(nextState);
                }
            }

            if (newInstructions.Count == 0) return false;

            // Добавляем ret в конце, если его нет
            if (newInstructions.Last().OpCode.Code != Code.Ret && method.ReturnType != null)
            {
                 // Если метод должен что-то возвращать, а мы потеряли ret, надо быть осторожнее.
                 // Но в примере ret был после цикла.
                 // В нашем парсинге мы остановились на ret.
                 // Проверим, есть ли явный ret в исходных блоках.
                 // Если метод void, ret не обязателен в IL, но желателен.
                 if (method.ReturnType.CorLibTypeSig == CorLibTypeSig.Void)
                 {
                     newInstructions.Add(Instruction.Create(OpCodes.Ret));
                 }
            }
            
            // Если в оригинале был ret, он мог попасть в блок. Если нет - добавим.
            bool hasRet = newInstructions.Any(x => x.OpCode.Code == Code.Ret);
            if (!hasRet)
            {
                newInstructions.Add(Instruction.Create(OpCodes.Ret));
            }

            ReplaceMethodBody(method, newInstructions);
            return true;
        }

        /// <summary>
        /// Упрощает поток управления, заменяя условия, которые можно вычислить статически.
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

                var knownValues = new Dictionary<int, object?>();

                for (int i = 0; i < instructions.Count; i++)
                {
                    var instr = instructions[i];

                    if (i + 1 < instructions.Count)
                    {
                        var next = instructions[i + 1];
                        if (IsStloc(next, out int localIdx))
                        {
                            var val = GetConstantValue(instr);
                            if (val != null) knownValues[localIdx] = val;
                        }
                    }

                    if (instr.OpCode.FlowControl == FlowControl.Cond_Branch)
                    {
                        bool? result = TryEvaluateSimpleCondition(instructions, i, knownValues);
                        if (result.HasValue)
                        {
                            if (instr.Operand is Instruction target)
                            {
                                if (result.Value)
                                {
                                    instr.OpCode = OpCodes.Br;
                                    instr.Operand = target;
                                }
                                else
                                {
                                    instr.OpCode = OpCodes.Nop;
                                    instr.Operand = null;
                                }
                                changed = true;
                            }
                        }
                    }
                }
                if (changed) body.UpdateInstructionOffsets();
            }
            RemoveUnreachableBlocks(method);
        }

        private bool? TryEvaluateSimpleCondition(IList<Instruction> instructions, int branchIndex, Dictionary<int, object?> knownValues)
        {
            if (branchIndex < 2) return null;
            var prev1 = instructions[branchIndex - 1];
            var prev2 = instructions[branchIndex - 2];

            if ((prev1.OpCode.Code == Code.Ceq || prev1.OpCode.Code == Code.Cgt || prev1.OpCode.Code == Code.Clt))
            {
                if (branchIndex < 3) return null;
                var ldc = instructions[branchIndex - 3];
                var ldloc = (branchIndex >= 4) ? instructions[branchIndex - 4] : null;

                if (ldloc != null && IsLdloc(ldloc, out int localIdx))
                {
                    object? val1 = knownValues.ContainsKey(localIdx) ? knownValues[localIdx] : null;
                    object? val2 = GetConstantValue(ldc);

                    if (val1 != null && val2 != null)
                        return CompareValues(val1, val2, prev1.OpCode.Code);
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
                    case Code.Cgt: case Code.Cgt_Un: return dActual > dExpected;
                    case Code.Clt: case Code.Clt_Un: return dActual < dExpected;
                }
            } catch { }
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
                        if (reachable.Add(next)) queue.Enqueue(next);
                    }
                }

                if (curr.Operand is Instruction target && reachable.Add(target)) queue.Enqueue(target);
                else if (curr.Operand is Instruction[] targets)
                {
                    foreach (var t in targets) if (reachable.Add(t)) queue.Enqueue(t);
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
            if (changed) method.Body.UpdateInstructionOffsets();
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
                        bool isTarget = instructions.Any(ins => ins.Operand == instructions[i] || 
                                           (ins.Operand is Instruction[] arr && arr.Contains(instructions[i])));
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
                   instr.OpCode.Code >= Code.Stloc_0 && instr.OpCode.Code <= Code.Stloc_3);
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
                   instr.OpCode.Code >= Code.Ldloc_0 && instr.OpCode.Code <= Code.Ldloc_3);
        }

        private object? GetConstantValue(Instruction instr)
        {
            switch (instr.OpCode.Code)
            {
                case Code.Ldc_I4: return instr.Operand as int?;
                case Code.Ldc_I4_0: return 0; case Code.Ldc_I4_1: return 1;
                case Code.Ldc_I4_2: return 2; case Code.Ldc_I4_3: return 3;
                case Code.Ldc_I4_4: return 4; case Code.Ldc_I4_5: return 5;
                case Code.Ldc_I4_6: return 6; case Code.Ldc_I4_7: return 7;
                case Code.Ldc_I4_8: return 8; case Code.Ldc_I4_M1: return -1;
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
            } catch { }
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
            if (operand is MethodDef m) return Instruction.Create(opCode, m);
            if (operand is FieldDef field) return Instruction.Create(opCode, field);
            if (operand is MemberRef memberRef) return Instruction.Create(opCode, memberRef);
            
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
            _aiAssistant?.Dispose();
            _module.Dispose();
        }
    }
}
