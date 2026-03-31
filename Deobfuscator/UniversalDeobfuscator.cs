using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Deobfuscator;

/// <summary>
/// Universal deobfuscator that handles various obfuscation techniques
/// including control flow flattening, goto unraveling, and constant condition simplification
/// Supports AI-assisted renaming via Ollama and other local LLM servers
/// </summary>
public class UniversalDeobfuscator
{
    private readonly ModuleDefMD _module;
    private readonly CorLibTypes _corLibTypes;
    private readonly AiAssistant? _aiAssistant;
    private readonly bool _useAi;

    public UniversalDeobfuscator(ModuleDefMD module, bool useAi = false, string aiBaseUrl = "http://localhost:11434", string aiModel = "codellama")
    {
        _module = module;
        _corLibTypes = module.CorLibTypes;
        
        if (useAi)
        {
            try
            {
                _aiAssistant = new AiAssistant(aiBaseUrl, aiModel);
                _useAi = true;
                Console.WriteLine($"[AI] Connected to AI assistant at {aiBaseUrl} using model: {aiModel}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AI] Failed to initialize AI assistant: {ex.Message}");
                _useAi = false;
                _aiAssistant = null;
            }
        }
        else
        {
            _useAi = false;
            _aiAssistant = null;
        }
    }

    public async Task ProcessAsync()
    {
        Console.WriteLine("Starting universal deobfuscation...");
        
        foreach (var type in _module.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody || !method.Body.HasInstructions)
                    continue;

                try
                {
                    SimplifyConstantConditions(method);
                    UnravelControlFlow(method);
                    RemoveDeadCode(method);
                    SimplifyBranches(method);
                    
                    if (_useAi && _aiAssistant != null)
                    {
                        await ApplyAiRenamingAsync(method);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error processing method {method.FullName}: {ex.Message}");
                }
            }
        }
        
