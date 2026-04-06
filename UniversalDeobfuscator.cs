using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

namespace Deobfuscator
{
    public class UniversalDeobfuscator : IDisposable
    {
        private readonly ModuleDefMD _module;
        private readonly AiConfig _aiConfig;
        private readonly bool _debugMode;
        private StreamWriter? _logWriter;

        public UniversalDeobfuscator(string filePath, AiConfig aiConfig, bool debugMode = false)
        {
            _module = ModuleDefMD.Load(filePath);
            _aiConfig = aiConfig;
            _debugMode = debugMode;

            if (_debugMode)
            {
                string logPath = Path.Combine(Path.GetDirectoryName(filePath) ?? "", "deob_log.txt");
                _logWriter = new StreamWriter(logPath, false) { AutoFlush = true };
                Log("=== Deobfuscation Started ===");
            }
        }

        private void Log(string msg)
        {
            if (!_debugMode) return;
            string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            Console.WriteLine(line);
            _logWriter?.WriteLine(line);
        }

        public void Deobfuscate()
        {
            Console.WriteLine("[*] Phase 1: Unpacking Control Flow (Emulation)...");
            Log("Starting control flow unpacking...");
            int unpackedCount = UnpackControlFlow();
            Console.WriteLine($"[+] Unpacked {unpackedCount} methods.");

            Console.WriteLine("[*] Phase 2: Dead Code Removal & Cleanup...");
            Log("Starting cleanup...");
            CleanupAll();

            Console.WriteLine("[*] Phase 3: Renaming...");
            RenameItems();

            Log("=== Deobfuscation Finished ===");
        }

