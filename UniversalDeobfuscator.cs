using System;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Writer;

namespace Deobfuscator
{
    /// <summary>
    /// Главный оркестратор деобфускации. Координирует работу всех анализаторов и оптимизаторов.
    /// Использует специализированные классы для каждой фазы деобфускации.
    /// </summary>
    public class UniversalDeobfuscator : IDisposable
    {
        private readonly ModuleDefMD _module;
        private readonly AiConfig _aiConfig;
        private readonly bool _debugMode;
        private StreamWriter? _logWriter;
        
        // Компоненты деобфускации (делегирование функциональности)
        private readonly ControlFlowUnraveler _cfUnraveler;
        private readonly MathOptimizer _mathOptimizer;
        private readonly WrapperInliner _wrapperInliner;
        private readonly CallChainAnalyzer _callChainAnalyzer;
        private readonly Renamer _renamer;

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
                Log("=== Deobfuscation Engine Started ===");
            }

            // Инициализация компонентов
            _cfUnraveler = new ControlFlowUnraveler(debugMode ? Log : null);
            _mathOptimizer = new MathOptimizer(debugMode ? Log : null);
            _wrapperInliner = new WrapperInliner(debugMode ? Log : null);
            _callChainAnalyzer = new CallChainAnalyzer(debugMode ? Log : null);
            _renamer = new Renamer(aiConfig, debugMode ? Log : null);
        }

        private void Log(string msg)
        {
            if (!_debugMode) return;
            string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            Console.WriteLine(line);
            _logWriter?.WriteLine(line);
        }

        /// <summary>
        /// Запускает полный процесс деобфускации.
        /// Порядок фаз важен: сначала упрощаем поток управления, затем оптимизируем выражения,
        /// анализируем цепочки вызовов, инлайним wrapper'ы и переименовываем элементы.
        /// </summary>
        public void Deobfuscate()
        {
            // Phase 1: Control Flow Unpacking (State Machine)
            Log("Phase 1: Control Flow Unpacking (State Machine)");
            Console.WriteLine("[*] Analyzing and unraveling control flow...");
            
            int unraveledCount = 0;
            int failedCount = 0;

            foreach (var type in _module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody || !method.Body.HasInstructions) continue;
                    if (method.Body.Instructions.Count < 5) continue;

                    try
                    {
                        if (_cfUnraveler.Unravel(method))
                        {
                            unraveledCount++;
                            Log($"[OK] Unraveled: {method.FullName}");
                        }
                    }
                    catch (Exception ex)
                    {
                        failedCount++;
                        Log($"[ERR] Failed to unravel {method.FullName}: {ex.Message}");
                    }
                }
            }

            Console.WriteLine($"[+] Unraveled {unraveledCount} methods. ({failedCount} failed/skipped)");

            // Phase 2: Math Optimization & Constant Folding
            Log("Phase 2: Math Optimization & Constant Folding");
            Console.WriteLine("[*] Optimizing math expressions...");
            int optimizedCount = _mathOptimizer.Optimize(_module);
            Console.WriteLine($"[+] Optimized {optimizedCount} math expressions.");

            // Phase 3: Cleanup (NOPs & Unreachable)
            Log("Phase 3: Cleanup (NOPs & Unreachable)");
            Console.WriteLine("[*] Cleaning up dead code...");
            CleanupAll();

            // Phase 4: Analyzing Call Chains
            Log("Phase 4: Analyzing Call Chains");
            Console.WriteLine("[*] Analyzing method call chains...");
            var callChains = _callChainAnalyzer.ScanModule(_module);
            
            if (callChains.Count > 0)
            {
                Console.WriteLine($"[+] Found {callChains.Count} call chains.");
                
                // Генерируем отчет для dnSpy
                string report = _callChainAnalyzer.GenerateReport(callChains);
                if (_debugMode)
                {
                    string reportPath = Path.Combine(Path.GetDirectoryName(_module.Location) ?? Directory.GetCurrentDirectory(), "call_chains_report.txt");
                    File.WriteAllText(reportPath, report);
                    Log($"Call chains report saved to: {reportPath}");
                }
                
                // Применяем инлайн к цепочкам, которые можно безопасно сократить
                var inlineableChains = callChains.Where(c => c.CanInline).ToList();
                if (inlineableChains.Count > 0)
                {
                    Console.WriteLine($"[*] Applying inlining for {inlineableChains.Count} safe chains...");
                    int chainInlinedCount = _callChainAnalyzer.ApplyInlining(_module, inlineableChains);
                    Console.WriteLine($"[+] Inlined {chainInlinedCount} calls from chains.");
                }
            }

            // Phase 5: Inlining Trivial Wrappers
            Log("Phase 5: Inlining Trivial Wrappers");
            Console.WriteLine("[*] Inlining trivial wrapper methods...");
            int inlinedCount = _wrapperInliner.Inline(_module);

            // Phase 6: Renaming
            Log("Phase 6: Renaming");
            Console.WriteLine("[*] Renaming obfuscated items...");
            _renamer.Rename(_module);

            Log("=== Deobfuscation Finished ===");
        }

        /// <summary>
        /// Выполняет полную очистку модуля от NOP-инструкций и недостижимых блоков.
        /// </summary>
        private void CleanupAll()
        {
            foreach (var type in _module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody || !method.Body.IsIL) continue;
                    
                    var body = method.Body;
                    CleanupNops(method);
                    RemoveUnreachableBlocks(method);
                    body.UpdateInstructionOffsets();
                }
            }
        }

        /// <summary>
        /// Удаляет NOP-инструкции, на которые нет ссылок из других инструкций.
        /// </summary>
        private static void CleanupNops(MethodDef method)
        {
            var body = method.Body;
            if (body == null) return;

            bool changed;
            do
            {
                changed = false;
                for (int i = body.Instructions.Count - 1; i >= 0; i--)
                {
                    if (body.Instructions[i].OpCode.Code == Code.Nop)
                    {
                        bool isTarget = body.Instructions.Any(ins => 
                            ins.Operand == body.Instructions[i] || 
                            (ins.Operand is Instruction[] arr && arr.Contains(body.Instructions[i])));

                        if (!isTarget)
                        {
                            body.Instructions.RemoveAt(i);
                            changed = true;
                        }
                    }
                }
            } while (changed);
        }

        /// <summary>
        /// Помечает недостижимые блоки кода как NOP.
        /// </summary>
        private static void RemoveUnreachableBlocks(MethodDef method)
        {
            var body = method.Body;
            if (body.Instructions.Count == 0) return;

            var reachable = new HashSet<Instruction>();
            var queue = new Queue<Instruction>();

            queue.Enqueue(body.Instructions[0]);
            reachable.Add(body.Instructions[0]);

            while (queue.Count > 0)
            {
                var curr = queue.Dequeue();
                int idx = body.Instructions.IndexOf(curr);
                if (idx == -1) continue;

                if (curr.OpCode.FlowControl != FlowControl.Branch &&
                    curr.OpCode.FlowControl != FlowControl.Return &&
                    curr.OpCode.FlowControl != FlowControl.Throw)
                {
                    if (idx + 1 < body.Instructions.Count)
                    {
                        var next = body.Instructions[idx + 1];
                        if (reachable.Add(next)) queue.Enqueue(next);
                    }
                }

                if (curr.Operand is Instruction target)
                {
                    if (reachable.Add(target)) queue.Enqueue(target);
                }
                else if (curr.Operand is Instruction[] targets)
                {
                    foreach (var t in targets)
                    {
                        if (reachable.Add(t)) queue.Enqueue(t);
                    }
                }
            }

            for (int i = body.Instructions.Count - 1; i >= 0; i--)
            {
                if (!reachable.Contains(body.Instructions[i]))
                {
                    body.Instructions[i].OpCode = OpCodes.Nop;
                    body.Instructions[i].Operand = null;
                }
            }
        }

        /// <summary>
        /// Сохраняет деобфусцированный модуль в файл.
        /// </summary>
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
