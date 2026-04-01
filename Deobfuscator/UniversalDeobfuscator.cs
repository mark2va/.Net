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
                        // Пытаемся распутать state machine
                        if (UnravelStateMachine(method))
                        {
                            // Если удалось, делаем финальную уборку
                            CleanupNops(method);
                            RemoveUnreachableBlocks(method);
                            count++;
                            Console.WriteLine($"[+] Unraveled: {method.FullName}");
                        }
                        else
                        {
                            // Если не получилось полностью, пробуем хотя бы упростить условия
                            SimplifyConditions(method);
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
        /// Основной метод распутывания State Machine.
        /// Эмулирует выполнение, игнорируя мусорные переходы и условия.
        /// </summary>
        private bool UnravelStateMachine(MethodDef method)
        {
            var body = method.Body;
            if (body == null || body.Instructions.Count < 5) return false;

            var instructions = body.Instructions;
            
            // 1. Поиск переменной состояния (обычно в начале метода)
            int stateVarIndex = -1;
            object? initialState = null;

            // Ищем паттерн: ldc.X -> stloc.X
            for (int i = 0; i < Math.Min(10, instructions.Count - 1); i++)
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

            if (stateVarIndex == -1 || initialState == null)
            {
                // Если нет явной переменной состояния, возможно это другой тип обфускации
                return false;
            }

            // 2. Эмуляция выполнения для построения линейного списка инструкций
            var realCode = new List<Instruction>();
            var visitedStates = new HashSet<object?>(); // Чтобы избежать бесконечных циклов в эмуляции
            
            // Стек для эмуляции (упрощенный)
            var stack = new Stack<object?>();
            // Локальные переменные
            var locals = new object?[body.Variables.Count];
            locals[stateVarIndex] = initialState;

            int ip = 0; // Индекс текущей инструкции
            int maxSteps = instructions.Count * 50; // Лимит шагов
            int steps = 0;

            while (ip >= 0 && ip < instructions.Count && steps < maxSteps)
            {
                steps++;
                var instr = instructions[ip];

                // Проверка на зацикливание эмуляции (если мы вернулись в то же состояние переменной)
                if (instr.OpCode.FlowControl == FlowControl.Branch || instr.OpCode.FlowControl == FlowControl.Cond_Branch)
                {
                     // Если это переход, проверяем, не ходили ли мы уже по этому пути с таким же состоянием
                     // Для простых state machine достаточно проверить, не повторяется ли значение stateVar
                     if (visitedStates.Contains(locals[stateVarIndex]))
                     {
                         // Скорее всего, мы замкнули цикл обфускации или дошли до конца
                         // Проверяем, не является ли это реальным циклом (например, while(true))
                         // В обфускации обычно после раскрутки циклов не остается, кроме реальных логиных циклов.
                         // Если мы уже были здесь, выходим.
                         break; 
                     }
                     if (locals[stateVarIndex] != null)
                         visitedStates.Add(locals[stateVarIndex]);
                }

                // Пропускаем инструкции, которые только меняют переменную состояния (мусор)
                bool isStateUpdate = false;
                if (IsStloc(instr, out int sIdx) && sIdx == stateVarIndex)
                {
                    // Это запись в переменную состояния. 
                    // Если значение берется из константы или простой арифметики - это мусорный переход.
                    // Но нам нужно сначала вычислить новое значение, чтобы знать куда идти дальше.
                    isStateUpdate = true;
                }
                
                // Если это не критическая инструкция управления потоком и не мусорное обновление стейта - добавляем в результат
                // Мы НЕ добавляем_ldc/stloc переменной состояния в финальный код, так как они нужны только для обфускации
                if (!isStateUpdate && 
                    instr.OpCode.Code != Code.Ldloc && GetLocalIndex(instr) != stateVarIndex && // Чтение стейта - мусор
                    instr.OpCode.Code != Code.Ldc_I4 && instr.OpCode.Code != Code.Ldc_I8 && instr.OpCode.Code != Code.Ldc_R4 && instr.OpCode.Code != Code.Ldc_R8) 
                {
                    // Добавляем инструкцию, если она не является частью механизма обфускации
                    // Исключение: если это реальная логика, использующая локаль, которая случайно совпадает с индексом стейта?
                    // В обфускации стейт-переменная обычно изолирована.
                    
                    // Более надежный фильтр: добавляем всё, кроме явных команд обновления стейта и сравнений с ним?
                    // Нет, лучше добавлять всё, а потом чистить unreachable.
                    // Но команды ldloc/stloc самой переменной num точно можно выкинуть.
                    
                    if (!(IsLdloc(instr, out int lIdx) && lIdx == stateVarIndex))
                    {
                         realCode.Add(CloneInstruction(instr));
                    }
                }

                // --- ЭМУЛЯЦИЯ ЛОГИКИ ПЕРЕХОДОВ ---

                // Обновляем стек/локали для эмуляции
                SimulateInstruction(instr, locals, stack, stateVarIndex);

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
                    bool? condition = EvaluateCondition(method, ip, locals, stack, stateVarIndex);
                    
                    if (condition.HasValue)
                    {
                        if (instr.Operand is Instruction target)
                        {
                            if (condition.Value)
                                ip = instructions.IndexOf(target);
                            else
                                ip++;
                        }
                        else
                        {
                            ip++;
                        }
                        continue;
                    }
                    else
                    {
                        // Не смогли вычислить (реальное условие?) - выходим из эмуляции, оставляем как есть
                        // Но в state machine обфускации условия обычно зависят от num, который мы знаем.
                        // Если не вычислилось, значит что-то пошло не так, прерываем.
                        break;
                    }
                }

                if (instr.OpCode.FlowControl == FlowControl.Return || 
                    instr.OpCode.FlowControl == FlowControl.Throw)
                {
                    break; // Конец метода
                }

                ip++;
            }

            if (realCode.Count == 0 || realCode.Count > instructions.Count * 2) 
                return false; // Что-то пошло не так

            // Заменяем тело метода
            ReplaceMethodBody(method, realCode);
            return true;
        }

        /// <summary>
        /// Упрощает условия, заменяя их на br/nop, если условие можно вычислить statically.
        /// Используется как запасной вариант.
        /// </summary>
        private void SimplifyConditions(MethodDef method)
        {
            var body = method.Body;
            var instructions = body.Instructions;
            bool changed = true;
            int passes = 0;

            while (changed && passes < 20)
            {
                changed = false;
                passes++;
                
                // Отслеживаем известные значения локалей
                var knownValues = new Dictionary<int, object?>();
                
                // Предварительный проход для сбора констант
                for (int i = 0; i < instructions.Count; i++)
                {
                    var instr = instructions[i];
                    if (i + 1 < instructions.Count && IsStloc(instructions[i+1], out int idx))
                    {
                        var val = GetConstantValue(instr);
                        if (val != null) knownValues[idx] = val;
                    }
                }

                for (int i = 0; i < instructions.Count; i++)
                {
                    var instr = instructions[i];
                    if (instr.OpCode.FlowControl == FlowControl.Cond_Branch)
                    {
                        bool? res = TryEvalSimpleCondition(instructions, i, knownValues);
                        if (res.HasValue)
                        {
                            if (instr.Operand is Instruction target)
                            {
                                if (res.Value)
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
        }

        private bool? TryEvalSimpleCondition(IList<Instruction> instrs, int idx, Dictionary<int, object?> known)
        {
            if (idx < 2) return null;
            var branch = instrs[idx];
            var prev1 = instrs[idx - 1]; // ceq, cgt...
            var prev2 = instrs[idx - 2]; // ldc
            var prev3 = (idx >= 3) ? instrs[idx - 3] : null; // ldloc

            if ((prev1.OpCode.Code == Code.Ceq || prev1.OpCode.Code == Code.Cgt || prev1.OpCode.Code == Code.Clt))
            {
                if (prev3 != null && IsLdloc(prev3, out int lIdx) && GetConstantValue(prev2) is object val2)
                {
                    if (known.ContainsKey(lIdx))
                    {
                        return CompareValues(known[lIdx], val2, prev1.OpCode.Code);
                    }
                }
            }
            
            // Brtrue/Brfalse
            if (branch.OpCode.Code == Code.Brtrue || branch.OpCode.Code == Code.Brfalse || 
                branch.OpCode.Code == Code.Brtrue_S || branch.OpCode.Code == Code.Brfalse_S)
            {
                if (prev2 != null && IsLdloc(prev2, out int lIdx) && known.ContainsKey(lIdx))
                {
                    var val = known[lIdx];
                    bool isZero = Convert.ToDouble(val) == 0;
                    return (branch.OpCode.Code == Code.Brtrue || branch.OpCode.Code == Code.Brtrue_S) ? !isZero : isZero;
                }
            }

            return null;
        }

        private bool? EvaluateCondition(MethodDef method, int ip, object?[] locals, Stack<object?> stack, int stateVarIndex)
        {
            var instrs = method.Body.Instructions;
            var branch = instrs[ip];
            
            // Смотрим назад на 2-3 инструкции
            if (ip < 2) return null;
            var prev1 = instrs[ip - 1];
            var prev2 = instrs[ip - 2];
            var prev3 = (ip >= 3) ? instrs[ip - 3] : null;

            // Паттерн: ldloc(state), ldc(val), ceq, br...
            if ((prev1.OpCode.Code == Code.Ceq || prev1.OpCode.Code == Code.Cgt || prev1.OpCode.Code == Code.Clt))
            {
                if (prev3 != null && IsLdloc(prev3, out int lIdx) && lIdx == stateVarIndex)
                {
                    var constVal = GetConstantValue(prev2);
                    var stateVal = locals[stateVarIndex];
                    if (constVal != null && stateVal != null)
                    {
                        return CompareValues(stateVal, constVal, prev1.OpCode.Code);
                    }
                }
            }

            // Паттерн: ldloc(state), brtrue...
            if (branch.OpCode.Code == Code.Brtrue || branch.OpCode.Code == Code.Brtrue_S ||
                branch.OpCode.Code == Code.Brfalse || branch.OpCode.Code == Code.Brfalse_S)
            {
                if (prev2 != null && IsLdloc(prev2, out int lIdx) && lIdx == stateVarIndex)
                {
                    var val = locals[stateVarIndex];
                    if (val != null)
                    {
                        bool isZero = Convert.ToDouble(val) == 0;
                        return (branch.OpCode.Code == Code.Brtrue || branch.OpCode.Code == Code.Brtrue_S) ? !isZero : isZero;
                    }
                }
            }

            return null;
        }

        private bool? CompareValues(object? a, object? b, Code op)
        {
            if (a == null || b == null) return null;
            try
            {
                double da = Convert.ToDouble(a);
                double db = Convert.ToDouble(b);
                switch (op)
                {
                    case Code.Ceq: return da == db;
                    case Code.Cgt: case Code.Cgt_Un: return da > db;
                    case Code.Clt: case Code.Clt_Un: return da < db;
                }
            } catch { }
            return null;
        }

        private void SimulateInstruction(Instruction instr, object?[] locals, Stack<object?> stack, int stateVarIndex)
        {
            try
            {
                switch (instr.OpCode.Code)
                {
                    case Code.Ldc_I4: case Code.Ldc_I4_0: case Code.Ldc_I4_1: case Code.Ldc_I4_2: 
                    case Code.Ldc_I4_3: case Code.Ldc_I4_4: case Code.Ldc_I4_5: case Code.Ldc_I4_6: 
                    case Code.Ldc_I4_7: case Code.Ldc_I4_8: case Code.Ldc_I4_M1:
                    case Code.Ldc_I8: case Code.Ldc_R4: case Code.Ldc_R8:
                        stack.Push(GetConstantValue(instr));
                        break;
                    case Code.Ldloc: case Code.Ldloc_S: case Code.Ldloc_0: case Code.Ldloc_1: case Code.Ldloc_2: case Code.Ldloc_3:
                        int li = GetLocalIndex(instr);
                        if (li >= 0 && li < locals.Length) stack.Push(locals[li]);
                        break;
                    case Code.Stloc: case Code.Stloc_S: case Code.Stloc_0: case Code.Stloc_1: case Code.Stloc_2: case Code.Stloc_3:
                        int si = GetLocalIndex(instr);
                        if (si >= 0 && si < locals.Length && stack.Count > 0)
                        {
                            var val = stack.Pop();
                            locals[si] = val;
                            // Если это обновление переменной состояния, помечаем, что состояние изменилось (для внешней логики)
                        }
                        break;
                    case Code.Add:
                        if (stack.Count >= 2) { var b = stack.Pop(); var a = stack.Pop(); stack.Push(Add(a,b)); }
                        break;
                    case Code.Sub:
                        if (stack.Count >= 2) { var b = stack.Pop(); var a = stack.Pop(); stack.Push(Sub(a,b)); }
                        break;
                }
            } catch { }
        }

        private object? Add(object? a, object? b)
        {
            if (a is double da && b is double db) return da + db;
            if (a is long la && b is long lb) return la + lb;
            if (a is int ia && b is int ib) return ia + ib;
            return null;
        }
        private object? Sub(object? a, object? b)
        {
            if (a is double da && b is double db) return da - db;
            if (a is long la && b is long lb) return la - lb;
            if (a is int ia && b is int ib) return ia - ib;
            return null;
        }

        private void RemoveUnreachableBlocks(MethodDef method)
        {
            var body = method.Body;
            var instrs = body.Instructions;
            if (instrs.Count == 0) return;

            var reachable = new HashSet<Instruction>();
            var q = new Queue<Instruction>();
            
            q.Enqueue(instrs[0]);
            reachable.Add(instrs[0]);

            while (q.Count > 0)
            {
                var curr = q.Dequeue();
                int idx = instrs.IndexOf(curr);
                if (idx == -1) continue;

                // Если не безусловный переход и не возврат, идем дальше
                if (curr.OpCode.Code != Code.Br && curr.OpCode.Code != Code.Br_S && 
                    curr.OpCode.Code != Code.Ret && curr.OpCode.Code != Code.Throw)
                {
                    if (idx + 1 < instrs.Count)
                    {
                        var next = instrs[idx + 1];
                        if (reachable.Add(next)) q.Enqueue(next);
                    }
                }

                if (curr.Operand is Instruction t)
                {
                    if (reachable.Add(t)) q.Enqueue(t);
                }
                else if (curr.Operand is Instruction[] ts)
                {
                    foreach (var x in ts) if (reachable.Add(x)) q.Enqueue(x);
                }
            }

            bool changed = false;
            for (int i = instrs.Count - 1; i >= 0; i--)
            {
                if (!reachable.Contains(instrs[i]))
                {
                    instrs[i].OpCode = OpCodes.Nop;
                    instrs[i].Operand = null;
                    changed = true;
                }
            }
            if (changed) body.UpdateInstructionOffsets();
        }

        private void CleanupNops(MethodDef method)
        {
            var body = method.Body;
            var instrs = body.Instructions;
            bool changed = true;
            while (changed)
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
                case Code.Ldc_I4_0: return 0; case Code.Ldc_I4_1: return 1; case Code.Ldc_I4_2: return 2;
                case Code.Ldc_I4_3: return 3; case Code.Ldc_I4_4: return 4; case Code.Ldc_I4_5: return 5;
                case Code.Ldc_I4_6: return 6; case Code.Ldc_I4_7: return 7; case Code.Ldc_I4_8: return 8;
                case Code.Ldc_I4_M1: return -1;
                case Code.Ldc_I8: return i.Operand as long?;
                case Code.Ldc_R4: return i.Operand as float?;
                case Code.Ldc_R8: return i.Operand as double?;
                default: return null;
            }
        }

        private bool IsObfuscatedName(string n) => !string.IsNullOrEmpty(n) && (n.Length == 1 || n.StartsWith("?"));

        private void RenameWithAi(MethodDef m)
        {
            if (_aiAssistant == null) return;
            try
            {
                var snip = string.Join("\n", m.Body.Instructions.Take(15).Select(x => x.ToString()));
                var newName = _aiAssistant.GetSuggestedName(m.Name, snip, m.ReturnType?.ToString());
                if (!string.IsNullOrEmpty(newName) && IsValidIdentifier(newName))
                {
                    m.Name = newName;
                    Console.WriteLine($"[AI] Renamed {m.Name}");
                }
            } catch { }
        }

        private bool IsValidIdentifier(string n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            if (!char.IsLetter(n[0]) && n[0] != '_') return false;
            foreach (var c in n) if (!char.IsLetterOrDigit(c) && c != '_') return false;
            return true;
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
            if (operand is long l) return Instruction.Create(op, l);
            if (operand is float f) return Instruction.Create(op, f);
            if (operand is double d) return Instruction.Create(op, d);
            if (operand is ITypeDefOrRef t) return Instruction.Create(op, t);
            if (operand is MethodDef m) return Instruction.Create(op, m);
            if (operand is FieldDef f) return Instruction.Create(op, f);
            if (operand is MemberRef mr) return Instruction.Create(op, mr);
            return new Instruction(op, operand);
        }

        private void ReplaceMethodBody(MethodDef m, List<Instruction> newInstrs)
        {
            var body = m.Body;
            body.Instructions.Clear();
            foreach (var i in newInstrs) body.Instructions.Add(i);
            body.UpdateInstructionOffsets();
            body.SimplifyMacros(m.Parameters);
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
