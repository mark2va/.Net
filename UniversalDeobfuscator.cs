using System;
using System.IO;
using dnlib.DotNet;
using dnlib.DotNet.Writer;

namespace Deobfuscator
{
    public class UniversalDeobfuscator : IDisposable
    {
        private readonly ModuleDefMD _module;
        private readonly AiConfig _aiConfig;
        private readonly bool _debugMode;
        private StreamWriter? _logWriter;
        private ControlFlowUnraveler? _cfUnraveler;
        private MathOptimizer? _mathOptimizer;
        private WrapperInliner? _wrapperInliner;
        private Renamer? _renamer;

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
            _renamer = new Renamer(aiConfig, debugMode ? Log : null);
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
            Log("Phase 1: Control Flow Unpacking (State Machine)");
            Console.WriteLine("[*] Analyzing and unraveling control flow...");
            
            int unraveledCount = 0;
            int failedCount = 0;

            foreach (var type in _module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody || !method.Body.IsIL) continue;
                    if (method.Body.Instructions.Count < 5) continue;

                    try
                    {
                        if (_cfUnraveler!.Unravel(method))
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

            Log("Phase 2: Math Optimization & Constant Folding");
            Console.WriteLine("[*] Optimizing math expressions...");
            int optimizedCount = _mathOptimizer!.Optimize(_module);
            Console.WriteLine($"[+] Optimized {optimizedCount} math expressions.");

            Log("Phase 3: Cleanup (NOPs & Unreachable)");
            Console.WriteLine("[*] Cleaning up dead code...");
            CleanupAll();

            Log("Phase 4: Inlining Trivial Wrappers");
            Console.WriteLine("[*] Inlining trivial wrapper methods...");
            int inlinedCount = _wrapperInliner!.Inline(_module);

            Log("Phase 5: Renaming");
            Console.WriteLine("[*] Renaming obfuscated items...");
            _renamer!.Rename(_module);

            Log("=== Deobfuscation Finished ===");
        }

