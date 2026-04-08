using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Deobfuscator
{
    /// <summary>
    /// Анализирует цепочки вызовов методов между классами для выявления и упрощения обфусцированных паттернов.
    /// Виртуально выполняет вызовы, определяя их глубину и возможность безопасного сокращения.
    /// </summary>
    public class CallChainAnalyzer
    {
        private readonly Action<string>? _log;
        private readonly int _maxDepth;
        private readonly int _maxInstructionsPerMethod;
        
        /// <summary>
        /// Результат анализа цепочки вызовов
        /// </summary>
        public class CallChainResult
        {
            public MethodDef StartMethod { get; set; } = null!;
            public List<MethodDef> Chain { get; set; } = new();
            public int Depth { get; set; }
            public bool HasConditions { get; set; }
            public bool CanInline { get; set; }
            public string? BlockReason { get; set; }
            public Dictionary<MethodDef, List<int>> ConditionPositions { get; set; } = new();
        }

        /// <summary>
        /// Информация о состоянии при виртуальном выполнении
        /// </summary>
        private class VirtualState
        {
            public Stack<object?> Stack { get; set; } = new();
            public Dictionary<int, object?> Locals { get; set; } = new();
            public Dictionary<int, object?> Args { get; set; } = new();
            public bool HasCondition { get; set; }
            public List<int> ConditionInstructions { get; set; } = new();
        }

        public CallChainAnalyzer(Action<string>? log = null, int maxDepth = 10, int maxInstructionsPerMethod = 50)
        {
            _log = log;
            _maxDepth = maxDepth;
            _maxInstructionsPerMethod = maxInstructionsPerMethod;
        }

        /// <summary>
        /// Сканирует модуль и находит все цепочки вызовов методов
        /// </summary>
        public List<CallChainResult> ScanModule(ModuleDef module)
        {
            var results = new List<CallChainResult>();
            var processedStartMethods = new HashSet<MethodDef>();
            var allMethods = new List<MethodDef>();

            // Собираем все методы с телом
            foreach (var type in module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (method.HasBody && method.Body.IsIL && method.Body.Instructions.Count > 0)
                    {
                        allMethods.Add(method);
                    }
                }
            }

            _log?.Invoke($"Scanning {allMethods.Count} methods for call chains...");

            // Для каждого метода пытаемся построить цепочку вызовов
            foreach (var method in allMethods)
            {
                if (processedStartMethods.Contains(method))
                    continue;

                var chainResult = AnalyzeCallChain(method, allMethods);
                
                if (chainResult != null && chainResult.Chain.Count > 0)
                {
                    results.Add(chainResult);
                    
                    // Помечаем все методы в цепочке как обработанные
                    foreach (var chainMethod in chainResult.Chain)
                    {
                        processedStartMethods.Add(chainMethod);
                    }
                    processedStartMethods.Add(method);
                }
            }

            _log?.Invoke($"Found {results.Count} call chains.");
            return results;
        }

        /// <summary>
        /// Анализирует цепочку вызовов начиная с указанного метода
        /// </summary>
        private CallChainResult? AnalyzeCallChain(MethodDef startMethod, List<MethodDef> allMethods)
        {
            if (!startMethod.HasBody || !startMethod.Body.IsIL)
                return null;

            var result = new CallChainResult
            {
                StartMethod = startMethod
            };

            var visitedMethods = new HashSet<MethodDef>();
            var chainStack = new Stack<(MethodDef method, int depth, VirtualState state)>();
            
            var initialState = new VirtualState();
            InitializeVirtualState(initialState, startMethod);

            chainStack.Push((startMethod, 0, initialState));
            visitedMethods.Add(startMethod);

            while (chainStack.Count > 0)
            {
                var (currentMethod, currentDepth, currentState) = chainStack.Pop();

                if (currentDepth >= _maxDepth)
                {
                    result.BlockReason = $"Max depth ({_maxDepth}) reached";
                    break;
                }

                // Виртуальное выполнение метода
                var executionResult = VirtualExecuteMethod(currentMethod, currentState, allMethods);

                if (executionResult == null)
                {
                    result.BlockReason = $"Failed to execute {currentMethod.FullName}";
                    break;
                }

                if (executionResult.HasCondition)
                {
                    result.HasConditions = true;
                    result.ConditionPositions[currentMethod] = executionResult.ConditionInstructions;
                }

                // Если метод вызывает другой метод, добавляем его в цепочку
                if (executionResult.CalledMethod != null)
                {
                    var calledDef = executionResult.CalledMethod.ResolveToken();
                    
                    if (calledDef is MethodDef calledMethodDef)
                    {
                        if (visitedMethods.Contains(calledMethodDef))
                        {
                            result.BlockReason = "Circular dependency detected";
                            break;
                        }

                        if (!IsValidForInlining(calledMethodDef))
                        {
                            result.BlockReason = $"Method {calledMethodDef.FullName} is not suitable for inlining";
                            break;
                        }

                        result.Chain.Add(calledMethodDef);
                        visitedMethods.Add(calledMethodDef);

                        var newState = CloneVirtualState(executionResult.StateAfterCall);
                        chainStack.Push((calledMethodDef, currentDepth + 1, newState));
                    }
                }
                else
                {
                    // Конец цепочки - метод не вызывает других методов или возвращает значение
                    break;
                }
            }

            result.Depth = result.Chain.Count;
            
            // Определяем возможность инлайна
            result.CanInline = CanInlineChain(result);

            _log?.Invoke($"Chain from {startMethod.Name}: depth={result.Depth}, hasConditions={result.HasConditions}, canInline={result.CanInline}");

            return result;
        }

        /// <summary>
        /// Результат виртуального выполнения метода
        /// </summary>
        private class ExecutionResult
        {
            public IMethodDefOrRef? CalledMethod { get; set; }
            public VirtualState StateAfterCall { get; set; } = null!;
            public bool HasCondition { get; set; }
            public List<int> ConditionInstructions { get; set; } = new();
            public bool CompletedSuccessfully { get; set; }
        }

        /// <summary>
        /// Виртуально выполняет метод, отслеживая вызовы других методов и условия
        /// </summary>
        private ExecutionResult? VirtualExecuteMethod(MethodDef method, VirtualState initialState, List<MethodDef> allMethods)
        {
            if (!method.HasBody || !method.Body.IsIL)
                return null;

            var instructions = method.Body.Instructions;
            
            if (instructions.Count > _maxInstructionsPerMethod)
            {
                _log?.Invoke($"  Skipping {method.Name}: too many instructions ({instructions.Count})");
                return null;
            }

            var state = CloneVirtualState(initialState);
            var result = new ExecutionResult
            {
                StateAfterCall = state
            };

            var instructionIndexMap = new Dictionary<Instruction, int>();
            for (int i = 0; i < instructions.Count; i++)
            {
                instructionIndexMap[instructions[i]] = i;
            }

            int ip = 0;
            int maxIterations = instructions.Count * 3; // Защита от бесконечных циклов
            int iterations = 0;

            while (ip >= 0 && ip < instructions.Count && iterations < maxIterations)
            {
                iterations++;
                var instr = instructions[ip];
                var opcode = instr.OpCode;

                try
                {
                    switch (opcode.Code)
                    {
                        case Code.Ldarg_0:
                        case Code.Ldarg_1:
                        case Code.Ldarg_2:
                        case Code.Ldarg_3:
                        case Code.Ldarg_S:
                        case Code.Ldarg:
                            int argIndex = GetArgumentIndex(instr);
                            if (state.Args.TryGetValue(argIndex, out var argValue))
                                state.Stack.Push(argValue);
                            else
                                state.Stack.Push(null);
                            break;

                        case Code.Ldc_I4:
                        case Code.Ldc_I4_S:
                        case Code.Ldc_I8:
                        case Code.Ldc_R4:
                        case Code.Ldc_R8:
                            state.Stack.Push(GetConstantValue(instr));
                            break;

                        case Code.Ldloc_0:
                        case Code.Ldloc_1:
                        case Code.Ldloc_2:
                        case Code.Ldloc_3:
                        case Code.Ldloc_S:
                        case Code.Ldloc:
                            int localIndex = GetLocalIndex(instr);
                            if (state.Locals.TryGetValue(localIndex, out var localValue))
                                state.Stack.Push(localValue);
                            else
                                state.Stack.Push(null);
                            break;

                        case Code.Stloc_0:
                        case Code.Stloc_1:
                        case Code.Stloc_2:
                        case Code.Stloc_3:
                        case Code.Stloc_S:
                        case Code.Stloc:
                            int storeIndex = GetLocalIndex(instr);
                            if (state.Stack.Count > 0)
                                state.Locals[storeIndex] = state.Stack.Pop();
                            break;

                        case Code.Call:
                        case Code.Callvirt:
                            if (instr.Operand is IMethodDefOrRef calledMethod)
                            {
                                result.CalledMethod = calledMethod;
                                result.CompletedSuccessfully = true;
                                return result;
                            }
                            break;

                        case Code.Br_S:
                        case Code.Br:
                            if (instr.Operand is Instruction target)
                            {
                                ip = instructionIndexMap[target];
                                continue;
                            }
                            break;

                        case Code.Brtrue_S:
                        case Code.Brtrue:
                        case Code.Brfalse_S:
                        case Code.Brfalse:
                            result.HasCondition = true;
                            result.ConditionInstructions.Add(ip);
                            
                            if (state.Stack.Count > 0)
                            {
                                var conditionValue = state.Stack.Pop();
                                bool isTrue = conditionValue is bool b ? b : conditionValue != null;
                                
                                if ((opcode.Code == Code.Brtrue_S || opcode.Code == Code.Brtrue) == isTrue)
                                {
                                    if (instr.Operand is Instruction brTarget)
                                    {
                                        ip = instructionIndexMap[brTarget];
                                        continue;
                                    }
                                }
                            }
                            break;

                        case Code.Beq_S:
                        case Code.Beq:
                        case Code.Bne_Un_S:
                        case Code.Bne_Un:
                        case Code.Blt_S:
                        case Code.Blt:
                        case Code.Blt_Un_S:
                        case Code.Blt_Un:
                        case Code.Bgt_S:
                        case Code.Bgt:
                        case Code.Bgt_Un_S:
                        case Code.Bgt_Un:
                        case Code.Ble_S:
                        case Code.Ble:
                        case Code.Ble_Un_S:
                        case Code.Ble_Un:
                        case Code.Bge_S:
                        case Code.Bge:
                        case Code.Bge_Un_S:
                        case Code.Bge_Un:
                            result.HasCondition = true;
                            result.ConditionInstructions.Add(ip);
                            
                            // Для упрощения считаем, что условие может пойти по любому пути
                            // Это консервативный подход
                            if (instr.Operand is Instruction condTarget)
                            {
                                // Продолжаем по следующему пути (fall-through)
                                // Реальный анализ всех путей был бы сложнее
                            }
                            break;

                        case Code.Ret:
                            result.CompletedSuccessfully = true;
                            return result;

                        case Code.Nop:
                        case Code.Pop:
                            if (state.Stack.Count > 0 && opcode.Code == Code.Pop)
                                state.Stack.Pop();
                            break;

                        case Code.Dup:
                            if (state.Stack.Count > 0)
                            {
                                var top = state.Stack.Peek();
                                state.Stack.Push(top);
                            }
                            break;

                        case Code.Add:
                            if (state.Stack.Count >= 2)
                            {
                                var b = state.Stack.Pop();
                                var a = state.Stack.Pop();
                                state.Stack.Push(ComputeBinaryOp(a, b, AddValues));
                            }
                            break;

                        case Code.Sub:
                            if (state.Stack.Count >= 2)
                            {
                                var b = state.Stack.Pop();
                                var a = state.Stack.Pop();
                                state.Stack.Push(ComputeBinaryOp(a, b, SubValues));
                            }
                            break;

                        case Code.Mul:
                            if (state.Stack.Count >= 2)
                            {
                                var b = state.Stack.Pop();
                                var a = state.Stack.Pop();
                                state.Stack.Push(ComputeBinaryOp(a, b, MulValues));
                            }
                            break;

                        case Code.Div:
                        case Code.Div_Un:
                            if (state.Stack.Count >= 2)
                            {
                                var b = state.Stack.Pop();
                                var a = state.Stack.Pop();
                                state.Stack.Push(ComputeBinaryOp(a, b, DivValues));
                            }
                            break;

                        case Code.Ceq:
                            if (state.Stack.Count >= 2)
                            {
                                var b = state.Stack.Pop();
                                var a = state.Stack.Pop();
                                state.Stack.Push(Equals(a, b));
                            }
                            break;

                        case Code.Cgt:
                        case Code.Cgt_Un:
                            if (state.Stack.Count >= 2)
                            {
                                var b = state.Stack.Pop();
                                var a = state.Stack.Pop();
                                state.Stack.Push(CompareGreater(a, b));
                            }
                            break;

                        case Code.Clt:
                        case Code.Clt_Un:
                            if (state.Stack.Count >= 2)
                            {
                                var b = state.Stack.Pop();
                                var a = state.Stack.Pop();
                                state.Stack.Push(CompareLess(a, b));
                            }
                            break;

                        case Code.And:
                            if (state.Stack.Count >= 2)
                            {
                                var b = state.Stack.Pop();
                                var a = state.Stack.Pop();
                                state.Stack.Push(ComputeBinaryOp(a, b, AndValues));
                            }
                            break;

                        case Code.Or:
                            if (state.Stack.Count >= 2)
                            {
                                var b = state.Stack.Pop();
                                var a = state.Stack.Pop();
                                state.Stack.Push(ComputeBinaryOp(a, b, OrValues));
                            }
                            break;

                        case Code.Xor:
                            if (state.Stack.Count >= 2)
                            {
                                var b = state.Stack.Pop();
                                var a = state.Stack.Pop();
                                state.Stack.Push(ComputeBinaryOp(a, b, XorValues));
                            }
                            break;

                        case Code.Not:
                            if (state.Stack.Count >= 1)
                            {
                                var a = state.Stack.Pop();
                                state.Stack.Push(NotValue(a));
                            }
                            break;

                        case Code.Neg:
                            if (state.Stack.Count >= 1)
                            {
                                var a = state.Stack.Pop();
                                state.Stack.Push(NegateValue(a));
                            }
                            break;

                        case Code.Conv_I4:
                        case Code.Conv_I8:
                        case Code.Conv_R4:
                        case Code.Conv_R8:
                        case Code.Conv_U4:
                        case Code.Conv_U8:
                            if (state.Stack.Count >= 1)
                            {
                                var a = state.Stack.Pop();
                                state.Stack.Push(ConvertValue(a, opcode.Code));
                            }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"  Error executing instruction at {ip}: {ex.Message}");
                    result.CompletedSuccessfully = false;
                    return result;
                }

                ip++;
            }

            result.CompletedSuccessfully = iterations < maxIterations;
            return result;
        }

        #region Helper Methods

        private void InitializeVirtualState(VirtualState state, MethodDef method)
        {
            // Инициализируем аргументы нулевыми значениями
            for (int i = 0; i < method.Parameters.Count; i++)
            {
                state.Args[i] = GetDefaultValue(method.Parameters[i].Type);
            }

            // Инициализируем локальные переменные
            if (method.Body.Variables != null)
            {
                for (int i = 0; i < method.Body.Variables.Count; i++)
                {
                    state.Locals[i] = GetDefaultValue(method.Body.Variables[i].Type);
                }
            }
        }

        private VirtualState CloneVirtualState(VirtualState original)
        {
            return new VirtualState
            {
                Stack = new Stack<object?>(original.Stack.Reverse()),
                Locals = new Dictionary<int, object?>(original.Locals),
                Args = new Dictionary<int, object?>(original.Args),
                HasCondition = original.HasCondition,
                ConditionInstructions = new List<int>(original.ConditionInstructions)
            };
        }

        private object? GetDefaultValue(TypeSig type)
        {
            if (type == null) return null;
            
            if (type.IsValueType)
            {
                if (type.IsInteger || type.IsPrimitive)
                    return 0;
                if (type.IsFloat)
                    return 0.0;
                if (type.FullName == "System.Boolean")
                    return false;
            }
            
            return null;
        }

        private int GetArgumentIndex(Instruction instr)
        {
            switch (instr.OpCode.Code)
            {
                case Code.Ldarg_0: return 0;
                case Code.Ldarg_1: return 1;
                case Code.Ldarg_2: return 2;
                case Code.Ldarg_3: return 3;
                case Code.Ldarg_S:
                case Code.Ldarg:
                    if (instr.Operand is Parameter param)
                        return param.Index;
                    if (instr.Operand is int idx)
                        return idx;
                    break;
            }
            return 0;
        }

        private int GetLocalIndex(Instruction instr)
        {
            switch (instr.OpCode.Code)
            {
                case Code.Ldloc_0:
                case Code.Stloc_0: return 0;
                case Code.Ldloc_1:
                case Code.Stloc_1: return 1;
                case Code.Ldloc_2:
                case Code.Stloc_2: return 2;
                case Code.Ldloc_3:
                case Code.Stloc_3: return 3;
                case Code.Ldloc_S:
                case Code.Ldloc:
                case Code.Stloc_S:
                case Code.Stloc:
                    if (instr.Operand is Local local)
                        return local.Index;
                    if (instr.Operand is int idx)
                        return idx;
                    break;
            }
            return 0;
        }

        private object? GetConstantValue(Instruction instr)
        {
            switch (instr.OpCode.Code)
            {
                case Code.Ldc_I4:
                case Code.Ldc_I4_S:
                    return instr.Operand as int?;
                case Code.Ldc_I8:
                    return instr.Operand as long?;
                case Code.Ldc_R4:
                    return instr.Operand as float?;
                case Code.Ldc_R8:
                    return instr.Operand as double?;
                default:
                    return null;
            }
        }

        private object? ComputeBinaryOp(object? a, object? b, Func<object?, object?, object?> operation)
        {
            if (a == null || b == null) return null;
            return operation(a, b);
        }

        private object? AddValues(object? a, object? b)
        {
            try
            {
                if (a is double da && b is double db) return da + db;
                if (a is float fa && b is float fb) return fa + fb;
                if (a is long la && b is long lb) return la + lb;
                if (a is int ia && b is int ib) return ia + ib;
                return Convert.ToDouble(a) + Convert.ToDouble(b);
            }
            catch { return null; }
        }

        private object? SubValues(object? a, object? b)
        {
            try
            {
                if (a is double da && b is double db) return da - db;
                if (a is float fa && b is float fb) return fa - fb;
                if (a is long la && b is long lb) return la - lb;
                if (a is int ia && b is int ib) return ia - ib;
                return Convert.ToDouble(a) - Convert.ToDouble(b);
            }
            catch { return null; }
        }

        private object? MulValues(object? a, object? b)
        {
            try
            {
                if (a is double da && b is double db) return da * db;
                if (a is float fa && b is float fb) return fa * fb;
                if (a is long la && b is long lb) return la * lb;
                if (a is int ia && b is int ib) return ia * ib;
                return Convert.ToDouble(a) * Convert.ToDouble(b);
            }
            catch { return null; }
        }

        private object? DivValues(object? a, object? b)
        {
            try
            {
                if (b is double db && db == 0) return null;
                if (b is float fb && fb == 0) return null;
                if (b is int ib && ib == 0) return null;
                
                if (a is double da && b is double ddb) return da / ddb;
                if (a is float fa && b is float ffb) return fa / ffb;
                if (a is long la && b is long llb) return la / llb;
                if (a is int ia && b is int iib) return ia / iib;
                return Convert.ToDouble(a) / Convert.ToDouble(b);
            }
            catch { return null; }
        }

        private object? AndValues(object? a, object? b)
        {
            try
            {
                if (a is long la && b is long lb) return la & lb;
                if (a is int ia && b is int ib) return ia & ib;
                return Convert.ToInt64(a) & Convert.ToInt64(b);
            }
            catch { return null; }
        }

        private object? OrValues(object? a, object? b)
        {
            try
            {
                if (a is long la && b is long lb) return la | lb;
                if (a is int ia && b is int ib) return ia | ib;
                return Convert.ToInt64(a) | Convert.ToInt64(b);
            }
            catch { return null; }
        }

        private object? XorValues(object? a, object? b)
        {
            try
            {
                if (a is long la && b is long lb) return la ^ lb;
                if (a is int ia && b is int ib) return ia ^ ib;
                return Convert.ToInt64(a) ^ Convert.ToInt64(b);
            }
            catch { return null; }
        }

        private object? NotValue(object? a)
        {
            try
            {
                if (a is long la) return ~la;
                if (a is int ia) return ~ia;
                return ~Convert.ToInt64(a);
            }
            catch { return null; }
        }

        private object? NegateValue(object? a)
        {
            try
            {
                if (a is double da) return -da;
                if (a is float fa) return -fa;
                if (a is long la) return -la;
                if (a is int ia) return -ia;
                return -Convert.ToDouble(a);
            }
            catch { return null; }
        }

        private object? ConvertValue(object? a, Code convCode)
        {
            try
            {
                switch (convCode)
                {
                    case Code.Conv_I4:
                        return Convert.ToInt32(a);
                    case Code.Conv_I8:
                        return Convert.ToInt64(a);
                    case Code.Conv_R4:
                        return Convert.ToSingle(a);
                    case Code.Conv_R8:
                        return Convert.ToDouble(a);
                    case Code.Conv_U4:
                        return Convert.ToUInt32(a);
                    case Code.Conv_U8:
                        return Convert.ToUInt64(a);
                    default:
                        return a;
                }
            }
            catch { return null; }
        }

        private bool Equals(object? a, object? b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            return a.Equals(b);
        }

        private bool CompareGreater(object? a, object? b)
        {
            try
            {
                double da = Convert.ToDouble(a);
                double db = Convert.ToDouble(b);
                return da > db;
            }
            catch { return false; }
        }

        private bool CompareLess(object? a, object? b)
        {
            try
            {
                double da = Convert.ToDouble(a);
                double db = Convert.ToDouble(b);
                return da < db;
            }
            catch { return false; }
        }

        #endregion

        /// <summary>
        /// Проверяет, подходит ли метод для инлайна
        /// </summary>
        private bool IsValidForInlining(MethodDef method)
        {
            if (!method.HasBody || !method.Body.IsIL)
                return false;

            if (method.Body.Instructions.Count > _maxInstructionsPerMethod)
                return false;

            // Не инлайним методы с exception handling
            if (method.Body.ExceptionHandlers.Count > 0)
                return false;

            return true;
        }

        /// <summary>
        /// Определяет, можно ли безопасно заинлайнить всю цепочку
        /// </summary>
        private bool CanInlineChain(CallChainResult result)
        {
            if (!result.CanInline)
                return false;

            // Если есть сложные условия, требующие анализа путей
            if (result.HasConditions)
            {
                // Проверяем, насколько сложны условия
                int totalConditions = result.ConditionPositions.Sum(kvp => kvp.Value.Count);
                
                // Если условий слишком много, инлайн может быть небезопасен
                if (totalConditions > 10)
                    return false;
            }

            // Проверяем стек: должен оставаться сбалансированным
            foreach (var method in result.Chain)
            {
                if (!IsStackBalanced(method))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Проверяет, сбалансирован ли стек в методе (количество push == количество pop)
        /// </summary>
        private bool IsStackBalanced(MethodDef method)
        {
            if (!method.HasBody || !method.Body.IsIL)
                return false;

            var instructions = method.Body.Instructions;
            int stackDelta = 0;
            int minStackDepth = 0;

            foreach (var instr in instructions)
            {
                int delta = GetStackDelta(instr);
                stackDelta += delta;
                minStackDepth = Math.Min(minStackDepth, stackDelta);
            }

            // Стек не должен уходить в минус и должен быть сбалансирован к концу
            return minStackDepth >= 0;
        }

        /// <summary>
        /// Возвращает изменение глубины стека для данной инструкции
        /// </summary>
        private int GetStackDelta(Instruction instr)
        {
            var opcode = instr.OpCode;
            
            // Получаем количество элементов, которые инструкция забирает со стека и кладет на стек
            int popCount = opcode.StackBehaviourPop == StackBehaviour.Pop0 ? 0 :
                          opcode.StackBehaviourPop == StackBehaviour.Pop1 ? 1 :
                          opcode.StackBehaviourPop == StackBehaviour.Pop1_pop1 ? 2 :
                          opcode.StackBehaviourPop == StackBehaviour.Varpop ? 1 : 0;

            int pushCount = opcode.StackBehaviourPush == StackBehaviour.Push0 ? 0 :
                           opcode.StackBehaviourPush == StackBehaviour.Push1 ? 1 :
                           opcode.StackBehaviourPush == StackBehaviour.Push1_push1 ? 2 :
                           opcode.StackBehaviourPush == StackBehaviour.Varpush ? 1 : 0;

            return pushCount - popCount;
        }

        /// <summary>
        /// Применяет инлайн к найденным цепочкам в модуле
        /// </summary>
        public int ApplyInlining(ModuleDef module, List<CallChainResult> chains)
        {
            int inlinedCount = 0;

            foreach (var chain in chains)
            {
                if (!chain.CanInline || chain.Chain.Count == 0)
                    continue;

                _log?.Invoke($"Applying inlining for chain starting at {chain.StartMethod.FullName}");

                // Заменяем вызовы в начальном методе на вызовы конечного метода в цепочке
                var finalMethod = chain.Chain.LastOrDefault();
                if (finalMethod == null)
                    continue;

                foreach (var type in module.GetTypes())
                {
                    foreach (var method in type.Methods)
                    {
                        if (!method.HasBody || !method.Body.IsIL)
                            continue;

                        var instrs = method.Body.Instructions;
                        
                        for (int i = 0; i < instrs.Count; i++)
                        {
                            if ((instrs[i].OpCode.Code == Code.Call || instrs[i].OpCode.Code == Code.Callvirt) &&
                                instrs[i].Operand is IMethodDefOrRef calledMethod)
                            {
                                var calledDef = calledMethod.ResolveToken();
                                
                                // Если вызывается метод из цепочки
                                if (calledDef == chain.StartMethod || chain.Chain.Contains(calledDef as MethodDef))
                                {
                                    // Заменяем на вызов финального метода
                                    instrs[i].Operand = finalMethod;
                                    inlinedCount++;
                                    _log?.Invoke($"  Replaced call to {calledDef?.Name} with {finalMethod.Name}");
                                }
                            }
                        }
                    }
                }
            }

            Console.WriteLine($"[+] Inlined {inlinedCount} calls across {chains.Count} chains.");
            return inlinedCount;
        }

        /// <summary>
        /// Генерирует отчет о найденных цепочках для анализа в dnSpy
        /// </summary>
        public string GenerateReport(List<CallChainResult> chains)
        {
            var report = new System.Text.StringBuilder();
            report.AppendLine("=== Call Chain Analysis Report ===\n");

            foreach (var chain in chains)
            {
                report.AppendLine($"Chain starting: {chain.StartMethod.FullName}");
                report.AppendLine($"  Depth: {chain.Depth}");
                report.AppendLine($"  Has Conditions: {chain.HasConditions}");
                report.AppendLine($"  Can Inline: {chain.CanInline}");
                
                if (!string.IsNullOrEmpty(chain.BlockReason))
                {
                    report.AppendLine($"  Blocked: {chain.BlockReason}");
                }

                if (chain.Chain.Count > 0)
                {
                    report.AppendLine("  Chain:");
                    for (int i = 0; i < chain.Chain.Count; i++)
                    {
                        report.AppendLine($"    [{i}] {chain.Chain[i].FullName}");
                    }
                }

                if (chain.HasConditions && chain.ConditionPositions.Count > 0)
                {
                    report.AppendLine("  Condition positions:");
                    foreach (var kvp in chain.ConditionPositions)
                    {
                        report.AppendLine($"    {kvp.Key.Name}: [{string.Join(", ", kvp.Value)}]");
                    }
                }

                report.AppendLine();
            }

            report.AppendLine($"Total chains: {chains.Count}");
            report.AppendLine($"Chains suitable for inlining: {chains.Count(c => c.CanInline)}");

            return report.ToString();
        }
    }
}
