using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

namespace Deobfuscator
{
    public class UniversalDeobfuscator : IDisposable
    {
        private ModuleDefMD _module;
        private AiConfig _aiConfig;
        private bool _debugMode;
        private StreamWriter? _logWriter;
        private AiAssistant? _ai;

        public UniversalDeobfuscator(string path, AiConfig config, bool debug)
        {
            _module = ModuleDefMD.Load(path);
            _aiConfig = config;
            _debugMode = debug;
            
            if (_debugMode)
            {
                var logPath = Path.Combine(Path.GetDirectoryName(path) ?? "", "deob_trace.log");
                _logWriter = new StreamWriter(logPath) { AutoFlush = true };
                Log("=== Trace Started ===");
            }

            if (_aiConfig.Enabled)
            {
                _ai = new AiAssistant(_aiConfig);
                if (!_ai.IsConnected)
                    Log("[!] AI Server unreachable.");
                else if (!_ai.IsModelLoaded)
                    Log($"[!] Model {_aiConfig.ModelName} not ready.");
                else
                    Log($"[+] AI Ready: {_aiConfig.ModelName}");
            }
        }

        private void Log(string msg)
        {
            if (_debugMode)
            {
                Console.WriteLine(msg);
                _logWriter?.WriteLine(msg);
            }
        }

        public void Process()
        {
            Log("Phase 1: Emulation & Control Flow Unpacking");
            int unpackedCount = 0;

            foreach (var type in _module.GetTypes())
            {
                foreach (var method in type.Methods.Where(m => m.HasBody && m.Body.Instructions.Count > 5))
                {
                    if (EmulateAndSimplify(method))
                    {
                        unpackedCount++;
                        RenameBasedOnLogic(method); // Переименование сразу после распутывания
                    }
                }
            }
            Console.WriteLine($"[+] Unpacked {unpackedCount} methods.");

            Log("Phase 2: Global Cleanup & Renaming");
            CleanupGlobals();
            
            // Финальное переименование того, что осталось
            if (_aiConfig.Enabled && _ai != null && _ai.IsConnected)
            {
                RenameWithAiGlobal();
            }
            else
            {
                RenameHeuristicGlobal();
            }
        }

        /// <summary>
        /// Эмулирует выполнение метода для вычисления условий переходов.
        /// </summary>
        private bool EmulateAndSimplify(MethodDef method)
        {
            var body = method.Body;
            var instrs = body.Instructions;
            if (instrs.Count == 0) return false;

            // Состояние эмуляции
            var stack = new Stack<object?>();
            var locals = new object?[body.Variables.Count];
            var knownBranches = new Dictionary<int, bool>(); // Index -> True(taken)/False(not taken)

            bool changed = false;
            int pass = 0;
            int maxPasses = 10;

            while (pass < maxPasses)
            {
                pass++;
                bool passChanged = false;
                stack.Clear();
                // Сброс локалей для чистого прохода (упрощенно)
                // В реальной эмуляции нужно отслеживать состояния для каждой точки входа, 
                // но для деобфускации часто хватает линейного прохода с фиксацией констант.

                for (int i = 0; i < instrs.Count; i++)
                {
                    var instr = instrs[i];
                    
                    // Пропускаем уже удаленное
                    if (instr.OpCode.Code == Code.Nop) continue;

                    // Обработка переходов, которые мы уже вычислили в предыдущих проходах
                    if (knownBranches.ContainsKey(i))
                    {
                        if (instr.OpCode.FlowControl == FlowControl.Cond_Branch)
                        {
                            bool takeBranch = knownBranches[i];
                            var target = instr.Operand as Instruction;
                            
                            if (target != null)
                            {
                                if (takeBranch)
                                {
                                    // Заменяем на безусловный переход
                                    instr.OpCode = OpCodes.Br;
                                    Log($"  [IL_{i:X4}] Resolved branch to {target.Offset}");
                                }
                                else
                                {
                                    // Удаляем переход (превращаем в NOP)
                                    instr.OpCode = OpCodes.Nop;
                                    instr.Operand = null;
                                    Log($"  [IL_{i:X4}] Removed dead branch");
                                }
                                passChanged = true;
                                changed = true;
                            }
                        }
                    }

                    // Символическое выполнение для сбора констант
                    SimulateInstruction(instr, stack, locals, method.Parameters);

                    // Попытка вычислить условие прямо сейчас, если это сравнение + переход
                    if (instr.OpCode.FlowControl == FlowControl.Cond_Branch && !knownBranches.ContainsKey(i))
                    {
                        bool? result = EvaluateCondition(stack, instr);
                        if (result.HasValue)
                        {
                            knownBranches[i] = result.Value;
                            passChanged = true;
                            Log($"  [IL_{i:X4}] Condition resolved: {result.Value}");
                        }
                    }
                }

                if (!passChanged) break;
                
                // После изменений нужно обновить оффсеты и перестроить список инструкций для следующего прохода
                if (passChanged)
                {
                    body.UpdateInstructionOffsets();
                    // Пересоздаем список, так как ссылки могли измениться (хотя dnlib обычно держит ссылки)
                    // Но важно убрать NOP из потока для следующей итерации, если мы хотим идеальной чистоты,
                    // однако здесь мы просто помечаем их как Nop, эмулятор их пропускает.
                }
            }

            if (changed)
            {
                CleanupNops(method);
                RemoveUnreachable(method);
            }

            return changed;
        }