        /// <summary>
        /// Алгоритм эмуляции для раскрытия State Machine (вдохновлен de4dot).
        /// Вычисляет значения переменной состояния и перестраивает переходы.
        /// </summary>
        private int UnpackControlFlow()
        {
            int count = 0;
            foreach (var type in _module.GetTypes())
            {
                foreach (var method in type.Methods.Where(m => m.HasBody && m.Body.Instructions.Count > 10))
                {
                    try
                    {
                        if (MethodUnpacker.Unpack(method))
                        {
                            count++;
                            Log($"Unpacked: {method.FullName}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Error unpacking {method.FullName}: {ex.Message}");
                    }
                }
            }
            return count;
        }

        private void CleanupAll()
        {
            foreach (var type in _module.GetTypes())
            {
                foreach (var method in type.Methods.Where(m => m.HasBody))
                {
                    RemoveUnreachableBlocks(method);
                    RemoveNops(method);
                    SimplifyMacros(method);
                }
            }
        }

        /// <summary>
        /// Удаляет инструкции, до которых нельзя добраться (Dead Code).
        /// </summary>
        private void RemoveUnreachableBlocks(MethodDef method)
        {
            var body = method.Body;
            var instructions = body.Instructions;
            if (instructions.Count == 0) return;

            var reachable = new HashSet<Instruction>();
            var queue = new Queue<Instruction>();

            // Старт с первой инструкции
            reachable.Add(instructions[0]);
            queue.Enqueue(instructions[0]);

            while (queue.Count > 0)
            {
                var instr = queue.Dequeue();
                int idx = instructions.IndexOf(instr);
                if (idx == -1) continue;

                // Переход к следующей, если не безусловный переход/возврат
                var flow = instr.OpCode.FlowControl;
                if (flow != FlowControl.Branch && flow != FlowControl.Ret && flow != FlowControl.Throw)
                {
                    if (idx + 1 < instructions.Count)
                    {
                        var next = instructions[idx + 1];
                        if (reachable.Add(next)) queue.Enqueue(next);
                    }
                }

                // Цели переходов
                if (instr.Operand is Instruction target)
                {
                    if (reachable.Add(target)) queue.Enqueue(target);
                }
                else if (instr.Operand is Instruction[] targets)
                {
                    foreach (var t in targets) if (reachable.Add(t)) queue.Enqueue(t);
                }
            }

            // Замена недостижимого на NOP
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
            if (changed) Log($"Removed dead code in {method.Name}");
        }

        private void RemoveNops(MethodDef method)
        {
            var body = method.Body;
            // Простое удаление NOP, которые не являются целями переходов
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

        private void SimplifyMacros(MethodDef method)
        {
            method.Body.SimplifyMacros(method.Parameters);
            method.Body.UpdateInstructionOffsets();
        }

        private void RenameItems()
        {
            bool useAi = _aiConfig.Enabled;
            AiAssistant? ai = null;

            if (useAi)
            {
                ai = new AiAssistant(_aiConfig);
                if (!ai.IsConnected)
                {
                    Console.WriteLine("[!] AI unavailable. Using heuristic renaming.");
                    useAi = false;
                    ai.Dispose();
                    ai = null;
                }
            }

            int renamed = 0;
            int classCounter = 1, methodCounter = 1;

            foreach (var type in _module.GetTypes())
            {
                if (IsObfuscated(type.Name))
                {
                    type.Name = useAi ? ai!.GetSuggestedName(type.Name, "", "Class") : $"Logic_Class_{classCounter++}";
                    renamed++;
                }

                foreach (var method in type.Methods)
                {
                    if (method.IsConstructor || method.Name.StartsWith("get_") || method.Name.StartsWith("set_")) continue;
                    
                    if (IsObfuscated(method.Name))
                    {
                        if (useAi && method.HasBody)
                        {
                            string snippet = GetSnippet(method);
                            string ret = method.ReturnType?.ToString() ?? "void";
                            method.Name = ai!.GetSuggestedName(method.Name, snippet, ret);
                        }
                        else
                        {
                            // Эвристика: попытка угадать назначение по сигнатуре
                            method.Name = HeuristicRename(method, methodCounter);
                        }
                        methodCounter++;
                        renamed++;
                    }
                }
            }
            Console.WriteLine($"[+] Renamed {renamed} items.");
            ai?.Dispose();
        }

        private string HeuristicRename(MethodDef m, int id)
        {
            if (m.ReturnType.FullName == "System.Void")
            {
                if (m.Parameters.Count == 0) return $"Action_Init_{id}";
                return $"Method_Execute_{id}";
            }
            if (m.ReturnType.FullName.Contains("String")) return $"Data_GetString_{id}";
            if (m.ReturnType.FullName.Contains("Int")) return $"Calc_Compute_{id}";
            if (m.ReturnType.FullName.Contains("Boolean")) return $"Check_Condition_{id}";
            
            return $"Func_Handler_{id}";
        }

        private string GetSnippet(MethodDef m)
        {
            if (!m.HasBody) return "";
            var sb = new System.Text.StringBuilder();
            int limit = Math.Min(20, m.Body.Instructions.Count);
            for (int i = 0; i < limit; i++) sb.AppendLine(m.Body.Instructions[i].ToString());
            return sb.ToString();
        }

        private bool IsObfuscated(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.Length <= 3 && name.All(char.IsLetter)) return true;
            if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-z]{1,2}\d+$")) return true;
            if (name.StartsWith("<") && name.EndsWith(">")) return false; // Свойства/события
            return false;
        }

        public void Save(string path)
        {
            Console.WriteLine($"[*] Saving to: {path}");
            Log($"Saving: {path}");
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
            _logWriter?.Close();
            _module?.Dispose();
        }
    }