        Console.WriteLine("Deobfuscation completed.");
    }
    
    private async Task ApplyAiRenamingAsync(MethodDef method)
    {
        if (!_useAi || _aiAssistant == null) return;
        
        // Пропускаем очень короткие методы (менее 5 инструкций)
        if (method.Body.Instructions.Count < 5) return;
        
        // Генерируем IL код для анализа
        var ilCode = GenerateIlCode(method);
        
        Console.WriteLine($"[AI] Analyzing method: {method.Name}");
        
        try
        {
            var result = await _aiAssistant.AnalyzeMethodAsync(method.Name.ToString(), ilCode);
            
            if (result != null)
            {
                // Переименовываем метод если предложено новое имя
                if (!string.IsNullOrEmpty(result.methodName) && result.methodName != method.Name.ToString())
                {
                    Console.WriteLine($"[AI] Renaming method '{method.Name}' -> '{result.methodName}'");
                    method.Name = result.methodName;
                }
                
                // Переименовываем локальные переменные если предложены имена
                if (result.variables != null && method.Body.HasVariables)
                {
                    foreach (var kvp in result.variables)
                    {
                        if (int.TryParse(kvp.Key.Replace("V_", ""), out int varIndex) && 
                            varIndex >= 0 && varIndex < method.Body.Variables.Count)
                        {
                            var variable = method.Body.Variables[varIndex];
                            if (!string.IsNullOrEmpty(kvp.Value))
                            {
                                Console.WriteLine($"[AI] Renaming variable V_{varIndex} -> '{kvp.Value}'");
                                variable.Name = kvp.Value;
                            }
                        }
                    }
                }
                
                // Добавляем комментарий если есть
                if (!string.IsNullOrEmpty(result.comment))
                {
                    Console.WriteLine($"[AI] Comment: {result.comment}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AI] Error during analysis of {method.Name}: {ex.Message}");
        }
    }
    
    private string GenerateIlCode(MethodDef method)
    {
        var sb = new System.Text.StringBuilder();
        
        // Добавляем сигнатуру метода
        sb.AppendLine($"// Method: {method.FullName}");
        sb.AppendLine($"// Signature: {method.Signature}");
        sb.AppendLine();
        
        // Добавляем переменные
        if (method.Body.HasVariables)
        {
            sb.AppendLine("// Locals:");
            foreach (var var in method.Body.Variables)
            {
                sb.AppendLine($"//   {var.Index}: {var.Type}");
            }
            sb.AppendLine();
        }
        
        // Добавляем инструкции
        sb.AppendLine("// Instructions:");
        foreach (var instr in method.Body.Instructions)
        {
            sb.AppendLine($"//   {instr.Offset:X4}: {instr.OpCode.Name} {FormatOperand(instr.Operand)}");
        }
        
        return sb.ToString();
    }
    
    private string FormatOperand(object? operand)
    {
        if (operand == null) return "";
        return operand switch
        {
            Instruction i => $"IL_{i.Offset:X4}",
            Local l => $"V_{l.Index}",
            Parameter p => p.Name,
            ITypeDefOrRef t => t.FullName,
            MethodDef m => m.FullName,
            FieldDef f => f.FullName,
            string s => $"\"{s}\"",
            _ => operand.ToString() ?? ""
        };
    }

    private void SimplifyConstantConditions(MethodDef method)
    {
        var instructions = method.Body.Instructions;
        var localConstants = new Dictionary<int, object>();
        bool changed = true;
        
        while (changed)
        {
            changed = false;
            localConstants.Clear();
            
            for (int i = 0; i < instructions.Count; i++)
            {
                var instr = instructions[i];
                
                if (instr.OpCode.Code == Code.Ldc_I4 && instr.Operand is int intValue)
                {
                    if (i + 1 < instructions.Count && IsStLoc(instructions[i + 1], out int localIndex))
                    {
                        localConstants[localIndex] = intValue;
                        changed = true;
                    }
                }
                else if (instr.OpCode.Code == Code.Ldc_I8 && instr.Operand is long longValue)
                {
                    if (i + 1 < instructions.Count && IsStLoc(instructions[i + 1], out int localIndex2))
                    {
                        localConstants[localIndex2] = longValue;
                        changed = true;
                    }
                }
                else if (instr.OpCode.Code == Code.Ldc_R4 && instr.Operand is float floatValue)
                {
                    if (i + 1 < instructions.Count && IsStLoc(instructions[i + 1], out int localIndex3))
                    {
                        localConstants[localIndex3] = floatValue;
                        changed = true;
                    }
                }
                else if (instr.OpCode.Code == Code.Ldc_R8 && instr.Operand is double doubleValue)
                {
                    if (i + 1 < instructions.Count && IsStLoc(instructions[i + 1], out int localIndex4))
                    {
                        localConstants[localIndex4] = doubleValue;
                        changed = true;
                    }
                }
                
                if (IsStLoc(instr, out int stLocalIndex) && i > 0 && !IsLdc(instructions[i - 1]))
                {
                    localConstants.Remove(stLocalIndex);
                }
            }
            
            for (int i = 0; i < instructions.Count - 2; i++)
            {
                var instr = instructions[i];
                
                if (IsLdLoc(instr, out int loadIndex) && localConstants.ContainsKey(loadIndex))
                {
                    if (i + 1 < instructions.Count && IsLdc(instructions[i + 1]))
                    {
                        var constValue = localConstants[loadIndex];
                        var compareValue = GetLdcValue(instructions[i + 1]);
                        
                        if (i + 2 < instructions.Count && IsComparisonOp(instructions[i + 2].OpCode.Code))
                        {
                            var comparisonOp = instructions[i + 2].OpCode.Code;
                            bool result = EvaluateComparison(constValue, compareValue, comparisonOp);
                            ReplaceComparisonWithResult(instructions, i, result);
                            changed = true;
                            break;
                        }
                    }
                }
            }
        }
    }

    private void UnravelControlFlow(MethodDef method)
    {
        var instructions = method.Body.Instructions;
        bool changed = true;
        int iterations = 0;
        
        while (changed && iterations++ < 100)
        {
            changed = false;
            changed |= SimplifyGotoChains(instructions);
            changed |= SimplifyWhileLoops(instructions, method);
            changed |= SimplifySwitchStatements(instructions);
        }
        
        RebuildExceptionHandlers(method);
    }

    private bool SimplifyGotoChains(IList<Instruction> instructions)
    {
        bool changed = false;
        
        for (int i = 0; i < instructions.Count; i++)
        {
            var instr = instructions[i];
            
            if (instr.OpCode.FlowControl == FlowControl.Branch && instr.Operand is Instruction target)
            {
                if (target.OpCode.FlowControl == FlowControl.Branch && target.Operand is Instruction nextTarget)
                {
                    while (nextTarget.OpCode.Code == Code.Nop && nextTarget.Next != null)
                        nextTarget = nextTarget.Next;
                    
                    if (nextTarget != target)
                    {
                        instr.Operand = nextTarget;
                        changed = true;
                    }
                }
                else if (target.OpCode.Code == Code.Ret)
                {
                    instr.OpCode = OpCodes.Ret;
                    instr.Operand = null;
                    changed = true;
                }
            }
        }
        
        return changed;
    }

    private bool SimplifyWhileLoops(IList<Instruction> instructions, MethodDef method)
    {
        bool changed = false;
        
        for (int i = 0; i < instructions.Count - 3; i++)
        {
            var instr = instructions[i];
            
            if (IsLdc(instr) && instructions[i + 1].OpCode.FlowControl == FlowControl.Cond_Branch)
            {
                var conditionValue = GetLdcValue(instr);
                var branchInstr = instructions[i + 1];
                bool isTrueBranch = branchInstr.OpCode.Code == Code.Brtrue || branchInstr.OpCode.Code == Code.Brtrue_S;
                
                if (conditionValue is int intValue || conditionValue is long longValue ||
                    conditionValue is float floatValue || conditionValue is double doubleValue)
                {
                    bool isTruthy = conditionValue switch
                    {
                        int iv => iv != 0,
                        long lv => lv != 0,
                        float fv => fv != 0f,
                        double dv => dv != 0.0,
                        _ => true
                    };
                    
                    if ((isTruthy && isTrueBranch) || (!isTruthy && !isTrueBranch))
                    {
                        if (branchInstr.Operand is Instruction target)
                        {
                            instr.OpCode = OpCodes.Br;
                            instr.Operand = target;
                            branchInstr.OpCode = OpCodes.Nop;
                            branchInstr.Operand = null;
                            changed = true;
                        }
                    }
                    else
                    {
                        branchInstr.OpCode = OpCodes.Nop;
                        branchInstr.Operand = null;
                        changed = true;
                    }
                }
            }
        }
        
        return changed;
    }

    private bool SimplifySwitchStatements(IList<Instruction> instructions)
    {
        bool changed = false;
        
        for (int i = 0; i < instructions.Count - 1; i++)
        {
            var instr = instructions[i];
            
            if (IsLdc(instr) && instructions[i + 1].OpCode.Code == Code.Switch)
            {
                var value = GetLdcValue(instr);
                var targets = instructions[i + 1].Operand as IList<Instruction>;
                
                if (targets != null && value is int intValue)
                {
                    if (intValue >= 0 && intValue < targets.Count)
                    {
                        instr.OpCode = OpCodes.Br;
                        instr.Operand = targets[intValue];
                        instructions[i + 1].OpCode = OpCodes.Nop;
                        instructions[i + 1].Operand = null;
                        changed = true;
                    }
                    else
                    {
                        instructions[i + 1].OpCode = OpCodes.Nop;
                        instructions[i + 1].Operand = null;
                        changed = true;
                    }
                }
            }
        }
        
        return changed;
    }

    private void RemoveDeadCode(MethodDef method)
    {
        var instructions = method.Body.Instructions;
        var reachable = new HashSet<Instruction>();
        var worklist = new Stack<Instruction>();
        
        worklist.Push(instructions[0]);
        
        while (worklist.Count > 0)
        {
            var instr = worklist.Pop();
            if (reachable.Contains(instr)) continue;
            reachable.Add(instr);
            
            switch (instr.OpCode.FlowControl)
            {
                case FlowControl.Branch:
                case FlowControl.Cond_Branch:
                    if (instr.Operand is Instruction target) worklist.Push(target);
                    if (instr.Operand is IList<Instruction> targets)
                        foreach (var t in targets) worklist.Push(t);
                    if (instr.OpCode.FlowControl == FlowControl.Cond_Branch && instr.Next != null)
                        worklist.Push(instr.Next);
                    break;
                default:
                    if (instr.Next != null) worklist.Push(instr.Next);
                    break;
            }
        }
        
        foreach (var instr in instructions)
        {
            if (!reachable.Contains(instr) && instr.OpCode.Code != Code.Nop)
            {
                instr.OpCode = OpCodes.Nop;
                instr.Operand = null;
            }
        }
    }

    private void SimplifyBranches(MethodDef method)
    {
        var instructions = method.Body.Instructions;
        for (int i = instructions.Count - 1; i >= 0; i--)
        {
            if (instructions[i].OpCode.Code == Code.Nop && i > 0 && instructions[i - 1].OpCode.Code == Code.Nop)
            {
                instructions.RemoveAt(i);
            }
        }
    }

    private bool IsLdc(Instruction instr) =>
        instr.OpCode.Code is Code.Ldc_I4 or Code.Ldc_I4_S or Code.Ldc_I4_0 or Code.Ldc_I4_1 or
        Code.Ldc_I4_2 or Code.Ldc_I4_3 or Code.Ldc_I4_4 or Code.Ldc_I4_5 or Code.Ldc_I4_6 or
        Code.Ldc_I4_7 or Code.Ldc_I4_8 or Code.Ldc_I4_M1 or Code.Ldc_I8 or Code.Ldc_R4 or Code.Ldc_R8;

    private object? GetLdcValue(Instruction instr) => instr.OpCode.Code switch
    {
        Code.Ldc_I4_0 => 0, Code.Ldc_I4_1 => 1, Code.Ldc_I4_2 => 2, Code.Ldc_I4_3 => 3,
        Code.Ldc_I4_4 => 4, Code.Ldc_I4_5 => 5, Code.Ldc_I4_6 => 6, Code.Ldc_I4_7 => 7,
        Code.Ldc_I4_8 => 8, Code.Ldc_I4_M1 => -1,
        Code.Ldc_I4 or Code.Ldc_I4_S => instr.Operand,
        Code.Ldc_I8 => instr.Operand, Code.Ldc_R4 => instr.Operand, Code.Ldc_R8 => instr.Operand,
        _ => null
    };

    private bool IsStLoc(Instruction instr, out int localIndex)
    {
        localIndex = -1;
        var code = instr.OpCode.Code;
        if (code >= Code.Stloc_0 && code <= Code.Stloc_3) { localIndex = code - Code.Stloc_0; return true; }
        if ((code == Code.Stloc || code == Code.Stloc_S) && instr.Operand is Local local) { localIndex = local.Index; return true; }
        if ((code == Code.Stloc || code == Code.Stloc_S) && instr.Operand is int idx) { localIndex = idx; return true; }
        return false;
    }

    private bool IsLdLoc(Instruction instr, out int localIndex)
    {
        localIndex = -1;
        var code = instr.OpCode.Code;
        if (code >= Code.Ldloc_0 && code <= Code.Ldloc_3) { localIndex = code - Code.Ldloc_0; return true; }
        if ((code == Code.Ldloc || code == Code.Ldloc_S) && instr.Operand is Local local) { localIndex = local.Index; return true; }
        if ((code == Code.Ldloc || code == Code.Ldloc_S) && instr.Operand is int idx) { localIndex = idx; return true; }
        return false;
    }

    private bool IsComparisonOp(Code code) => code is Code.Ceq or Code.Cgt or Code.Cgt_Un or Code.Clt or Code.Clt_Un;

    private bool EvaluateComparison(object? left, object? right, Code op)
    {
        if (left == null || right == null) return false;
        return (left, right, op) switch
        {
            (int l, int r, Code.Ceq) => l == r, (int l, int r, Code.Cgt) => l > r, (int l, int r, Code.Clt) => l < r,
            (long l, long r, Code.Ceq) => l == r, (long l, long r, Code.Cgt) => l > r, (long l, long r, Code.Clt) => l < r,
            (float l, float r, Code.Ceq) => l == r, (float l, float r, Code.Cgt) => l > r, (float l, float r, Code.Clt) => l < r,
            (double l, double r, Code.Ceq) => l == r, (double l, double r, Code.Cgt) => l > r, (double l, double r, Code.Clt) => l < r,
            _ => false
        };
    }

    private void ReplaceComparisonWithResult(IList<Instruction> instructions, int startIndex, bool result)
    {
        instructions[startIndex].OpCode = result ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0;
        instructions[startIndex].Operand = null;
        if (startIndex + 1 < instructions.Count) { instructions[startIndex + 1].OpCode = OpCodes.Nop; instructions[startIndex + 1].Operand = null; }
        if (startIndex + 2 < instructions.Count) { instructions[startIndex + 2].OpCode = OpCodes.Nop; instructions[startIndex + 2].Operand = null; }
    }

    private void RebuildExceptionHandlers(MethodDef method)
    {
        foreach (var handler in method.Body.ExceptionHandlers)
        {
            if (handler.TryStart != null && handler.TryStart.OpCode.Code == Code.Nop)
            {
                var current = handler.TryStart;
                while (current != null && current.OpCode.Code == Code.Nop) current = current.Next;
                if (current != null) handler.TryStart = current;
            }
            if (handler.HandlerStart != null && handler.HandlerStart.OpCode.Code == Code.Nop)
            {
                var current = handler.HandlerStart;
                while (current != null && current.OpCode.Code == Code.Nop) current = current.Next;
                if (current != null) handler.HandlerStart = current;
            }
        }
    }
}