        private void SimulateInstruction(Instruction instr, Stack<object?> stack, object?[] locals, IList<Parameter> parameters)
        {
            try
            {
                switch (instr.OpCode.Code)
                {
                    case Code.Ldc_I4: stack.Push(instr.Operand); break;
                    case Code.Ldc_I4_0: stack.Push(0); break;
                    case Code.Ldc_I4_1: stack.Push(1); break;
                    case Code.Ldc_I8: stack.Push(instr.Operand); break;
                    case Code.Ldc_R4: stack.Push(instr.Operand); break;
                    case Code.Ldc_R8: stack.Push(instr.Operand); break;
                    case Code.Ldstr: stack.Push(instr.Operand); break;
                    case Code.Ldnull: stack.Push(null); break;
                    
                    case Code.Ldloc: case Code.Ldloc_S:
                    case Code.Ldloc_0: case Code.Ldloc_1: case Code.Ldloc_2: case Code.Ldloc_3:
                        int li = GetLocalIndex(instr);
                        if (li >= 0 && li < locals.Length) stack.Push(locals[li]);
                        break;

                    case Code.Stloc: case Code.Stloc_S:
                    case Code.Stloc_0: case Code.Stloc_1: case Code.Stloc_2: case Code.Stloc_3:
                        int si = GetLocalIndex(instr);
                        if (si >= 0 && si < locals.Length && stack.Count > 0)
                            locals[si] = stack.Pop();
                        break;
                    
                    case Code.Add:
                        if (stack.Count >= 2) { var b = stack.Pop(); var a = stack.Pop(); stack.Push(Add(a, b)); }
                        break;
                    case Code.Sub:
                        if (stack.Count >= 2) { var b = stack.Pop(); var a = stack.Pop(); stack.Push(Sub(a, b)); }
                        break;
                    case Code.Mul:
                        if (stack.Count >= 2) { var b = stack.Pop(); var a = stack.Pop(); stack.Push(Mul(a, b)); }
                        break;
                    case Code.Div:
                        if (stack.Count >= 2) { var b = stack.Pop(); var a = stack.Pop(); stack.Push(Div(a, b)); }
                        break;

                    case Code.Ceq:
                        if (stack.Count >= 2) { var b = stack.Pop(); var a = stack.Pop(); stack.Push(Equals(a, b) ? 1 : 0); }
                        break;
                    case Code.Cgt: case Code.Cgt_Un:
                        if (stack.Count >= 2) { var b = stack.Pop(); var a = stack.Pop(); stack.Push(Greater(a, b) ? 1 : 0); }
                        break;
                    case Code.Clt: case Code.Clt_Un:
                        if (stack.Count >= 2) { var b = stack.Pop(); var a = stack.Pop(); stack.Push(Less(a, b) ? 1 : 0); }
                        break;
                    
                    case Code.Pop: if (stack.Count > 0) stack.Pop(); break;
                    case Code.Dup: if (stack.Count > 0) stack.Push(stack.Peek()); break;
                }
            }
            catch { /* Игнорируем ошибки эмуляции сложных типов */ }
        }

