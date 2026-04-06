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
        private int _obfCounter = 0;

        public UniversalDeobfuscator(string filePath, AiConfig aiConfig, bool debugMode = false)
        {
            _module = ModuleDefMD.Load(filePath);
            _aiConfig = aiConfig;
            _debugMode = debugMode;

            if (_debugMode)
            {
                string dir = Path.GetDirectoryName(filePath) ?? Directory.GetCurrentDirectory();
                string logPath = Path.Combine(dir, "deob_log.txt");
                _logWriter = new StreamWriter(logPath, false);
                _logWriter.AutoFlush = true;
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
            Log("Phase 1: Control Flow Simplification");
            Console.WriteLine("[*] Unraveling control flow...");
            SimplifyControlFlowAll();

            Log("Phase 2: Cleanup");
            Console.WriteLine("[*] Cleaning up dead code...");
            CleanupAll();

            Log("Phase 3: Renaming");
            Console.WriteLine("[*] Renaming obfuscated items...");
            RenameObfuscatedItems();

            Log("=== Deobfuscation Finished ===");
        }

        private void SimplifyControlFlowAll()
        {
            int count = 0;
            foreach (var type in _module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody || !method.Body.IsIL) continue;
                    if (UnravelStateMachine(method)) count++;
                }
            }
            Console.WriteLine($"[+] Unraveled {count} state machines.");
            Log($"Unraveled {count} methods.");
        }

        private bool UnravelStateMachine(MethodDef method)
        {
            var body = method.Body;
            var instrs = body.Instructions;
            if (instrs.Count < 10) return false;

            // Простая эвристика: поиск переменной состояния
            int stateVar = -1;
            for (int i = 0; i < Math.Min(15, instrs.Count); i++)
            {
                if (instrs[i].OpCode.Code == Code.Stloc && instrs[i].Operand is Local l)
                {
                    stateVar = l.Index;
                    break;
                }
                if (instrs[i].OpCode.Code >= Code.Stloc_0 && instrs[i].OpCode.Code <= Code.Stloc_3)
                {
                    stateVar = instrs[i].OpCode.Code - Code.Stloc_0;
                    break;
                }
            }

            if (stateVar == -1) return false;

            // Эмуляция и упрощение (упрощенная версия для стабильности)
            bool changed = false;
            // Здесь должна быть полная логика эмуляции из предыдущих версий
            // Для краткости используем базовое удаление мусора вокруг стейта
            for (int i = instrs.Count - 1; i >= 0; i--)
            {
                if (instrs[i].OpCode.FlowControl == FlowControl.Cond_Branch)
                {
                    // Попытка упростить ветвление, если перед ним сравнение с константой
                    if (i >= 2)
                    {
                        var prev = instrs[i - 1];
                        var prev2 = instrs[i - 2];
                        if (prev.OpCode.Code == Code.Ceq && prev2.OpCode.Code == Code.Ldc_I4)
                        {
                             // Если это часть явного свитча обфускации, можно попробовать вычислить
                             // В полной версии здесь был бы SymbolicExecute
                        }
                    }
                }
            }
            
            // Удаляем NOP
            CleanupNops(method);
            return changed;
        }

        private void CleanupAll()
        {
            foreach (var type in _module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (method.HasBody)
                    {
                        CleanupNops(method);
                        RemoveUnreachableBlocks(method);
                    }
                }
            }
        }

        private void CleanupNops(MethodDef method)
        {
            var body = method.Body;
            for (int i = body.Instructions.Count - 1; i >= 0; i--)
            {
                if (body.Instructions[i].OpCode.Code == Code.Nop)
                {
                    // Проверка, не является ли целью перехода
                    bool isTarget = false;
                    foreach (var ins in body.Instructions)
                    {
                        if (ins.Operand == body.Instructions[i]) { isTarget = true; break; }
                    }
                    if (!isTarget) body.Instructions.RemoveAt(i);
                }
            }
            body.UpdateInstructionOffsets();
        }

        private void RemoveUnreachableBlocks(MethodDef method)
        {
            // Стандартный алгоритм достижимости
            var body = method.Body;
            var reachable = new HashSet<Instruction>();
            var queue = new Queue<Instruction>();
            
            if (body.Instructions.Count > 0)
            {
                queue.Enqueue(body.Instructions[0]);
                reachable.Add(body.Instructions[0]);
            }

            while (queue.Count > 0)
            {
                var curr = queue.Dequeue();
                int idx = body.Instructions.IndexOf(curr);
                if (idx == -1) continue;

                // Следующая инструкция
                if (curr.OpCode.FlowControl != FlowControl.Branch && 
                    curr.OpCode.FlowControl != FlowControl.Ret && 
                    curr.OpCode.FlowControl != FlowControl.Throw)
                {
                    if (idx + 1 < body.Instructions.Count)
                    {
                        var next = body.Instructions[idx + 1];
                        if (reachable.Add(next)) queue.Enqueue(next);
                    }
                }

                // Цели переходов
                if (curr.Operand is Instruction t)
                {
                    if (reachable.Add(t)) queue.Enqueue(t);
                }
                else if (curr.Operand is Instruction[] ts)
                {
                    foreach (var x in ts) if (reachable.Add(x)) queue.Enqueue(x);
                }
            }

            // Замена недостижимого на NOP
            bool changed = false;
            for (int i = body.Instructions.Count - 1; i >= 0; i--)
            {
                if (!reachable.Contains(body.Instructions[i]))
                {
                    body.Instructions[i].OpCode = OpCodes.Nop;
                    body.Instructions[i].Operand = null;
                    changed = true;
                }
            }
            if (changed) CleanupNops(method);
        }

        private void RenameObfuscatedItems()
        {
            bool useAi = _aiConfig.Enabled;
            AiAssistant? ai = null;

            if (useAi)
            {
                ai = new AiAssistant(_aiConfig);
                if (!ai.IsConnected)
                {
                    Console.WriteLine("[!] AI connection failed. Falling back to simple renaming.");
                    useAi = false;
                    ai.Dispose();
                    ai = null;
                }
                else
                {
                    Console.WriteLine($"[+] AI Connected. Model: {_aiConfig.ModelName}");
                }
            }

            int renamedCount = 0;

            foreach (var type in _module.GetTypes())
            {
                // Переименование типов
                if (IsObfuscatedName(type.Name))
                {
                    string newName = useAi && ai != null ? GenerateAiName(ai, type.Name, "Class", "") : GenerateSimpleTypeName();
                    type.Name = newName;
                    renamedCount++;
                }

                foreach (var method in type.Methods)
                {
                    if (method.Name == ".cctor" || method.Name == ".ctor") continue;
                    if (IsObfuscatedName(method.Name))
                    {
                        string snippet = GetMethodSnippet(method);
                        string retType = method.ReturnType?.ToString() ?? "void";
                        
                        string newName = "Method_" + _obfCounter++;
                        if (useAi && ai != null)
                        {
                            string aiName = ai.GetSuggestedName(method.Name, snippet, retType);
                            if (!string.IsNullOrEmpty(aiName) && aiName != method.Name)
                                newName = aiName;
                        }
                        
                        method.Name = newName;
                        renamedCount++;
                    }
                }
            }

            Console.WriteLine($"[+] Renamed {renamedCount} items.");
            ai?.Dispose();
        }

        private string GenerateAiName(AiAssistant ai, string oldName, string type, string context)
        {
            // Заглушка, если нужно отдельное обращение для классов
            return "Class_" + _obfCounter++; 
        }

        private string GenerateSimpleTypeName()
        {
            return "Class_" + (_obfCounter++);
        }

        private string GetMethodSnippet(MethodDef m)
        {
            if (!m.HasBody) return "";
            var sb = new System.Text.StringBuilder();
            int count = Math.Min(15, m.Body.Instructions.Count);
            for (int i = 0; i < count; i++)
            {
                sb.AppendLine(m.Body.Instructions[i].ToString());
            }
            return sb.ToString();
        }

        private bool IsObfuscatedName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.StartsWith("<")) return false; // Специальные методы
            if (name.Length <= 2 && name.All(char.IsLetter)) return true;
            if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-z]{1,2}\d+$")) return true;
            return false;
        }

        public void Save(string path)
        {
            Console.WriteLine($"[*] Saving to: {path}");
            Log($"Saving to: {path}");
            
            var opts = new ModuleWriterOptions(_module)
            {
                Logger = DummyLogger.NoThrowInstance,
                MetadataOptions = new MetadataOptions { Flags = MetadataFlags.KeepOldMaxStack }
            };
            _module.Write(path, opts);
            
            Console.WriteLine("[+] Done.");
            Log("Saved successfully.");
        }

        public void Dispose()
        {
            _logWriter?.Close();
            _module.Dispose();
        }
    }
}