        /// <summary>
        /// Основной алгоритм распутывания State Machine.
        /// </summary>
        private bool UnravelStateMachine(MethodDef method)
        {
            var body = method.Body;
            var instrs = body.Instructions;
            
            // 1. Поиск переменной состояния (State Variable)
            int? stateVarIndex = FindStateVariable(method);
            
            if (!stateVarIndex.HasValue)
            {
                return SimplifyControlFlowBasic(method);
            }

            Log($"  Found state variable: V_{stateVarIndex.Value}");

            // 2. Символьное выполнение для вычисления значений состояния
            var stateMap = SymbolicExecuteState(method, stateVarIndex.Value);

            if (stateMap.Count == 0)
            {
                Log("  Symbolic execution yielded no states. Skipping.");
                return false;
            }

            bool changed = false;

            // 3. Упрощение ветвлений на основе карты состояний
            for (int i = instrs.Count - 1; i >= 0; i--)
            {
                var instr = instrs[i];

                // А. Удаляем записи в переменную состояния
                if (IsStloc(instr, out int sIdx) && sIdx == stateVarIndex.Value)
                {
                    if (i > 0 && IsLdc(instrs[i - 1]))
                    {
                        instrs[i - 1].OpCode = OpCodes.Nop;
                        instrs[i - 1].Operand = null;
                    }
                    instr.OpCode = OpCodes.Nop;
                    instr.Operand = null;
                    changed = true;
                    continue;
                }

                // Б. Упрощаем условные переходы
                if (instr.OpCode.FlowControl == FlowControl.Cond_Branch)
                {
                    if (TryResolveBranch(instrs, i, stateMap, stateVarIndex.Value))
                    {
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                CleanupNops(method);
                RemoveUnreachableBlocks(method);
                body.UpdateInstructionOffsets();
            }

            return changed;
        }

        /// <summary>
        /// Находит индекс локальной переменной, используемой как состояние.
        /// </summary>
        private int? FindStateVariable(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            var usageCount = new Dictionary<int, int>();

            for (int i = 0; i < instructions.Count - 3; i++)
            {
                if (IsLdloc(instructions[i], out int idx))
                {
                    var next = instructions[i + 1];
                    var next2 = instructions[i + 2];

                    if (IsLdc(next) && 
                        (next2.OpCode.Code == Code.Ceq || next2.OpCode.Code == Code.Cgt || next2.OpCode.Code == Code.Clt || next2.OpCode.Code == Code.Cgt_Un || next2.OpCode.Code == Code.Clt_Un))
                    {
                        if (!usageCount.ContainsKey(idx)) usageCount[idx] = 0;
                        usageCount[idx]++;
                    }
                }
            }

            foreach (var kvp in usageCount)
            {
                if (kvp.Value >= 3) return kvp.Key;
            }

            return null;
        }

        /// <summary>
        /// Символьное выполнение: определяет значение переменной состояния перед каждой инструкцией.
        /// </summary>
        private Dictionary<int, object?> SymbolicExecuteState(MethodDef method, int stateVarIndex)
        {
            var stateValues = new Dictionary<int, object?>();
            var instructions = method.Body.Instructions;
            
            var queue = new Queue<Tuple<int, object?>>();
            
            object? initialState = null;
            int startIp = 0;

            for (int i = 0; i < Math.Min(30, instructions.Count); i++)
            {
                if (IsStloc(instructions[i], out int idx) && idx == stateVarIndex)
                {
                    if (i > 0 && IsLdc(instructions[i - 1]))
                    {
                        initialState = GetConstantValue(instructions[i - 1]);
                        startIp = i + 1;
                        break;
                    }
                }
            }

            if (initialState == null) return stateValues;

            queue.Enqueue(Tuple.Create(startIp, initialState));
            
            var visited = new HashSet<int>();

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                int ip = current.Item1;
                object? currentState = current.Item2;

                if (ip < 0 || ip >= instructions.Count) continue;
                if (visited.Contains(ip)) continue;
                visited.Add(ip);

                if (!stateValues.ContainsKey(ip))
                    stateValues[ip] = currentState;

                var instr = instructions[ip];

                if (instr.OpCode.FlowControl == FlowControl.Branch)
                {
                    if (instr.Operand is Instruction target)
                    {
                        int targetIp = instructions.IndexOf(target);
                        if (targetIp != -1) queue.Enqueue(Tuple.Create(targetIp, currentState));
                    }
                }
                else if (instr.OpCode.FlowControl == FlowControl.Cond_Branch)
                {
                    if (instr.Operand is Instruction target)
                    {
                        int targetIp = instructions.IndexOf(target);
                        if (targetIp != -1) queue.Enqueue(Tuple.Create(targetIp, currentState));
                    }
                    int nextIp = ip + 1;
                    if (nextIp < instructions.Count) queue.Enqueue(Tuple.Create(nextIp, currentState));
                }
                else if (instr.OpCode.FlowControl == FlowControl.Ret || instr.OpCode.FlowControl == FlowControl.Throw)
                {
                    // Конец пути
                }
                else
                {
                    object? nextState = currentState;
                    
                    if (IsStloc(instr, out int sIdx) && sIdx == stateVarIndex)
                    {
                        if (ip > 0 && IsLdc(instructions[ip - 1]))
                        {
                            nextState = GetConstantValue(instructions[ip - 1]);
                        }
                    }
                    
                    int nextIp = ip + 1;
                    if (nextIp < instructions.Count)
                    {
                        queue.Enqueue(Tuple.Create(nextIp, nextState));
                    }
                }
            }

            return stateValues;
        }

        /// <summary>
        /// Пытается разрешить условный переход, если условие зависит от известного состояния.
        /// </summary>
        private bool TryResolveBranch(IList<Instruction> instrs, int branchIp, Dictionary<int, object?> stateMap, int stateVarIndex)
        {
            var branchInstr = instrs[branchIp];
            
            int lookback = 0;
            int cmpIp = -1;
            int ldcIp = -1;
            int ldlocIp = -1;

            if (branchIp >= 3)
            {
                var i1 = instrs[branchIp - 1];
                var i2 = instrs[branchIp - 2];
                var i3 = instrs[branchIp - 3];

                if ((i1.OpCode.Code == Code.Ceq || i1.OpCode.Code == Code.Cgt || i1.OpCode.Code == Code.Clt || 
                     i1.OpCode.Code == Code.Cgt_Un || i1.OpCode.Code == Code.Clt_Un) &&
                    IsLdc(i2) && IsLdloc(i3, out int lIdx) && lIdx == stateVarIndex)
                {
                    cmpIp = branchIp - 1;
                    ldcIp = branchIp - 2;
                    ldlocIp = branchIp - 3;
                }
            }

            if (cmpIp == -1) return false;

            if (!stateMap.TryGetValue(ldlocIp, out object? currentState))
                return false;

            var compareValue = GetConstantValue(instrs[ldcIp]);
            if (compareValue == null || currentState == null)
                return false;

            bool? result = CompareValues(currentState, compareValue, instrs[cmpIp].OpCode.Code);

            if (result.HasValue)
            {
                if (result.Value)
                {
                    Log($"    Resolved branch at {branchIp}: TRUE -> br");
                    branchInstr.OpCode = OpCodes.Br;
                    if (branchInstr.OpCode.Code == Code.Brtrue_S || branchInstr.OpCode.Code == Code.Brfalse_S)
                         branchInstr.OpCode = OpCodes.Br_S;
                    else branchInstr.OpCode = OpCodes.Br;
                    
                    instrs[ldlocIp].OpCode = OpCodes.Nop;
                    instrs[ldlocIp].Operand = null;
                    instrs[ldcIp].OpCode = OpCodes.Nop;
                    instrs[ldcIp].Operand = null;
                    instrs[cmpIp].OpCode = OpCodes.Nop;
                    instrs[cmpIp].Operand = null;
                }
                else
                {
                    Log($"    Resolved branch at {branchIp}: FALSE -> nop");
                    branchInstr.OpCode = OpCodes.Nop;
                    branchInstr.Operand = null;
                    
                    instrs[ldlocIp].OpCode = OpCodes.Nop;
                    instrs[ldlocIp].Operand = null;
                    instrs[ldcIp].OpCode = OpCodes.Nop;
                    instrs[ldcIp].Operand = null;
                    instrs[cmpIp].OpCode = OpCodes.Nop;
                    instrs[cmpIp].Operand = null;
                }
                return true;
            }

            return false;
        }

        private bool SimplifyControlFlowBasic(MethodDef method)
        {
            return false;
        }

        /// <summary>
        /// Оптимизация математических выражений и сворачивание констант.
        /// </summary>
        private void OptimizeMathExpressions()
        {
            int optimizedCount = 0;

            foreach (var type in _module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody || !method.Body.IsIL) continue;

                    var body = method.Body;
                    var instrs = body.Instructions;
                    bool changed = true;

                    while (changed)
                    {
                        changed = false;

                        for (int i = 0; i < instrs.Count - 1; i++)
                        {
                            var instr = instrs[i];

                            // Паттерн: ldc.r4/r8, call Math.Ceiling/Floor/Round, conv.i4
                            if (IsLdc(instr) && i + 2 < instrs.Count)
                            {
                                var next1 = instrs[i + 1];
                                var next2 = instrs[i + 2];

                                if (next1.OpCode.Code == Code.Call && next1.Operand is IMethodDefOrRef mathMethod)
                                {
                                    string methodName = mathMethod.Name;
                                    string typeName = mathMethod.DeclaringType?.FullName ?? "";

                                    if (typeName == "System.Math" && (methodName == "Ceiling" || methodName == "Floor" || methodName == "Round"))
                                    {
                                        object? value = GetConstantValue(instr);
                                        if (value != null)
                                        {
                                            double result = 0;
                                            try
                                            {
                                                double dVal = Convert.ToDouble(value);
                                                if (methodName == "Ceiling") result = Math.Ceiling(dVal);
                                                else if (methodName == "Floor") result = Math.Floor(dVal);
                                                else if (methodName == "Round") result = Math.Round(dVal);

                                                if (i + 3 < instrs.Count && IsConversionToInt(next2))
                                                {
                                                    int finalResult = Convert.ToInt32(result);
                                                    
                                                    instr.OpCode = OpCodes.Ldc_I4;
                                                    instr.Operand = finalResult;
                                                    
                                                    next1.OpCode = OpCodes.Nop;
                                                    next1.Operand = null;
                                                    next2.OpCode = OpCodes.Nop;
                                                    next2.Operand = null;

                                                    if (i + 3 < instrs.Count && IsConversionToInt(instrs[i + 2]))
                                                    {
                                                        instrs[i + 2].OpCode = OpCodes.Nop;
                                                        instrs[i + 2].Operand = null;
                                                    }

                                                    changed = true;
                                                    optimizedCount++;
                                                    Log($"  Optimized Math.{methodName}({value}) -> {finalResult}");
                                                }
                                                else if (i + 2 < instrs.Count && next2.OpCode.Code == Code.Nop)
                                                {
                                                    instr.OpCode = OpCodes.Ldc_R8;
                                                    instr.Operand = result;
                                                    next1.OpCode = OpCodes.Nop;
                                                    next1.Operand = null;
                                                    changed = true;
                                                    optimizedCount++;
                                                }
                                            }
                                            catch { }
                                        }
                                    }
                                }
                            }

                            // Арифметические операции с константами
                            if ((instr.OpCode.Code == Code.Add || instr.OpCode.Code == Code.Sub || 
                                 instr.OpCode.Code == Code.Mul || instr.OpCode.Code == Code.Div) && i >= 2)
                            {
                                var prev1 = instrs[i - 1];
                                var prev2 = instrs[i - 2];

                                if (IsLdc(prev1) && IsLdc(prev2))
                                {
                                    object? val1 = GetConstantValue(prev2);
                                    object? val2 = GetConstantValue(prev1);

                                    if (val1 != null && val2 != null)
                                    {
                                        try
                                        {
                                            object? result = null;
                                            Code resultCode = Code.Ldc_I4;

                                            if (val1 is int i1 && val2 is int i2)
                                            {
                                                if (instr.OpCode.Code == Code.Add) result = i1 + i2;
                                                else if (instr.OpCode.Code == Code.Sub) result = i1 - i2;
                                                else if (instr.OpCode.Code == Code.Mul) result = i1 * i2;
                                                else if (instr.OpCode.Code == Code.Div && i2 != 0) result = i1 / i2;
                                            }
                                            else if ((val1 is long l1 && val2 is long l2) || 
                                                     (val1 is int i1l && val2 is long l2l))
                                            {
                                                long vl1 = Convert.ToInt64(val1);
                                                long vl2 = Convert.ToInt64(val2);
                                                if (instr.OpCode.Code == Code.Add) result = vl1 + vl2;
                                                else if (instr.OpCode.Code == Code.Sub) result = vl1 - vl2;
                                                else if (instr.OpCode.Code == Code.Mul) result = vl1 * vl2;
                                                else if (instr.OpCode.Code == Code.Div && vl2 != 0) result = vl1 / vl2;
                                                resultCode = Code.Ldc_I8;
                                            }
                                            else if ((val1 is double d1 && val2 is double d2) ||
                                                     (val1 is float f1 && val2 is float f2))
                                            {
                                                double vd1 = Convert.ToDouble(val1);
                                                double vd2 = Convert.ToDouble(val2);
                                                if (instr.OpCode.Code == Code.Add) result = vd1 + vd2;
                                                else if (instr.OpCode.Code == Code.Sub) result = vd1 - vd2;
                                                else if (instr.OpCode.Code == Code.Mul) result = vd1 * vd2;
                                                else if (instr.OpCode.Code == Code.Div && vd2 != 0) result = vd1 / vd2;
                                                resultCode = Code.Ldc_R8;
                                            }

                                            if (result != null)
                                            {
                                                prev2.OpCode = resultCode == Code.Ldc_I8 ? OpCodes.Ldc_I8 : 
                                                               resultCode == Code.Ldc_R8 ? OpCodes.Ldc_R8 : OpCodes.Ldc_I4;
                                                prev2.Operand = result;
                                                
                                                prev1.OpCode = OpCodes.Nop;
                                                prev1.Operand = null;
                                                
                                                instr.OpCode = OpCodes.Nop;
                                                instr.Operand = null;

                                                changed = true;
                                                optimizedCount++;
                                                Log($"  Folded constant: {val1} {instr.OpCode.Name} {val2} -> {result}");
                                            }
                                        }
                                        catch { }
                                    }
                                }
                            }
                        }

                        if (changed)
                        {
                            CleanupNops(method);
                            body.UpdateInstructionOffsets();
                        }
                    }
                }
            }

