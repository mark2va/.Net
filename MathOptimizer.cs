using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Deobfuscator
{
    /// <summary>
    /// Отвечает за оптимизацию математических выражений и сворачивание констант.
    /// </summary>
    public class MathOptimizer
    {
        private readonly Action<string>? _log;

        public MathOptimizer(Action<string>? log = null)
        {
            _log = log;
        }

        public int Optimize(ModuleDef module)
        {
            int optimizedCount = 0;

            foreach (var type in module.GetTypes())
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

                                                    instrs[i + 1].OpCode = OpCodes.Nop;
                                                    instrs[i + 1].Operand = null;
                                                    instrs[i + 2].OpCode = OpCodes.Nop;
                                                    instrs[i + 2].Operand = null;
                                                    instrs[i + 3].OpCode = OpCodes.Nop;
                                                    instrs[i + 3].Operand = null;

                                                    optimizedCount++;
                                                    changed = true;
                                                    _log?.Invoke($"  Optimized Math.{methodName}({value}) -> {finalResult}");
                                                }
                                            }
                                            catch { }
                                        }
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

            return optimizedCount;
        }

        #region Helper Methods

        private bool IsLdc(Instruction instr)
        {
            return instr.OpCode.Code == Code.Ldc_I4 || instr.OpCode.Code == Code.Ldc_I4_S ||
                   instr.OpCode.Code == Code.Ldc_I8 || instr.OpCode.Code == Code.Ldc_R4 ||
                   instr.OpCode.Code == Code.Ldc_R8;
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

        private bool IsConversionToInt(Instruction instr)
        {
            return instr.OpCode.Code == Code.Conv_I4 || instr.OpCode.Code == Code.Conv_I4_1 ||
                   instr.OpCode.Code == Code.Conv_I || instr.OpCode.Code == Code.Conv_Ovf_I4 ||
                   instr.OpCode.Code == Code.Conv_Ovf_I4_Un;
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

        #endregion
    }
}
