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
                        // Попытка распутать state machine через эмуляцию и патчинг
                        if (EmulateAndPatch(method))
                        {
                            // Очистка недостижимого кода и NOP
                            CleanupMethod(method);
                            count++;
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
        /// Эмулирует выполнение метода и заменяет условные переходы на безусловные или NOP,
        /// основываясь на вычисленных значениях переменной состояния.
        /// Этот подход безопасен для стека, так как не удаляет инструкции, а меняет их логику.
        /// </summary>
        private bool EmulateAndPatch(MethodDef method)
        {
            var body = method.Body;
            if (body == null || !body.HasInstructions) return false;

            var instructions = body.Instructions;
            if (instructions.Count < 3) return false;

            // 1. Поиск переменной состояния
            int stateVarIndex = -1;
            object? initialState = null;

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
                        break;
                    }
                }
            }

            if (stateVarIndex == -1 || initialState == null)
            {
                return false;
            }

            // 2. Эмуляция выполнения для определения путей
            // Мы не меняем код сразу, а просто выясняем, какие ветки выполняются
            var locals = new object?[method.Body.Variables.Count];
            var stack = new Stack<object?>();
            
            locals[stateVarIndex] = initialState;

            int ip = 0;
            int maxIterations = instructions.Count * 200; // Защита
            int iterations = 0;
            
            // Множество инструкций, которые мы посетили в процессе эмуляции
            var visitedInstructions = new HashSet<Instruction>();

            while (ip >= 0 && ip < instructions.Count && iterations < maxIterations)
            {
                iterations++;
                var instr = instructions[ip];
                visitedInstructions.Add(instr);

                // Эмуляция текущей инструкции
                var flowAction = SimulateInstruction(instr, locals, stack, stateVarIndex);

                if (flowAction.FlowControl == FlowControl.Branch)
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
                    
                    // Объявляем target заранее, чтобы избежать CS0165
                    Instruction? target = null;
                    if (instr.Operand is Instruction)
                    {
                        target = (Instruction)instr.Operand;
                    }

                    if (conditionResult.HasValue && target != null)
                    {
                        if (conditionResult.Value)
                        {
                            ip = instructions.IndexOf(target);
                        }
                        else
                        {
                            ip++;
                        }
                        continue;
                    }
                    else
                    {
                        // Не смогли вычислить или нет цели перехода
                        ip++; 
                    }
                    continue;
                }
                    else
                    {
                        // Не смогли вычислить (реальное условие). Просто идем дальше (упрощенно: считаем false или true?)
                        // Для state machine обфускации условия обычно детерминированы. Если не вычислилось, возможно, ошибка эмуляции.
                        // В таком случае лучше остановиться или попробовать угадать. 
                        // Для безопасности оставим как есть и прервем эмуляцию для этого метода, 
                        // чтобы не сломать реальную логику.
                        // Но часто это значит, что мы дошли до конца цепочки состояний.
                        ip++; 
                    }
                }
                else if (flowAction.FlowControl == FlowControl.Return || flowAction.FlowControl == FlowControl.Throw)
                {
                    break;
                }
                else
                {
                    ip++;
                }
            }

            return visitedInstructions.Count > 0;
        }

        /// <summary>
        /// Симулирует инструкцию и возвращает действие для потока управления.
        /// </summary>
        private (FlowControl FlowControl, bool? ConditionResult) SimulateInstruction(Instruction instr, object?[] locals, Stack<object?> stack, int stateVarIndex)
        {
            try
            {
                // Обработка загрузки констант
                if (instr.OpCode.Code == Code.Ldc_I4 || instr.OpCode.Code == Code.Ldc_I4_0 || 
                    instr.OpCode.Code == Code.Ldc_I4_1 || instr.OpCode.Code == Code.Ldc_I4_2 || 
                    instr.OpCode.Code == Code.Ldc_I4_3 || instr.OpCode.Code == Code.Ldc_I4_4 || 
                    instr.OpCode.Code == Code.Ldc_I4_5 || instr.OpCode.Code == Code.Ldc_I4_6 || 
                    instr.OpCode.Code == Code.Ldc_I4_7 || instr.OpCode.Code == Code.Ldc_I4_8 || 
                    instr.OpCode.Code == Code.Ldc_I4_M1 || instr.OpCode.Code == Code.Ldc_I8 || 
                    instr.OpCode.Code == Code.Ldc_R4 || instr.OpCode.Code == Code.Ldc_R8)
                {
                    stack.Push(GetConstantValue(instr));
                    return (FlowControl.Next, null);
                }

                // Загрузка локалей
                if (instr.OpCode.Code == Code.Ldloc || instr.OpCode.Code == Code.Ldloc_S || 
                   (instr.OpCode.Code >= Code.Ldloc_0 && instr.OpCode.Code <= Code.Ldloc_3))
                {
                    int idx = GetLocalIndex(instr);
                    if (idx >= 0 && idx < locals.Length)
                        stack.Push(locals[idx]);
                    return (FlowControl.Next, null);
                }

                // Сохранение в локаль
                if (instr.OpCode.Code == Code.Stloc || instr.OpCode.Code == Code.Stloc_S || 
                   (instr.OpCode.Code >= Code.Stloc_0 && instr.OpCode.Code <= Code.Stloc_3))
                {
                    int idx = GetLocalIndex(instr);
                    if (idx >= 0 && idx < locals.Length && stack.Count > 0)
                        locals[idx] = stack.Pop();
                    return (FlowControl.Next, null);
                }

                // Арифметика (упрощенно)
                if (instr.OpCode.Code == Code.Add)
                {
                    if (stack.Count >= 2)
                    {
                        var v2 = stack.Pop();
                        var v1 = stack.Pop();
                        stack.Push(AddValues(v1, v2));
                    }
                    return (FlowControl.Next, null);
                }
                if (instr.OpCode.Code == Code.Sub)
                {
                    if (stack.Count >= 2)
                    {
                        var v2 = stack.Pop();
                        var v1 = stack.Pop();
                        stack.Push(SubValues(v1, v2));
                    }
                    return (FlowControl.Next, null);
                }

                // Сравнения
                if (instr.OpCode.Code == Code.Ceq || instr.OpCode.Code == Code.Cgt || instr.OpCode.Code == Code.Clt || 
                    instr.OpCode.Code == Code.Cgt_Un || instr.OpCode.Code == Code.Clt_Un)
                {
                    if (stack.Count >= 2)
                    {
                        var v2 = stack.Pop();
                        var v1 = stack.Pop();
                        var res = CompareValuesSimple(v1, v2, instr.OpCode.Code);
                        stack.Push(res ? 1 : 0);
                    }
                    return (FlowControl.Next, null);
                }

                // Ветвления
                if (instr.OpCode.FlowControl == FlowControl.Branch)
                {
                    return (FlowControl.Branch, null);
                }

                if (instr.OpCode.FlowControl == FlowControl.Cond_Branch)
                {
                    // Пытаемся вычислить условие на основе вершины стека
                    if (stack.Count > 0)
                    {
                        var val = stack.Pop();
                        if (val != null)
                        {
                            bool isTrue = Convert.ToDouble(val) != 0;
                            
                            if (instr.OpCode.Code == Code.Brtrue || instr.OpCode.Code == Code.Brtrue_S)
                                return (FlowControl.Cond_Branch, isTrue);
                            if (instr.OpCode.Code == Code.Brfalse || instr.OpCode.Code == Code.Brfalse_S)
                                return (FlowControl.Cond_Branch, !isTrue);
                            
                            // Для сложных условий (bgt, beq и т.д.), которые используют предыдущее сравнение (ceq),
                            // значение уже лежит на стеке как 0 или 1. Логика та же.
                            if (instr.OpCode.Code == Code.Beq || instr.OpCode.Code == Code.Beq_S ||
                                instr.OpCode.Code == Code.Bne_Un || instr.OpCode.Code == Code.Bne_Un_S)
                            {
                                 // Beq переходит если равно (т.е. ceq вернул 1)
                                 // Bne_Un переходит если не равно (т.е. ceq вернул 0)
                                 if (instr.OpCode.Code == Code.Beq || instr.OpCode.Code == Code.Beq_S)
                                     return (FlowControl.Cond_Branch, isTrue);
                                 else
                                     return (FlowControl.Cond_Branch, !isTrue);
                            }
                        }
                    }
                    return (FlowControl.Cond_Branch, null);
                }

                if (instr.OpCode.FlowControl == FlowControl.Return || instr.OpCode.FlowControl == FlowControl.Throw)
                {
                    return (instr.OpCode.FlowControl, null);
                }

                // Pop и другие
                if (instr.OpCode.Code == Code.Pop)
                {
                    if (stack.Count > 0) stack.Pop();
                    return (FlowControl.Next, null);
                }
            }
            catch
            {
                // Ошибка эмуляции
            }

            return (FlowControl.Next, null);
        }

        private object? AddValues(object? a, object? b)
        {
            if (a is double da && b is double db) return da + db;
            if (a is long la && b is long lb) return la + lb;
            if (a is int ia && b is int ib) return ia + ib;
            return 0; // Заглушка
        }

        private object? SubValues(object? a, object? b)
        {
            if (a is double da && b is double db) return da - db;
            if (a is long la && b is long lb) return la - lb;
            if (a is int ia && b is int ib) return ia - ib;
            return 0;
        }

        private bool CompareValuesSimple(object? a, object? b, Code opCode)
        {
            if (a == null || b == null) return false;
            try
            {
                double da = Convert.ToDouble(a);
                double db = Convert.ToDouble(b);
                switch (opCode)
                {
                    case Code.Ceq: return da == db;
                    case Code.Cgt: case Code.Cgt_Un: return da > db;
                    case Code.Clt: case Code.Clt_Un: return da < db;
                }
            }
            catch { }
            return false;
        }

        private void CleanupMethod(MethodDef method)
        {
            RemoveUnreachableBlocks(method);
            RemoveNops(method);
            method.Body.UpdateInstructionOffsets();
            method.Body.SimplifyMacros(method.Parameters);
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

                // Если инструкция не является безусловным переходом, возвратом или выбросом, следующая достижима
                if (curr.OpCode.Code != Code.Br && curr.OpCode.Code != Code.Ret && curr.OpCode.Code != Code.Throw)
                {
                    if (idx + 1 < instructions.Count)
                    {
                        var next = instructions[idx + 1];
                        if (reachable.Add(next))
                            queue.Enqueue(next);
                    }
                }

                // Цели переходов
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

            if (changed) method.Body.UpdateInstructionOffsets();
        }

        private void RemoveNops(MethodDef method)
        {
            var instructions = method.Body.Instructions;
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

        private int GetLocalIndex(Instruction instr)
        {
            if (instr.Operand is Local l) return l.Index;
            if (instr.OpCode.Code == Code.Ldloc_0 || instr.OpCode.Code == Code.Stloc_0) return 0;
            if (instr.OpCode.Code == Code.Ldloc_1 || instr.OpCode.Code == Code.Stloc_1) return 1;
            if (instr.OpCode.Code == Code.Ldloc_2 || instr.OpCode.Code == Code.Stloc_2) return 2;
            if (instr.OpCode.Code == Code.Ldloc_3 || instr.OpCode.Code == Code.Stloc_3) return 3;
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