            Console.WriteLine($"[+] Optimized {optimizedCount} math expressions.");
        }

        private bool IsConversionToInt(Instruction instr)
        {
            return instr.OpCode.Code == Code.Conv_I4 || instr.OpCode.Code == Code.Conv_I ||
                   instr.OpCode.Code == Code.Conv_Ovf_I4 || instr.OpCode.Code == Code.Conv_Ovf_I4_Un ||
                   instr.OpCode.Code == Code.Stelem_I4 || instr.OpCode.Code == Code.Box && 
                   instr.Operand is TypeDef td && td.FullName == "System.Int32";
        }

        /// <summary>
        /// Находит и упрощает методы-обёртки.
        /// </summary>
        private void InlineTrivialWrappers()
        {
            var wrappers = new Dictionary<MethodDef, MethodDef>();
            int inlinedCount = 0;

            foreach (var type in _module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody || !method.Body.IsIL) continue;
                    if (method.Body.Instructions.Count > 10) continue;

                    var instrs = method.Body.Instructions;
                    
                    int callIndex = -1;
                    IMethodDefOrRef? targetMethod = null;

                    for (int i = 0; i < instrs.Count; i++)
                    {
                        if (instrs[i].OpCode.Code == Code.Call || instrs[i].OpCode.Code == Code.Callvirt)
                        {
                            if (callIndex == -1)
                            {
                                callIndex = i;
                                targetMethod = instrs[i].Operand as IMethodDefOrRef;
                            }
                            else
                            {
                                callIndex = -1;
                                break;
                            }
                        }
                    }

                    bool isWrapper = callIndex >= 0 && targetMethod != null;
                    if (isWrapper)
                    {
                        for (int i = callIndex + 1; i < instrs.Count; i++)
                        {
                            if (instrs[i].OpCode.Code != Code.Ret && instrs[i].OpCode.Code != Code.Nop)
                            {
                                isWrapper = false;
                                break;
                            }
                        }
                        
                        if (isWrapper)
                        {
                            for (int i = 0; i < callIndex; i++)
                            {
                                var code = instrs[i].OpCode.Code;
                                if (code != Code.Ldarg && code != Code.Ldarg_0 && code != Code.Ldarg_1 && 
                                    code != Code.Ldarg_2 && code != Code.Ldarg_3 && code != Code.Ldarg_S &&
                                    code != Code.Nop && code != Code.Stloc && code != Code.Stloc_S &&
                                    code != Code.Ldloc && code != Code.Ldloc_S && code != Code.Ldloc_0 &&
                                    code != Code.Ldloc_1 && code != Code.Ldloc_2 && code != Code.Ldloc_3)
                                {
                                    isWrapper = false;
                                    break;
                                }
                            }
                        }
                    }

                    if (isWrapper && targetMethod != null)
                    {
                        var targetDef = targetMethod.ResolveToken();
                        if (targetDef is MethodDef targetMethodDef && targetMethodDef != method)
                        {
                            wrappers[method] = targetMethodDef;
                            Log($"  Found wrapper: {method.FullName} -> {targetMethodDef.FullName}");
                        }
                    }
                }
            }

            if (wrappers.Count > 0)
            {
                // Обнаружение циклических зависимостей
                var cyclicMethods = DetectCyclicDependencies(wrappers);
                
                if (cyclicMethods.Count > 0)
                {
                    Log($"  Detected {cyclicMethods.Count} methods in cyclic dependencies, skipping them.");
                    Console.WriteLine($"[!] Skipping {cyclicMethods.Count} methods involved in cyclic dependencies.");
                    
                    // Удаляем цикличные методы из списка wrapper'ов
                    foreach (var cyclicMethod in cyclicMethods)
                    {
                        wrappers.Remove(cyclicMethod);
                    }
                }
                
                bool changed = true;
                int maxIterations = 10;
                int iteration = 0;

                while (changed && iteration < maxIterations)
                {
                    changed = false;
                    iteration++;

                    foreach (var type in _module.GetTypes())
                    {
                        foreach (var method in type.Methods)
                        {
                            if (!method.HasBody || !method.Body.IsIL) continue;

                            var instrs = method.Body.Instructions;
                            for (int i = 0; i < instrs.Count; i++)
                            {
                                if ((instrs[i].OpCode.Code == Code.Call || instrs[i].OpCode.Code == Code.Callvirt) &&
                                    instrs[i].Operand is IMethodDefOrRef calledMethod)
                                {
                                    var calledDef = calledMethod.ResolveToken();
                                    if (calledDef is MethodDef calledMethodDef && wrappers.ContainsKey(calledMethodDef))
                                    {
                                        var target = wrappers[calledMethodDef];
                                        instrs[i].Operand = target;
                                        changed = true;
                                        inlinedCount++;
                                        Log($"    Inlined: {calledMethodDef.FullName} -> {target.FullName}");
                                    }
                                }
                            }
                        }
                    }
                }
            }

            Console.WriteLine($"[+] Inlined {inlinedCount} wrapper calls across {wrappers.Count} wrapper methods.");
        }

        /// <summary>
        /// Обнаруживает методы, участвующие в циклических зависимостях.
        /// </summary>
        private HashSet<MethodDef> DetectCyclicDependencies(Dictionary<MethodDef, MethodDef> wrappers)
        {
            var cyclicMethods = new HashSet<MethodDef>();
            
            // Строим граф зависимостей: метод -> список методов, которые его используют
            var usedBy = new Dictionary<MethodDef, List<MethodDef>>();
            
            foreach (var kvp in wrappers)
            {
                var wrapper = kvp.Key;
                var target = kvp.Value;
                
                if (!usedBy.ContainsKey(target))
                    usedBy[target] = new List<MethodDef>();
                
                usedBy[target].Add(wrapper);
            }
            
            // Для каждого метода проверяем, есть ли цикл
            foreach (var startMethod in wrappers.Keys)
            {
                var visited = new HashSet<MethodDef>();
                var stack = new Stack<MethodDef>();
                stack.Push(startMethod);
                
                while (stack.Count > 0)
                {
                    var current = stack.Pop();
                    
                    if (visited.Contains(current))
                    {
                        // Если мы встретили метод, который уже посещали в текущем обходе,
                        // и это не начальный метод, проверяем дальше
                        continue;
                    }
                    
                    visited.Add(current);
                    
                    // Если текущий метод является wrapper'ом, получаем целевой метод
                    if (wrappers.ContainsKey(current))
                    {
                        var target = wrappers[current];
                        
                        // Если целевой метод - это начальный метод, нашли цикл
                        if (target == startMethod)
                        {
                            // Добавляем все методы из текущего пути в цикличные
                            foreach (var m in visited)
                                cyclicMethods.Add(m);
                            cyclicMethods.Add(startMethod);
                            break;
                        }
                        
                        // Если еще не посещали целевой метод, добавляем в стек
                        if (!visited.Contains(target))
                        {
                            stack.Push(target);
                        }
                    }
                }
            }
            
            return cyclicMethods;
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
                        method.Body.UpdateInstructionOffsets();
                    }
                }
            }
        }

        private void CleanupNops(MethodDef method)
        {
            var body = method.Body;
            bool changed = true;
            while (changed)
            {
                changed = false;
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
                        
                        if (!isTarget)
                        {
                            body.Instructions.RemoveAt(i);
                            changed = true;
                        }
                    }
                }
            }
        }

        private void RemoveUnreachableBlocks(MethodDef method)
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

        #region Renaming Logic

        private void RenameObfuscatedItems()
        {
            bool useAi = _aiConfig.Enabled;
            AiAssistant? ai = null;

            if (useAi)
            {
                Console.Write("[*] Connecting to AI... ");
                ai = new AiAssistant(_aiConfig, _debugMode);
                if (!ai.IsConnected)
                {
                    Console.WriteLine("FAILED");
                    Console.WriteLine("[!] AI connection failed. Falling back to simple renaming.");
                    useAi = false;
                    ai.Dispose();
                    ai = null;
                }
                else
                {
                    Console.WriteLine($"OK ({_aiConfig.ModelName})");
                }
            }

            int renamedCount = 0;
            int classCounter = 1;
            int methodCounter = 1;
            int fieldCounter = 1;
            int paramCounter = 1;
            int localVarCounter = 1;

            var itemsToRename = new List<(string Type, object Item, string OldName)>();
            
            foreach (var type in _module.GetTypes())
            {
                if (IsObfuscatedName(type.Name))
                    itemsToRename.Add(("Class", type, type.Name));

                foreach (var method in type.Methods)
                {
                    if (method.Name == ".cctor" || method.Name == ".ctor") continue;
                    if (IsObfuscatedName(method.Name))
                        itemsToRename.Add(("Method", method, method.Name));
                    
                    foreach (var param in method.Params)
                    {
                        if (!string.IsNullOrEmpty(param.Name) && IsObfuscatedName(param.Name))
                            itemsToRename.Add(("Param", param, param.Name));
                    }
                    
                    if (method.HasBody && method.Body.HasVariables)
                    {
                        foreach (var local in method.Body.Variables)
                        {
                            if (!string.IsNullOrEmpty(local.Name) && IsObfuscatedName(local.Name))
                                itemsToRename.Add(("Local", local, local.Name));
                        }
                    }
                }
                
                foreach (var field in type.Fields)
                {
                    if (IsObfuscatedName(field.Name))
                        itemsToRename.Add(("Field", field, field.Name));
                }
            }

            int total = itemsToRename.Count;
            int current = 0;
            int lastProgress = -1;

            foreach (var item in itemsToRename)
            {
                current++;
                int progress = (int)((current * 100) / total);
                
                if (progress % 5 == 0 && progress != lastProgress)
                {
                    lastProgress = progress;
                    Console.Write($"\r[*] Renaming: [{new string('█', progress / 5)}{new string('░', 20 - progress / 5)}] {progress}% ({current}/{total})");
                }

                if (item.Type == "Class" && item.Item is TypeDef type)
                {
                    string newName = useAi && ai != null ? GenerateAiName(ai, item.OldName, "Class", "") : $"Class_{classCounter++}";
                    type.Name = newName;
                    renamedCount++;
                }
                else if (item.Type == "Method" && item.Item is MethodDef method)
                {
                    string snippet = GetMethodSnippet(method);
                    string retType = method.ReturnType?.ToString() ?? "void";
                    
                    string newName = $"Method_{methodCounter++}";
                    if (useAi && ai != null)
                    {
                        string aiName = ai.GetSuggestedName(item.OldName, snippet, retType);
                        if (!string.IsNullOrEmpty(aiName) && aiName != item.OldName && IsValidIdentifier(aiName))
                            newName = aiName;
                    }
                    
                    method.Name = newName;
                    renamedCount++;
                }
                else if (item.Type == "Param" && item.Item is ParamDef param)
                {
                    param.Name = $"param_{paramCounter++}";
                    renamedCount++;
                }
                else if (item.Type == "Field" && item.Item is FieldDef field)
                {
                    field.Name = $"field_{fieldCounter++}";
                    renamedCount++;
                }
                else if (item.Type == "Local" && item.Item is Local local)
                {
                    local.Name = $"var_{localVarCounter++}";
                    renamedCount++;
                }
            }

            Console.WriteLine($"\r[+] Renamed {renamedCount} items.{(useAi && ai != null ? " (AI-assisted)" : "")}          ");
            ai?.Dispose();
        }

        private string GenerateAiName(AiAssistant ai, string oldName, string type, string context)
        {
            return $"Item_{oldName.GetHashCode().ToString("X").Substring(0, 4)}";
        }

        private bool IsObfuscatedName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.StartsWith("<")) return false;
            
            foreach (char c in name)
            {
                if (c > 127) return true;
                if (c == '_' || char.IsDigit(c)) continue;
                if (!char.IsLetter(c)) return true;
            }

            if (name.Length <= 2 && name.All(char.IsLetter)) return true;
            
            if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-z]{1,2}\d+$")) return true;
            
            return false;
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

        private bool IsValidIdentifier(string n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            if (!char.IsLetter(n[0]) && n[0] != '_') return false;
            foreach (var c in n) if (!char.IsLetterOrDigit(c) && c != '_') return false;
            return true;
        }

        #endregion


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
