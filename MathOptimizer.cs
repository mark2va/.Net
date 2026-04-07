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
            bool globalChanged = true;

            // Несколько проходов для обработки вложенных вызовов Convert.ToInt32()
            while (globalChanged)
            {
                globalChanged = false;

                foreach (var type in module.GetTypes())
                {
                    foreach (var method in type.Methods)
                    {
                        if (!method.HasBody || !method.Body.HasInstructions) continue;

                        var body = method.Body;
                        var instrs = body.Instructions;
                        bool changed = true;

                        while (changed)
                        {
                            changed = false;

                            for (int i = 0; i < instrs.Count - 1; i++)
                            {
                                var instr = instrs[i];

                                // Паттерн 1: ldc.r4/r8/i4, call Math.Ceiling/Floor/Round, conv.i4
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
                                                        globalChanged = true;
                                                        _log?.Invoke($"  Optimized Math.{methodName}({value}) -> {finalResult}");
                                                    }
                                                }
                                                catch { }
                                            }
                                        }
                                    }
                                }

                                // Паттерн 2: ldc.* , call Convert.ToInt32/ToDouble/ToString и т.д.
                                if (IsLdc(instr) && i + 1 < instrs.Count)
                                {
                                    var next = instrs[i + 1];

                                    if (next.OpCode.Code == Code.Call && next.Operand is IMethodDefOrRef convertMethod)
                                    {
                                        string methodName = convertMethod.Name;
                                        string typeName = convertMethod.DeclaringType?.FullName ?? "";

                                        // Обрабатываем методы Convert.ToInt32, Convert.ToDouble, Convert.ToString и другие
                                        if (typeName == "System.Convert" && 
                                            (methodName == "ToInt32" || methodName == "ToDouble" || methodName == "ToSingle" || 
                                             methodName == "ToInt64" || methodName == "ToString" || methodName == "ToByte" ||
                                             methodName == "ToInt16" || methodName == "ToUInt32" || methodName == "ToUInt16" ||
                                             methodName == "ToUInt64" || methodName == "ToSByte"))
                                        {
                                            object? value = GetConstantValue(instr);
                                            if (value != null)
                                            {
                                                try
                                                {
                                                    object? result = null;
                                                    string resultTypeName = "";

                                                    if (methodName == "ToInt32")
                                                    {
                                                        result = Convert.ToInt32(value);
                                                        resultTypeName = "Int32";
                                                    }
                                                    else if (methodName == "ToDouble")
                                                    {
                                                        result = Convert.ToDouble(value);
                                                        resultTypeName = "Double";
                                                    }
                                                    else if (methodName == "ToSingle")
                                                    {
                                                        result = Convert.ToSingle(value);
                                                        resultTypeName = "Single";
                                                    }
                                                    else if (methodName == "ToInt64")
                                                    {
                                                        result = Convert.ToInt64(value);
                                                        resultTypeName = "Int64";
                                                    }
                                                    else if (methodName == "ToString")
                                                    {
                                                        result = Convert.ToString(value);
                                                        resultTypeName = "String";
                                                    }
                                                    else if (methodName == "ToByte")
                                                    {
                                                        result = Convert.ToByte(value);
                                                        resultTypeName = "Byte";
                                                    }
                                                    else if (methodName == "ToInt16")
                                                    {
                                                        result = Convert.ToInt16(value);
                                                        resultTypeName = "Int16";
                                                    }
                                                    else if (methodName == "ToUInt32")
                                                    {
                                                        result = Convert.ToUInt32(value);
                                                        resultTypeName = "UInt32";
                                                    }
                                                    else if (methodName == "ToUInt16")
                                                    {
                                                        result = Convert.ToUInt16(value);
                                                        resultTypeName = "UInt16";
                                                    }
                                                    else if (methodName == "ToUInt64")
                                                    {
                                                        result = Convert.ToUInt64(value);
                                                        resultTypeName = "UInt64";
                                                    }
                                                    else if (methodName == "ToSByte")
                                                    {
                                                        result = Convert.ToSByte(value);
                                                        resultTypeName = "SByte";
                                                    }

                                                    if (result != null)
                                                    {
                                                        // Заменяем ldc + call на ldc с результатом
                                                        SetConstantValue(instr, result, resultTypeName);

                                                        next.OpCode = OpCodes.Nop;
                                                        next.Operand = null;

                                                        optimizedCount++;
                                                        changed = true;
                                                        globalChanged = true;
                                                        _log?.Invoke($"  Optimized Convert.{methodName}({value}) -> {result}");
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

        private void SetConstantValue(Instruction instr, object value, string typeName)
        {
            switch (typeName)
            {
                case "Int32":
                    instr.OpCode = OpCodes.Ldc_I4;
                    instr.Operand = Convert.ToInt32(value);
                    break;
                case "Int64":
                    instr.OpCode = OpCodes.Ldc_I8;
                    instr.Operand = Convert.ToInt64(value);
                    break;
                case "Double":
                    instr.OpCode = OpCodes.Ldc_R8;
                    instr.Operand = Convert.ToDouble(value);
                    break;
                case "Single":
                    instr.OpCode = OpCodes.Ldc_R4;
                    instr.Operand = Convert.ToSingle(value);
                    break;
                case "String":
                    instr.OpCode = OpCodes.Ldstr;
                    instr.Operand = Convert.ToString(value);
                    break;
                case "Byte":
                    instr.OpCode = OpCodes.Ldc_I4;
                    instr.Operand = Convert.ToByte(value);
                    break;
                case "Int16":
                    instr.OpCode = OpCodes.Ldc_I4;
                    instr.Operand = Convert.ToInt16(value);
                    break;
                case "UInt32":
                    instr.OpCode = OpCodes.Ldc_I4;
                    instr.Operand = (int)Convert.ToUInt32(value);
                    break;
                case "UInt16":
                    instr.OpCode = OpCodes.Ldc_I4;
                    instr.Operand = Convert.ToUInt16(value);
                    break;
                case "UInt64":
                    instr.OpCode = OpCodes.Ldc_I8;
                    instr.Operand = (long)Convert.ToUInt64(value);
                    break;
                case "SByte":
                    instr.OpCode = OpCodes.Ldc_I4;
                    instr.Operand = Convert.ToSByte(value);
                    break;
                default:
                    // По умолчанию пробуем определить тип и установить соответствующую инструкцию
                    if (value is int iVal)
                    {
                        instr.OpCode = OpCodes.Ldc_I4;
                        instr.Operand = iVal;
                    }
                    else if (value is long lVal)
                    {
                        instr.OpCode = OpCodes.Ldc_I8;
                        instr.Operand = lVal;
                    }
                    else if (value is double dVal)
                    {
                        instr.OpCode = OpCodes.Ldc_R8;
                        instr.Operand = dVal;
                    }
                    else if (value is float fVal)
                    {
                        instr.OpCode = OpCodes.Ldc_R4;
                        instr.Operand = fVal;
                    }
                    else if (value is string sVal)
                    {
                        instr.OpCode = OpCodes.Ldstr;
                        instr.Operand = sVal;
                    }
                    break;
            }
        }

        private bool IsConversionToInt(Instruction instr)
        {
            return instr.OpCode.Code == Code.Conv_I4 || instr.OpCode.Code == Code.Conv_I ||
                   instr.OpCode.Code == Code.Conv_Ovf_I4 ||
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