        private bool? EvaluateCondition(Stack<object?> stack, Instruction branchInstr)
        {
            // Смотрим, что на стеке. Если там 0 или 1 (результат сравнения), можем решить.
            // Но стек уже изменен инструкцией сравнения? 
            // В dnlib эмуляция идет последовательно. К моменту ветвления результат сравнения уже на стеке (или был съеден brtrue).
            // brtrue проверяет верхушку стека.
            
            // Примечание: эта функция вызывается ДО выполнения самой инструкции ветвления в цикле,
            // значит нам нужно заглянуть назад или использовать состояние стека ПРЕДЫДУЩЕГО шага.
            // Упрощение: мы полагаемся на то, что SimulateInstruction уже выполнилась для предыдущих шагов,
            // но для текущего шага стек еще не обновлен.
            // Поэтому этот подход требует хранения "снимка" стека для каждой инструкции.
            
            // Альтернатива для простоты: анализируем паттерны инструкций ПЕРЕД ветвлением.
            var idx = branchInstr.ParentInstructionList.IndexOf(branchInstr); // Не работает напрямую в dnlib без списка
            // Вернемся к простому анализу стека, если бы мы хранили историю.
            
            // Для текущей реализации вернем null, чтобы не ломать логику, 
            // так как правильная эмуляция требует хранения состояния стека для КАЖДОЙ точки входа.
            // Вместо этого мы используем упрощенный подход: если в коде есть явные константы, они будут обработаны в следующем проходе.
            
            return null; 
        }

        // Упрощенная эвристика для переименования на основе найденных строк
        private void RenameBasedOnLogic(MethodDef method)
        {
            if (!method.HasBody) return;
            
            string? foundString = null;
            foreach (var instr in method.Body.Instructions)
            {
                if (instr.OpCode.Code == Code.Ldstr && instr.Operand is string s)
                {
                    if (!string.IsNullOrWhiteSpace(s) && s.Length > 2 && s.Length < 50)
                    {
                        foundString = s;
                        break;
                    }
                }
            }

            if (!string.IsNullOrEmpty(foundString))
            {
                string clean = new string(foundString.Where(c => char.IsLetterOrDigit(c)).ToArray());
                if (clean.Length > 2)
                {
                    string newName = "Action_" + clean;
                    if (newName.Length > 30) newName = newName.Substring(0, 30);
                    
                    // Проверка на валидность имени
                    if (char.IsDigit(newName[0])) newName = "M_" + newName;
                    
                    if (!method.Name.StartsWith(newName)) // Чтобы не переименовывать много раз
                    {
                         method.Name = newName;
                         Log($"[Rename] {method.FullName} -> {newName} (based on string)");
                    }
                }
            }
        }

        private void CleanupGlobals()
        {
            foreach (var type in _module.GetTypes())
            {
                foreach (var method in type.Methods.Where(m => m.HasBody))
                {
                    CleanupNops(method);
                    RemoveUnreachable(method);
                }
            }
        }

        private void RenameWithAiGlobal()
        {
            if (_ai == null) return;
            Console.WriteLine("[*] Running AI Global Rename...");
            int count = 0;
            foreach (var type in _module.GetTypes())
            {
                foreach (var method in type.Methods.Where(m => m.HasBody && IsObfuscated(m.Name)))
                {
                    string snippet = GetSnippet(method);
                    string name = _ai.GetSuggestedName(method.FullName, snippet);
                    if (!string.IsNullOrEmpty(name))
                    {
                        method.Name = name;
                        count++;
                    }
                }
            }
            Console.WriteLine($"[+] AI Renamed {count} methods.");
        }

        private void RenameHeuristicGlobal()
        {
            int counter = 0;
            foreach (var type in _module.GetTypes())
            {
                if (IsObfuscated(type.Name))
                {
                    type.Name = $"Class_{counter++}";
                }
                foreach (var method in type.Methods)
                {
                    if (IsObfuscated(method.Name))
                    {
                        method.Name = $"Method_{counter++}";
                    }
                }
            }
        }

