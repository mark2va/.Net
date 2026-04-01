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
        private readonly AiAssistant? _aiAssistant;
        private readonly bool _debugMode;
        private readonly string? _logFilePath;
        private int _indentLevel = 0;
        private StreamWriter? _logWriter;
        private readonly Dictionary<string, MethodStats> _methodStats = new();

        public class MethodStats
        {
            public string Name { get; set; } = "";
            public int OriginalInstructions { get; set; }
            public int FinalInstructions { get; set; }
            public bool WasModified { get; set; }
            public List<string> Changes { get; set; } = new();
            public string? Error { get; set; }
        }

        public UniversalDeobfuscator(string filePath, AiConfig aiConfig, bool debugMode = false)
        {
            _module = ModuleDefMD.Load(filePath);
            _aiAssistant = aiConfig.Enabled ? new AiAssistant(aiConfig) : null;
            _debugMode = debugMode;

            if (_debugMode)
            {
                string dir = Path.GetDirectoryName(filePath) ?? Directory.GetCurrentDirectory();
                _logFilePath = Path.Combine(dir, $"deob_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                try
                {
                    _logWriter = new StreamWriter(_logFilePath, false);
                    _logWriter.AutoFlush = true;
                    Log("=== Deobfuscation Started ===");
                    Log($"Source: {filePath}");
                    Log($"Time: {DateTime.Now}");
                    Log($"Module: {_module.Name}");
                    Log("=" .PadRight(50, '='));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[!] Failed to create log file: {ex.Message}");
                }
            }
        }

        private void Log(string message, bool addTimestamp = true)
        {
            if (!_debugMode) return;
            
            string indent = new string(' ', _indentLevel * 2);
            string timestamp = addTimestamp ? $"[{DateTime.Now:HH:mm:ss.fff}] " : "";
            string fullMsg = $"{timestamp}{indent}{message}";
            
            Console.WriteLine(fullMsg);
            _logWriter?.WriteLine(fullMsg);
        }

        private void LogMethodStart(MethodDef method)
        {
            var stats = new MethodStats
            {
                Name = method.FullName,
                OriginalInstructions = method.Body?.Instructions.Count ?? 0
            };
            _methodStats[method.FullName] = stats;
            
            Log($"┌─ Processing: {method.FullName}");
            Log($"│  Original instructions: {stats.OriginalInstructions}");
            _indentLevel++;
        }

        private void LogMethodEnd(MethodDef method, bool success)
        {
            _indentLevel--;
            var stats = _methodStats[method.FullName];
            stats.FinalInstructions = method.Body?.Instructions.Count ?? 0;
            
            string status = success ? "✓" : "✗";
            Log($"└─ {status} Result: {stats.OriginalInstructions} → {stats.FinalInstructions} instructions");
            
            if (stats.WasModified && stats.Changes.Any())
            {
                Log($"   Changes:");
                foreach (var change in stats.Changes.Take(10))
                {
                    Log($"     • {change}");
                }
                if (stats.Changes.Count > 10)
                    Log($"     • ... and {stats.Changes.Count - 10} more changes");
            }
            
            if (stats.Error != null)
                Log($"   Error: {stats.Error}");
        }

        private void LogChange(MethodDef method, string description)
        {
            if (_methodStats.TryGetValue(method.FullName, out var stats))
            {
                stats.WasModified = true;
                stats.Changes.Add(description);
            }
            Log($"│  [CHANGE] {description}");
        }

        public void Deobfuscate()
        {
            Log("Starting deobfuscation process...");
            int processedCount = 0;
            int modifiedCount = 0;
            
            foreach (var type in _module.GetTypes())
            {
                Log($"Processing type: {type.FullName}");
                _indentLevel++;
                
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody || !method.Body.HasInstructions) 
                    {
                        Log($"Skipping method without body: {method.FullName}");
                        continue;
                    }

                    try
                    {
                        processedCount++;
                        LogMethodStart(method);
                        
                        // Создаем бэкап инструкций
                        var originalInstructions = method.Body.Instructions.ToList();
                        bool wasModified = false;
                        
                        // 1. Проверяем, является ли метод state machine
                        bool isStateMachine = IsStateMachine(method);
                        if (isStateMachine)
                        {
                            Log($"│  Detected as state machine");
                            bool unpacked = UnpackStateMachine(method);
                            if (unpacked)
                            {
                                wasModified = true;
                                LogChange(method, "Unpacked state machine");
                            }
                        }
                        
                        // 2. Упрощаем условия (только если есть изменения)
                        if (wasModified || HasComplexConditions(method))
                        {
                            int beforeSimplify = method.Body.Instructions.Count;
                            SimplifyConditions(method);
                            if (beforeSimplify != method.Body.Instructions.Count)
                            {
                                wasModified = true;
                                LogChange(method, $"Simplified conditions (removed {beforeSimplify - method.Body.Instructions.Count} instructions)");
                            }
                        }
                        
                        // 3. Чистим NOPs (только если они есть)
                        int nopCount = method.Body.Instructions.Count(i => i.OpCode.Code == Code.Nop);
                        if (nopCount > 0)
                        {
                            CleanupNops(method);
                            wasModified = true;
                            LogChange(method, $"Removed {nopCount} NOP instructions");
                        }
                        
                        // 4. Исправляем стек (только если нужно)
                        if (HasStackIssues(method))
                        {
                            FixStackImbalance(method);
                            wasModified = true;
                            LogChange(method, "Fixed stack imbalances");
                        }
                        
                        // 5. Проверяем, не остался ли метод пустым
                        if (method.Body.Instructions.Count == 0)
                        {
                            Log($"│  WARNING: Method became empty! Restoring original...");
                            RestoreMethodBody(method, originalInstructions);
                            LogChange(method, "RESTORED original instructions (method would be empty)");
                            wasModified = false;
                        }
                        
                        // 6. AI переименование (опционально)
                        if (_aiAssistant != null && IsObfuscatedName(method.Name))
                        {
                            string oldName = method.Name;
                            RenameWithAi(method);
                            if (oldName != method.Name)
                            {
                                LogChange(method, $"Renamed: {oldName} → {method.Name}");
                                wasModified = true;
                            }
                        }
                        
                        // 7. Логируем результат
                        if (wasModified)
                        {
                            modifiedCount++;
                            Log($"│  Successfully modified method");
                            
                            // Выводим первые 10 инструкций для проверки
                            if (_debugMode && method.Body.Instructions.Any())
                            {
                                Log($"│  First 10 instructions after processing:");
                                _indentLevel++;
                                for (int i = 0; i < Math.Min(10, method.Body.Instructions.Count); i++)
                                {
                                    Log($"│  {method.Body.Instructions[i]}");
                                }
                                _indentLevel--;
                            }
                        }
                        else
                        {
                            Log($"│  No changes needed");
                        }
                        
                        LogMethodEnd(method, true);
                    }
                    catch (Exception ex)
                    {
                        Log($"│  ERROR: {ex.Message}");
                        Log($"│  Stack trace: {ex.StackTrace}");
                        LogMethodEnd(method, false);
                        
                        if (_methodStats.TryGetValue(method.FullName, out var stats))
                        {
                            stats.Error = ex.Message;
                        }
                    }
                }
                
                _indentLevel--;
            }
            
            // Итоговая статистика
            Log("\n" + "=".PadRight(50, '='));
            Log($"DECOMPILATION COMPLETE");
            Log($"Total methods processed: {processedCount}");
            Log($"Methods modified: {modifiedCount}");
            Log($"Methods with errors: {_methodStats.Count(s => s.Value.Error != null)}");
            
            // Сохраняем детальную статистику
            string statsPath = Path.Combine(Path.GetDirectoryName(_logFilePath) ?? ".", "deobfuscation_stats.csv");
            SaveStatsToCsv(statsPath);
            Log($"Statistics saved to: {statsPath}");
        }

        private bool IsStateMachine(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            
            // Проверяем наличие паттернов state machine
            for (int i = 0; i < instructions.Count - 5; i++)
            {
                // Ищем паттерн: stloc (state var) + сравнение + ветвление
                if (IsStloc(instructions[i + 1], out int idx) && 
                    GetConstantValue(instructions[i]) != null)
                {
                    for (int j = i + 2; j < Math.Min(instructions.Count, i + 10); j++)
                    {
                        if (instructions[j].OpCode.Code == Code.Ceq &&
                            (instructions[j + 1].OpCode.Code == Code.Brtrue ||
                             instructions[j + 1].OpCode.Code == Code.Brfalse))
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        private bool HasComplexConditions(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            return instructions.Any(i => i.OpCode.FlowControl == FlowControl.Cond_Branch);
        }

        private bool HasStackIssues(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            int pushCount = 0;
            
            for (int i = 0; i < instructions.Count; i++)
            {
                var instr = instructions[i];
                pushCount += GetStackPushCount(instr);
                int popCount = GetStackPopCount(instr);
                
                if (pushCount - popCount < 0)
                    return true;
                    
                pushCount -= popCount;
            }
            
            return pushCount != 0;
        }

        private int GetStackPushCount(Instruction instr)
        {
            var behaviour = instr.OpCode.StackBehaviourPush;
            return behaviour switch
            {
                StackBehaviour.Push0 => 0,
                StackBehaviour.Push1 => 1,
                StackBehaviour.Push1_push1 => 2,
                StackBehaviour.Pushi => 1,
                StackBehaviour.Pushi8 => 1,
                StackBehaviour.Pushr4 => 1,
                StackBehaviour.Pushr8 => 1,
                StackBehaviour.Pushref => 1,
                _ => 0
            };
        }

        private int GetStackPopCount(Instruction instr)
        {
            var behaviour = instr.OpCode.StackBehaviourPop;
            return behaviour switch
            {
                StackBehaviour.Pop0 => 0,
                StackBehaviour.Pop1 => 1,
                StackBehaviour.Pop1_pop1 => 2,
                StackBehaviour.Popi => 1,
                StackBehaviour.Popi_pop1 => 2,
                StackBehaviour.Popi_popi => 2,
                StackBehaviour.Popi_popi8 => 2,
                StackBehaviour.Popi_popr4 => 2,
                StackBehaviour.Popi_popr8 => 2,
                StackBehaviour.Popref => 1,
                StackBehaviour.Popref_pop1 => 2,
                StackBehaviour.Popref_popi => 2,
                StackBehaviour.Popref_popi_popi => 3,
                StackBehaviour.Popref_popi_popi8 => 3,
                StackBehaviour.Popref_popi_popr4 => 3,
                StackBehaviour.Popref_popi_popr8 => 3,
                StackBehaviour.Popref_popi_popref => 3,
                StackBehaviour.PopAll => int.MaxValue,
                _ => 0
            };
        }

        private void RestoreMethodBody(MethodDef method, List<Instruction> originalInstructions)
        {
            var body = method.Body;
            body.Instructions.Clear();
            foreach (var instr in originalInstructions)
            {
                body.Instructions.Add(CloneInstruction(instr));
            }
            body.UpdateInstructionOffsets();
            body.SimplifyMacros(method.Parameters);
        }

        private void SaveStatsToCsv(string path)
        {
            try
            {
                using var writer = new StreamWriter(path);
                writer.WriteLine("Method,OriginalInstructions,FinalInstructions,WasModified,Error,Changes");
                
                foreach (var stats in _methodStats.Values)
                {
                    string changes = string.Join("; ", stats.Changes.Take(5));
                    if (stats.Changes.Count > 5)
                        changes += "...";
                    
                    writer.WriteLine($"\"{stats.Name}\",{stats.OriginalInstructions},{stats.FinalInstructions}," +
                                   $"{stats.WasModified},\"{stats.Error ?? ""}\",\"{changes}\"");
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to save statistics: {ex.Message}");
            }
        }

        // Остальные методы остаются, но с добавлением логирования
        private bool UnpackStateMachine(MethodDef method)
        {
            // ... ваш существующий код с добавлением LogChange
            return false; // временно
        }

        private void SimplifyConditions(MethodDef method)
        {
            // ... ваш существующий код
        }

        private void FixStackImbalance(MethodDef method)
        {
            // ... ваш существующий код, но менее агрессивный
        }

        private void CleanupNops(MethodDef method)
        {
            // ... ваш существующий код
        }

        // ... остальные вспомогательные методы остаются без изменений

        public void Save(string path)
        {
            string saveMsg = $"[*] Saving to: {path}";
            Console.WriteLine(saveMsg);
            Log(saveMsg);
            
            try
            {
                var opts = new ModuleWriterOptions(_module)
                {
                    Logger = DummyLogger.NoThrowInstance,
                    MetadataOptions = new MetadataOptions { Flags = MetadataFlags.KeepOldMaxStack }
                };
                _module.Write(path, opts);
                
                Log("[+] File saved successfully");
                
                // Создаем отчет
                string reportPath = Path.Combine(Path.GetDirectoryName(path) ?? ".", "deobfuscation_report.txt");
                GenerateReport(reportPath);
                Log($"Report generated: {reportPath}");
            }
            catch (Exception ex)
            {
                Log($"[-] Failed to save: {ex.Message}");
                throw;
            }
        }

        private void GenerateReport(string reportPath)
        {
            try
            {
                using var writer = new StreamWriter(reportPath);
                writer.WriteLine("=== DEOBFUSCATION REPORT ===");
                writer.WriteLine($"Generated: {DateTime.Now}");
                writer.WriteLine($"Module: {_module.Name}");
                writer.WriteLine();
                
                var modified = _methodStats.Values.Where(s => s.WasModified).ToList();
                var errors = _methodStats.Values.Where(s => s.Error != null).ToList();
                
                writer.WriteLine($"Total methods: {_methodStats.Count}");
                writer.WriteLine($"Modified: {modified.Count}");
                writer.WriteLine($"Errors: {errors.Count}");
                writer.WriteLine();
                
                writer.WriteLine("=== MODIFIED METHODS ===");
                foreach (var stat in modified)
                {
                    writer.WriteLine($"\n{stat.Name}");
                    writer.WriteLine($"  Instructions: {stat.OriginalInstructions} → {stat.FinalInstructions}");
                    writer.WriteLine($"  Changes:");
                    foreach (var change in stat.Changes)
                    {
                        writer.WriteLine($"    - {change}");
                    }
                }
                
                if (errors.Any())
                {
                    writer.WriteLine("\n=== ERRORS ===");
                    foreach (var stat in errors)
                    {
                        writer.WriteLine($"\n{stat.Name}: {stat.Error}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to generate report: {ex.Message}");
            }
        }

        public void Dispose()
        {
            Log("=== Deobfuscation Finished ===");
            Log($"Total log entries: {_methodStats.Count}");
            
            _aiAssistant?.Dispose();
            _module.Dispose();
            _logWriter?.Dispose();
        }
    }
}