    /// <summary>
    /// Вспомогательный класс для эмуляции и раскрытия обфускации потока управления.
    /// Реализует упрощенную версию алгоритма de4dot.
    /// </summary>
    public static class MethodUnpacker
    {
        public static bool Unpack(MethodDef method)
        {
            if (!method.HasBody) return false;
            var body = method.Body;
            var instructions = body.Instructions;

            // 1. Поиск переменной состояния (State Variable)
            // Обычно это локаль, в которую часто пишут константы перед ветвлениями
            int? stateVar = FindStateVariable(method);
            if (!stateVar.HasValue) return false;

            // 2. Символьное выполнение для построения реального графа
            // Мы эмулируем только вычисление адреса перехода, не выполняя побочные эффекты
            var realInstructions = new List<Instruction>();
            var visited = new HashSet<int>();
            var stack = new Stack<object?>();
            var locals = new object?[body.Variables.Count];
            
            // Инициализация: пытаемся найти стартовое значение
            // Обычно в начале метода: ldc.i4.X -> stloc stateVar
            object? currentState = null;
            for (int i = 0; i < Math.Min(10, instructions.Count); i++)
            {
                if (instructions[i].OpCode.Code == Code.Stloc && ((Local)instructions[i].Operand).Index == stateVar.Value)
                {
                    if (i > 0) currentState = GetConstant(instructions[i-1]);
                }
                else if (instructions[i].OpCode.Code >= Code.Stloc_0 && instructions[i].OpCode.Code <= Code.Stloc_3)
                {
                     int idx = instructions[i].OpCode.Code - Code.Stloc_0;
                     if (idx == stateVar.Value && i > 0) currentState = GetConstant(instructions[i-1]);
                }
            }

            if (currentState == null) return false; // Не нашли явного стейта

            locals[stateVar.Value] = currentState;
            var queue = new Queue<int>();
            queue.Enqueue(0); // Начинаем с первой инструкции

            // Эмуляция прохода для сбора достижимых инструкций
            // В реальной реализации de4dot здесь сложный анализ, здесь упрощенный вариант
            // Мы просто помечаем инструкции, которые выполняются при известном состоянии
            
            // Примечание: Полная эмуляция сложна для одного файла. 
            // Здесь мы применяем эвристику: если видим паттерн "ldloc state, ldc val, ceq, brtrue",
            // и значение совпадает с текущим состоянием, мы считаем ветку истинной.
            
            bool modified = false;
            
            // Проход 1: Упрощение условий на основе найденного стейта
            for (int i = 0; i < instructions.Count - 3; i++)
            {
                // Паттерн: ldloc(state), ldc(const), ceq, brtrue target
                if (IsLdloc(instructions[i], stateVar.Value) && 
                    IsConst(instructions[i+1]) && 
                    instructions[i+2].OpCode.Code == Code.Ceq &&
                    (instructions[i+3].OpCode.Code == Code.Brtrue || instructions[i+3].OpCode.Code == Code.Brtrue_S))
                {
                    var constVal = GetConstant(instructions[i+1]);
                    if (constVal != null && currentState != null && constVal.Equals(currentState))
                    {
                        // Условие истинно: превращаем в безусловный переход
                        instructions[i+3].OpCode = OpCodes.Br;
                        modified = true;
                        
                        // Обновляем состояние, если в целевом блоке есть присваивание
                        // (Это требует более глубокого анализа, пока пропускаем для простоты)
                    }
                    else if (constVal != null)
                    {
                        // Условие ложно: заменяем на NOP
                        instructions[i+3].OpCode = OpCodes.Nop;
                        instructions[i+3].Operand = null;
                        modified = true;
                    }
                }
            }

            if (modified)
            {
                // После изменения переходов нужно удалить недостижимый код
                // Это делается в основном классе Cleanup
                return true;
            }

            return false;
        }

        private static int? FindStateVariable(MethodDef method)
        {
            var counts = new Dictionary<int, int>();
            var instrs = method.Body.Instructions;
            
            for (int i = 0; i < instrs.Count - 3; i++)
            {
                if (instrs[i].OpCode.Code == Code.Ldloc && instrs[i].Operand is Local l)
                {
                    if (instrs[i+1].OpCode.Code == Code.Ldc_I4 && instrs[i+2].OpCode.Code == Code.Ceq)
                    {
                        if (!counts.ContainsKey(l.Index)) counts[l.Index] = 0;
                        counts[l.Index]++;
                    }
                }
            }
            
            // Переменная состояния обычно используется в сравнениях много раз (> 2)
            foreach (var kvp in counts)
            {
                if (kvp.Value > 2) return kvp.Key;
            }
            return null;
        }

        private static bool IsLdloc(Instruction i, int index)
        {
            if (i.OpCode.Code == Code.Ldloc && ((Local)i.Operand).Index == index) return true;
            if (i.OpCode.Code >= Code.Ldloc_0 && i.OpCode.Code <= Code.Ldloc_3)
                return (i.OpCode.Code - Code.Ldloc_0) == index;
            return false;
        }

        private static bool IsConst(Instruction i)
        {
            return i.OpCode.Code == Code.Ldc_I4 || i.OpCode.Code == Code.Ldc_I8 || i.OpCode.Code == Code.Ldc_R4 || i.OpCode.Code == Code.Ldc_R8;
        }

        private static object? GetConstant(Instruction i)
        {
            switch (i.OpCode.Code)
            {
                case Code.Ldc_I4: return i.Operand as int?;
                case Code.Ldc_I4_0: return 0;
                case Code.Ldc_I4_1: return 1;
                case Code.Ldc_I4_M1: return -1;
                // ... можно добавить остальные
                default: return i.Operand;
            }
        }
    }
}
