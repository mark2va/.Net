using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Deobfuscator
{
    public class UniversalDeobfuscator : IDisposable
    {
        private readonly ModuleDefMD _module;
        private readonly AiAssistant? _aiAssistant;
        private readonly Dictionary<Local, object?> _constantLocals = new Dictionary<Local, object?>();

        public UniversalDeobfuscator(string filePath, AiConfig aiConfig)
        {
            _module = ModuleDefMD.Load(filePath);
            _aiAssistant = aiConfig.Enabled ? new AiAssistant(aiConfig) : null;
        }

        public void Deobfuscate()
        {
            Console.WriteLine("[*] Starting deobfuscation...");
            
            foreach (var type in _module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody || !method.Body.HasInstructions) continue;

                    Console.WriteLine($"[*] Processing: {method.FullName}");
                    
                    // 1. Сбор констант
                    AnalyzeConstants(method);

                    // 2. Упрощение условий
                    SimplifyConstantConditions(method);

                    // 3. Распутывание потоков (Goto, циклы)
                    UnravelControlFlow(method);

                    // 4. Удаление мертвого кода
                    RemoveDeadCode(method);

                    // 5. AI Переименование (если включено)
                    if (_aiAssistant != null && method.Name.StartsWith("<"))
                    {
                        RenameWithAi(method);
                    }
                }
            }
            
            Console.WriteLine("[*] Deobfuscation complete.");
        }

        // Вспомогательный метод для получения следующей инструкции
        private Instruction? GetNextInstruction(IList<Instruction> instructions, Instruction current)
        {
            for (int i = 0; i < instructions.Count; i++)
            {
                if (instructions[i] == current && i < instructions.Count - 1)
                {
                    return instructions[i + 1];
                }
            }
            return null;
        }

        // Вспомогательный метод для получения предыдущей инструкции
        private Instruction? GetPreviousInstruction(IList<Instruction> instructions, Instruction current)
        {
            for (int i = 0; i < instructions.Count; i++)
            {
                if (instructions[i] == current && i > 0)
                {
                    return instructions[i - 1];
                }
            }
            return null;
        }

        private void AnalyzeConstants(MethodDef method)
        {
            _constantLocals.Clear();
            var instructions = method.Body.Instructions;

            foreach (var instr in instructions)
            {
                Local? local = null;

                // Обработка всех вариантов сохранения в локаль (stloc, stloc.s, stloc.0-3)
                if (instr.OpCode.Code == Code.Stloc)
                {
                    local = instr.Operand as Local;
                }
                else if (instr.OpCode.Code == Code.Stloc_S)
                {
                    local = instr.Operand as Local;
                }
                else if (instr.OpCode.Code >= Code.Stloc_0 && instr.OpCode.Code <= Code.Stloc_3)
                {
                    int index = instr.OpCode.Code - Code.Stloc_0;
                    if (index < method.Body.Variables.Count)
                        local = method.Body.Variables[index];
                }

                if (local != null)
                {
                    // Ищем предыдущую инструкцию загрузки константы
                    var prev = GetPreviousInstruction(instructions, instr);
                    if (prev != null)
                    {
                        object? val = GetConstantValue(prev);
                        if (val != null)
                            _constantLocals[local] = val;
                    }
                }
            }
        }

        private object? GetConstantValue(Instruction? instr)
        {
            if (instr == null) return null;
        
            switch (instr.OpCode.Code)
            {
                case Code.Ldc_I4:
                    // Классическая проверка типа и приведение
                    if (instr.Operand is int)
                        return (int)instr.Operand;
                    return null;
        
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
        
                case Code.Ldc_R4:
                    if (instr.Operand is float)
                        return (float)instr.Operand;
                    return null;
        
                case Code.Ldc_R8:
                    if (instr.Operand is double)
                        return (double)instr.Operand;
                    return null;
        
                case Code.Ldc_I8:
                    if (instr.Operand is long)
                        return (long)instr.Operand;
                    return null;
        
                default:
                    return null;
            }
        }

        private void SimplifyConstantConditions(MethodDef method)
        {
            // Здесь можно добавить логику замены ветвлений на основе _constantLocals
            // Для примера пока заглушка, так как полная реализация требует анализа стека
            Console.WriteLine($"[*] Found {_constantLocals.Count} constant locals in {method.Name}");
        }

        private void UnravelControlFlow(MethodDef method)
        {
            bool changed = true;
            while (changed)
            {
                changed = false;
                var instructions = method.Body.Instructions;

                for (int i = 0; i < instructions.Count; i++)
                {
                    var instr = instructions[i];

                    // Удаление цепочек goto: goto L1; L1: goto L2; -> goto L2;
                    if (instr.OpCode.Code == Code.Br && instr.Operand is Instruction target)
                    {
                        if (target.OpCode.Code == Code.Br && target.Operand is Instruction nextTarget)
                        {
                            instr.Operand = nextTarget;
                            changed = true;
                        }
                        else if (IsNopBlock(target, instructions))
                        {
                            // Пропуск блоков nop
                            var realTarget = FindRealTarget(target, instructions);
                            if (realTarget != null)
                            {
                                instr.Operand = realTarget;
                                changed = true;
                            }
                        }
                    }
                    
                    // Удаление безусловных переходов на следующую инструкцию
                    var nextInstruction = GetNextInstruction(instructions, instr);
                    if (instr.OpCode.Code == Code.Br && instr.Operand is Instruction nextInstr && nextInstr == nextInstruction)
                    {
                        instr.OpCode = OpCodes.Nop;
                        instr.Operand = null;
                        changed = true;
                    }
                }
                
                // Обновляем список инструкций после изменений (dnlib требует пересчета)
                if (changed)
                    method.Body.SimplifyMacros(method.Parameters);
            }
            
            // Распутывание кода с переменной состояния (state variable obfuscation)
            UnravelStateVariableFlow(method);
        }

        /// <summary>
        /// Распутывает код, обфусцированный с помощью переменной состояния.
        /// Пример паттерна:
        /// long num = 4586L;
        /// do {
        ///   if (num == 4603L) { ...; num = 4607L; }
        ///   if (num == 4607L) { ...; num = 4608L; }
        ///   if (num == 4586L) { num = 4603L; }
        /// } while (num != 4608L);
        /// </summary>
        private void UnravelStateVariableFlow(MethodDef method)
        {
            if (!method.Body.HasInstructions || !method.Body.HasVariables)
                return;

            var instructions = method.Body.Instructions;
            
            // Ищем переменную состояния (локаль, которая используется для управления потоком)
            Local? stateVar = FindStateVariable(method);
            if (stateVar == null)
                return;

            Console.WriteLine($"[*] Found state variable: {stateVar} in {method.Name}");

            // Извлекаем блоки кода, управляемые переменной состояния
            var stateBlocks = ExtractStateBlocks(method, stateVar);
            if (stateBlocks.Count == 0)
                return;

            Console.WriteLine($"[*] Extracted {stateBlocks.Count} state blocks");

            // Находим начальное значение и условие выхода
            var initialState = FindInitialState(instructions, stateVar);
            var exitStates = FindExitStates(method, stateVar);

            if (initialState == null || exitStates.Count == 0)
                return;

            Console.WriteLine($"[*] Initial state: {initialState}, Exit states: {string.Join(", ", exitStates)}");

            // Строим граф переходов
            var transitions = BuildTransitionGraph(stateBlocks, stateVar);

            // Генерируем новый линейный код
            var newInstructions = RebuildLinearCode(stateBlocks, transitions, initialState.Value, exitStates);
            
            if (newInstructions.Count > 0)
            {
                ReplaceMethodBody(method, newInstructions);
                Console.WriteLine($"[+] Unraveled state variable flow in {method.Name}");
            }
        }

        private Local? FindStateVariable(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            var candidates = new Dictionary<Local, int>();

            foreach (var instr in instructions)
            {
                // Ищем загрузки и сохранения локалей
                Local? local = null;
                
                if (instr.OpCode.Code == Code.Stloc || instr.OpCode.Code == Code.Stloc_S)
                    local = instr.Operand as Local;
                else if (instr.OpCode.Code >= Code.Stloc_0 && instr.OpCode.Code <= Code.Stloc_3)
                    local = method.Body.Variables[instr.OpCode.Code - Code.Stloc_0];
                
                if (instr.OpCode.Code == Code.Ldloc || instr.OpCode.Code == Code.Ldloc_S)
                    local = instr.Operand as Local;
                else if (instr.OpCode.Code >= Code.Ldloc_0 && instr.OpCode.Code <= Code.Ldloc_3)
                    local = method.Body.Variables[instr.OpCode.Code - Code.Ldloc_0];

                if (local != null)
                {
                    if (!candidates.ContainsKey(local))
                        candidates[local] = 0;
                    candidates[local]++;
                }
            }

            // Переменная состояния обычно используется многократно
            var sorted = candidates.OrderByDescending(x => x.Value).ToList();
            
            foreach (var kvp in sorted)
            {
                var local = kvp.Key;
                // Проверяем тип - должен быть числовым
                if (local.Type.IsPrimitive || local.Type.FullName == "System.Int64" || 
                    local.Type.FullName == "System.Double" || local.Type.FullName == "System.Int32")
                {
                    // Проверяем, используется ли она в сравнениях
                    if (IsUsedInComparisons(method, local))
                        return local;
                }
            }

            return null;
        }

        private bool IsUsedInComparisons(MethodDef method, Local local)
        {
            var instructions = method.Body.Instructions;
            int comparisonCount = 0;

            for (int i = 0; i < instructions.Count; i++)
            {
                var instr = instructions[i];
                
                // Проверка на сравнение (ceq, cgt, clt, bne.un, beq, etc.)
                if (instr.OpCode.Code == Code.Ceq || instr.OpCode.Code == Code.Cgt || 
                    instr.OpCode.Code == Code.Clt || instr.OpCode.Code == Code.Cgt_Un ||
                    instr.OpCode.Code == Code.Clt_Un)
                {
                    // Проверяем, была ли загружена наша переменная перед этим
                    if (i >= 2)
                    {
                        var prev1 = instructions[i - 1];
                        var prev2 = instructions[i - 2];
                        
                        if (IsLoadOfLocal(prev1, local, method) || IsLoadOfLocal(prev2, local, method))
                            comparisonCount++;
                    }
                }
                
                // Ветвления на основе значения
                if ((instr.OpCode.Code == Code.Bne_Un || instr.OpCode.Code == Code.Beq ||
                     instr.OpCode.Code == Code.Bgt || instr.OpCode.Code == Code.Blt ||
                     instr.OpCode.Code == Code.Bgt_Un || instr.OpCode.Code == Code.Blt_Un) &&
                    instr.Operand is Instruction)
                {
                    if (i >= 1 && IsLoadOfLocal(instructions[i - 1], local, method))
                        comparisonCount++;
                }
            }

            return comparisonCount >= 2;
        }

        private bool IsLoadOfLocal(Instruction instr, Local local, MethodDef method)
        {
            if (instr.OpCode.Code == Code.Ldloc || instr.OpCode.Code == Code.Ldloc_S)
                return Equals(instr.Operand as Local, local);
            if (instr.OpCode.Code >= Code.Ldloc_0 && instr.OpCode.Code <= Code.Ldloc_3)
            {
                int idx = instr.OpCode.Code - Code.Ldloc_0;
                return idx < method.Body.Variables.Count && Equals(method.Body.Variables[idx], local);
            }
            return false;
        }

        private class StateBlock
        {
            public long StateValue { get; set; }
            public List<Instruction> Instructions { get; set; } = new List<Instruction>();
            public long? NextState { get; set; }
            public Instruction? OriginalLabel { get; set; }
        }

        private Dictionary<long, StateBlock> ExtractStateBlocks(MethodDef method, Local stateVar)
        {
            var blocks = new Dictionary<long, StateBlock>();
            var instructions = method.Body.Instructions;

            for (int i = 0; i < instructions.Count; i++)
            {
                var instr = instructions[i];
                
                // Ищем паттерн: if (num == STATE) { ...; num = NEXT_STATE; }
                if (instr.OpCode.Code == Code.Bne_Un && instr.Operand is Instruction skipTarget)
                {
                    // Проверяем, было ли сравнение с константой
                    if (i >= 2 && instructions[i - 2].OpCode.Code == Code.Ldc_I8 &&
                        IsLoadOfLocal(instructions[i - 1], stateVar, method))
                    {
                        long stateValue = (long)instructions[i - 2].Operand!;
                        
                        // Собираем инструкции до перехода
                        var blockInstrs = new List<Instruction>();
                        int j = i + 1;
                        long? nextState = null;
                        
                        while (j < instructions.Count && instructions[j] != skipTarget)
                        {
                            var curr = instructions[j];
                            
                            // Проверяем на присваивание следующего состояния
                            if ((curr.OpCode.Code == Code.Stloc || curr.OpCode.Code == Code.Stloc_S) &&
                                Equals(curr.Operand as Local, stateVar) && j >= 1)
                            {
                                if (instructions[j - 1].OpCode.Code == Code.Ldc_I8)
                                    nextState = (long)instructions[j - 1].Operand!;
                                else if (instructions[j - 1].OpCode.Code == Code.Ldc_I4)
                                    nextState = Convert.ToInt64((int)instructions[j - 1].Operand!);
                                else if (instructions[j - 1].OpCode.Code == Code.Ldc_R8)
                                    nextState = Convert.ToInt64((double)instructions[j - 1].Operand!);
                                
                                j++;
                                break;
                            }
                            
                            blockInstrs.Add(curr);
                            j++;
                        }
                        
                        if (!blocks.ContainsKey(stateValue))
                        {
                            blocks[stateValue] = new StateBlock
                            {
                                StateValue = stateValue,
                                OriginalLabel = instructions[i - 2]
                            };
                        }
                        
                        blocks[stateValue].Instructions.AddRange(blockInstrs);
                        blocks[stateValue].NextState = nextState;
                    }
                }
            }

            return blocks;
        }

        private long? FindInitialState(IList<Instruction> instructions, Local stateVar)
        {
            // Ищем первую инициализацию переменной состояния: stloc X, ldc.i8/lcd.i4
            for (int i = 0; i < instructions.Count; i++)
            {
                var instr = instructions[i];
                
                if ((instr.OpCode.Code == Code.Stloc || instr.OpCode.Code == Code.Stloc_S) &&
                    Equals(instr.Operand as Local, stateVar) && i >= 1)
                {
                    var prev = instructions[i - 1];
                    if (prev.OpCode.Code == Code.Ldc_I8)
                        return (long)prev.Operand!;
                    if (prev.OpCode.Code == Code.Ldc_I4)
                        return Convert.ToInt64((int)prev.Operand!);
                    if (prev.OpCode.Code == Code.Ldc_R8)
                        return Convert.ToInt64((double)prev.Operand!);
                }
            }
            
            return null;
        }

        private List<long> FindExitStates(MethodDef method, Local stateVar)
        {
            var exitStates = new List<long>();
            var instructions = method.Body.Instructions;

            // Ищем условия выхода из цикла: while (num != EXIT_STATE)
            for (int i = 0; i < instructions.Count; i++)
            {
                var instr = instructions[i];
                
                if (instr.OpCode.Code == Code.Bne_Un && instr.Operand is Instruction loopEnd)
                {
                    // Проверяем, сравнивается ли переменная состояния с константой
                    if (i >= 2 && IsLoadOfLocal(instructions[i - 1], stateVar, method))
                    {
                        if (instructions[i - 2].OpCode.Code == Code.Ldc_I8)
                            exitStates.Add((long)instructions[i - 2].Operand!);
                        else if (instructions[i - 2].OpCode.Code == Code.Ldc_I4)
                            exitStates.Add(Convert.ToInt64((int)instructions[i - 2].Operand!));
                        else if (instructions[i - 2].OpCode.Code == Code.Ldc_R8)
                            exitStates.Add(Convert.ToInt64((double)instructions[i - 2].Operand!));
                    }
                }
            }

            return exitStates.Distinct().ToList();
        }

        private Dictionary<long, List<long>> BuildTransitionGraph(Dictionary<long, StateBlock> blocks, Local stateVar)
        {
            var graph = new Dictionary<long, List<long>>();
            
            foreach (var kvp in blocks)
            {
                var block = kvp.Value;
                if (block.NextState.HasValue)
                {
                    if (!graph.ContainsKey(kvp.Key))
                        graph[kvp.Key] = new List<long>();
                    
                    if (!graph[kvp.Key].Contains(block.NextState.Value))
                        graph[kvp.Key].Add(block.NextState.Value);
                }
            }
            
            return graph;
        }

        private List<Instruction> RebuildLinearCode(Dictionary<long, StateBlock> blocks, 
                                                     Dictionary<long, List<long>> transitions,
                                                     long initialState,
                                                     List<long> exitStates)
        {
            var newInstructions = new List<Instruction>();
            var visited = new HashSet<long>();
            var queue = new Queue<long>();
            
            queue.Enqueue(initialState);
            visited.Add(initialState);

            while (queue.Count > 0)
            {
                var currentState = queue.Dequeue();
                
                if (exitStates.Contains(currentState))
                    continue;
                
                if (!blocks.ContainsKey(currentState))
                    continue;
                
                var block = blocks[currentState];
                
                // Добавляем инструкции блока
                foreach (var instr in block.Instructions)
                {
                    newInstructions.Add(CloneInstruction(instr));
                }
                
                // Переходим к следующему состоянию
                if (block.NextState.HasValue && !exitStates.Contains(block.NextState.Value))
                {
                    var nextState = block.NextState.Value;
                    if (!visited.Contains(nextState) && blocks.ContainsKey(nextState))
                    {
                        visited.Add(nextState);
                        queue.Enqueue(nextState);
                    }
                }
            }

            return newInstructions;
        }

        private Instruction CloneInstruction(Instruction orig)
        {
            if (orig.Operand == null)
                return Instruction.Create(orig.OpCode);
            
            if (orig.Operand is Instruction target)
                return Instruction.Create(orig.OpCode, target);
            
            if (orig.Operand is Instruction[] targets)
                return Instruction.Create(orig.OpCode, targets);
            
            if (orig.Operand is Local local)
                return Instruction.Create(orig.OpCode, local);
            
            if (orig.Operand is Parameter param)
                return Instruction.Create(orig.OpCode, param);
            
            if (orig.Operand is FieldDef field)
                return Instruction.Create(orig.OpCode, field);
            
            if (orig.Operand is MethodDef meth)
                return Instruction.Create(orig.OpCode, meth);
            
            if (orig.Operand is TypeDef type)
                return Instruction.Create(orig.OpCode, type);
            
            if (orig.Operand is string s)
                return Instruction.Create(orig.OpCode, s);
            
            if (orig.Operand is int i)
                return Instruction.Create(orig.OpCode, i);
            
            if (orig.Operand is long l)
                return Instruction.Create(orig.OpCode, l);
            
            if (orig.Operand is float f)
                return Instruction.Create(orig.OpCode, f);
            
            if (orig.Operand is double d)
                return Instruction.Create(orig.OpCode, d);
            
            if (orig.Operand is byte b)
                return Instruction.Create(orig.OpCode, b);
            
            if (orig.Operand is short s16)
                return Instruction.Create(orig.OpCode, s16);
            
            if (orig.Operand is ushort us16)
                return Instruction.Create(orig.OpCode, us16);
            
            if (orig.Operand is uint ui)
                return Instruction.Create(orig.OpCode, ui);
            
            if (orig.Operand is ulong ul)
                return Instruction.Create(orig.OpCode, ul);
            
            if (orig.Operand is IMethod methodRef)
                return Instruction.Create(orig.OpCode, methodRef);
            
            if (orig.Operand is IField fieldRef)
                return Instruction.Create(orig.OpCode, fieldRef);
            
            if (orig.Operand is ITypeDefOrRef typeRef)
                return Instruction.Create(orig.OpCode, typeRef);
            
            if (orig.Operand is Parameter parameter)
                return Instruction.Create(orig.OpCode, parameter);
            
            if (orig.Operand is Local localVar)
                return Instruction.Create(orig.OpCode, localVar);
            
            if (orig.Operand is Instruction instr)
                return Instruction.Create(orig.OpCode, instr);
            
            if (orig.Operand is Instruction[] instrs)
                return Instruction.Create(orig.OpCode, instrs);
            
            // Fallback: создаем инструкцию с оригинальным операндом через конструктор
            return new Instruction(orig.OpCode, orig.Operand);
        }

        private void ReplaceMethodBody(MethodDef method, List<Instruction> newInstructions)
        {
            if (newInstructions.Count == 0)
                return;
            
            // Очищаем старые инструкции
            method.Body.Instructions.Clear();
            
            // Добавляем новые
            foreach (var instr in newInstructions)
            {
                method.Body.Instructions.Add(instr);
            }
            
            // Пересчитываем исключения и обработчики
            method.Body.ExceptionHandlers.Clear();
            
            // Оптимизируем
            method.Body.OptimizeMacros();
        }

        private bool IsNopBlock(Instruction instr, IList<Instruction> instructions)
        {
            int count = 0;
            var curr = instr;
            while (curr != null && curr.OpCode.Code == Code.Nop && count < 10)
            {
                curr = GetNextInstruction(instructions, curr);
                count++;
            }
            return count > 0 && curr != null;
        }

        private Instruction? FindRealTarget(Instruction start, IList<Instruction> instructions)
        {
            var curr = start;
            while (curr != null && curr.OpCode.Code == Code.Nop)
            {
                curr = GetNextInstruction(instructions, curr);
            }
            return curr;
        }

        private void RemoveDeadCode(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            for (int i = instructions.Count - 1; i >= 0; i--)
            {
                var instr = instructions[i];
                if (instr.OpCode.FlowControl == FlowControl.Return || instr.OpCode.FlowControl == FlowControl.Throw)
                {
                    for (int j = i + 1; j < instructions.Count; j++)
                    {
                        if (!IsBranchTarget(instructions[j], instructions))
                        {
                            instructions[j].OpCode = OpCodes.Nop;
                            instructions[j].Operand = null;
                        }
                    }
                    break; 
                }
            }
        }

        private bool IsBranchTarget(Instruction instr, IList<Instruction> all)
        {
            foreach (var i in all)
            {
                if (i.Operand == instr && (i.OpCode.FlowControl == FlowControl.Cond_Branch || i.OpCode.Code == Code.Br || i.OpCode.Code == Code.Switch))
                    return true;
            }
            return false;
        }

        private void RenameWithAi(MethodDef method)
        {
            if (_aiAssistant == null) return;

            try
            {
                string ilCode = string.Join("\n", method.Body.Instructions.Take(20).Select(x => x.ToString()));
                string suggested = _aiAssistant.GetSuggestedName(method.Name, ilCode, method.ReturnType.ToString());
                
                if (!string.IsNullOrEmpty(suggested) && suggested != method.Name)
                {
                    method.Name = suggested;
                    Console.WriteLine($"[AI] Renamed {method.Name}");
                }
            }
            catch { }
        }

        public void Save(string outputPath)
        {
            _module.Write(outputPath);
            Console.WriteLine($"[+] Saved to: {outputPath}");
        }

        public void Dispose()
        {
            _aiAssistant?.Dispose();
            _module.Dispose();
        }
    }
}