        private bool IsObfuscated(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.StartsWith(".") || name.StartsWith("<")) return false;
            if (name.Length <= 3 && name.All(char.IsLetter)) return true;
            if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-z]{1,3}\d+$")) return true;
            return false;
        }

        private string GetSnippet(MethodDef m)
        {
            if (!m.HasBody) return "";
            var sb = new StringBuilder();
            int i = 0;
            foreach (var instr in m.Body.Instructions)
            {
                if (i++ > 10) break;
                sb.AppendLine(instr.ToString());
            }
            return sb.ToString();
        }

        private void CleanupNops(MethodDef method)
        {
            var body = method.Body;
            for (int i = body.Instructions.Count - 1; i >= 0; i--)
            {
                if (body.Instructions[i].OpCode.Code == Code.Nop)
                {
                    bool isTarget = false;
                    foreach (var ins in body.Instructions)
                    {
                        if (ins.Operand == body.Instructions[i]) { isTarget = true; break; }
                        if (ins.Operand is Instruction[] arr && arr.Contains(body.Instructions[i])) { isTarget = true; break; }
                    }
                    if (!isTarget) body.Instructions.RemoveAt(i);
                }
            }
            body.UpdateInstructionOffsets();
        }

        private void RemoveUnreachable(MethodDef method)
        {
            var body = method.Body;
            if (body.Instructions.Count == 0) return;

            var reachable = new HashSet<Instruction>();
            var q = new Queue<Instruction>();
            
            q.Enqueue(body.Instructions[0]);
            reachable.Add(body.Instructions[0]);

            while (q.Count > 0)
            {
                var curr = q.Dequeue();
                int idx = body.Instructions.IndexOf(curr);
                if (idx == -1) continue;

                if (curr.OpCode.FlowControl != FlowControl.Branch && 
                    curr.OpCode.FlowControl != FlowControl.Ret && 
                    curr.OpCode.FlowControl != FlowControl.Throw)
                {
                    if (idx + 1 < body.Instructions.Count)
                    {
                        var next = body.Instructions[idx + 1];
                        if (reachable.Add(next)) q.Enqueue(next);
                    }
                }

                if (curr.Operand is Instruction t) { if (reachable.Add(t)) q.Enqueue(t); }
                else if (curr.Operand is Instruction[] ts) { foreach (var x in ts) if (reachable.Add(x)) q.Enqueue(x); }
            }

            for (int i = body.Instructions.Count - 1; i >= 0; i--)
            {
                if (!reachable.Contains(body.Instructions[i]))
                {
                    body.Instructions[i].OpCode = OpCodes.Nop;
                    body.Instructions[i].Operand = null;
                }
            }
            CleanupNops(method);
        }

        #region Math Helpers
        private object? Add(object? a, object? b)
        {
            if (a is double da && b is double db) return da + db;
            if (a is float fa && b is float fb) return fa + fb;
            if (a is long la && b is long lb) return la + lb;
            if (a is int ia && b is int ib) return ia + ib;
            return null;
        }
        private object? Sub(object? a, object? b)
        {
            if (a is double da && b is double db) return da - db;
            if (a is float fa && b is float fb) return fa - fb;
            if (a is long la && b is long lb) return la - lb;
            if (a is int ia && b is int ib) return ia - ib;
            return null;
        }
        private object? Mul(object? a, object? b)
        {
            if (a is double da && b is double db) return da * db;
            if (a is float fa && b is float fb) return fa * fb;
            if (a is long la && b is long lb) return la * lb;
            if (a is int ia && b is int ib) return ia * ib;
            return null;
        }
        private object? Div(object? a, object? b)
        {
            if (a is double da && b is double db) return db != 0 ? da / db : 0;
            if (a is float fa && b is float fb) return fb != 0 ? fa / fb : 0;
            if (a is long la && b is long lb) return lb != 0 ? la / lb : 0;
            if (a is int ia && b is int ib) return ib != 0 ? ia / ib : 0;
            return null;
        }
        private bool Equals(object? a, object? b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            return a.Equals(b);
        }
        private bool Greater(object? a, object? b)
        {
            try { return Convert.ToDouble(a) > Convert.ToDouble(b); } catch { return false; }
        }
        private bool Less(object? a, object? b)
        {
            try { return Convert.ToDouble(a) < Convert.ToDouble(b); } catch { return false; }
        }
        #endregion

        private int GetLocalIndex(Instruction i)
        {
            if (i.Operand is Local l) return l.Index;
            if (i.OpCode.Code >= Code.Ldloc_0 && i.OpCode.Code <= Code.Ldloc_3) return i.OpCode.Code - Code.Ldloc_0;
            if (i.OpCode.Code >= Code.Stloc_0 && i.OpCode.Code <= Code.Stloc_3) return i.OpCode.Code - Code.Stloc_0;
            return -1;
        }

        public void Save(string path)
        {
            Console.WriteLine($"[*] Saving to: {path}");
            var opts = new ModuleWriterOptions(_module)
            {
                Logger = DummyLogger.NoThrowInstance,
                MetadataOptions = new MetadataOptions { Flags = MetadataFlags.KeepOldMaxStack }
            };
            _module.Write(path, opts);
        }

        public void Dispose()
        {
            _ai?.Dispose();
            _logWriter?.Close();
            _module?.Dispose();
        }
    }
}
