using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Deobfuscator
{
    /// <summary>
    /// Отвечает за оптимизацию математических выражений и сворачивание констант.
    /// Безопасная версия, которая не ломает код.
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
            int totalOptimized = 0;
            bool globalChanged = true;

            // Внешний цикл для обработки цепочек (вложенных вызовов)
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
                        bool methodChanged = true;

                        // Внутренний цикл для обработки всех совпадений в методе
                        while (methodChanged)
                        {
                            methodChanged = false;

                            for (int i = 0; i < instrs.Count - 1; i++)
                            {
                                var current = instrs[i];
                                var next = instrs[i + 1];

                                // Пропускаем NOP
                                if (current.OpCode.Code == Code.Nop) continue;

                                // Проверяем, является ли текущая инструкция константой
                                if (!IsLdc(current)) continue;

                                // Проверяем, является ли следующая инструкция вызовом
                                if (next.OpCode.Code != Code.Call || next.Operand is not IMethodDefOrRef calledMethod) continue;

                                string typeName = calledMethod.DeclaringType?.FullName ?? "";
                                string methodName = calledMethod.Name;

                                object? resultValue = null;
                                string resultType = "";

                                try
                                {
                                    object? argValue = GetConstantValue(current);
                                    if (argValue == null) continue;

                                    // Обработка System.Math
                                    if (typeName == "System.Math")
                                    {
                                        double dArg = Convert.ToDouble(argValue);
                                        double dRes = 0;

                                        switch (methodName)
                                        {
                                            case "Ceiling": dRes = Math.Ceiling(dArg); break;
                                            case "Floor": dRes = Math.Floor(dArg); break;
                                            case "Round": dRes = Math.Round(dArg); break;
                                            case "Abs": dRes = Math.Abs(dArg); break;
                                            case "Sin": dRes = Math.Sin(dArg); break;
                                            case "Cos": dRes = Math.Cos(dArg); break;
                                            case "Tan": dRes = Math.Tan(dArg); break;
                                            case "Log": dRes = Math.Log(dArg); break;
                                            case "Log10": dRes = Math.Log10(dArg); break;
                                            case "Exp": dRes = Math.Exp(dArg); break;
                                            case "Sqrt": dRes = Math.Sqrt(dArg); break;
                                            default: continue; // Неизвестный метод Math
                                        }
                                        resultValue = dRes;
                                        resultType = "Double";
                                    }
                                    // Обработка System.Convert
                                    else if (typeName == "System.Convert")
                                    {
                                        switch (methodName)
                                        {
                                            case "ToInt32": resultValue = Convert.ToInt32(argValue); resultType = "Int32"; break;
                                            case "ToInt64": resultValue = Convert.ToInt64(argValue); resultType = "Int64"; break;
                                            case "ToDouble": resultValue = Convert.ToDouble(argValue); resultType = "Double"; break;
                                            case "ToSingle": resultValue = Convert.ToSingle(argValue); resultType = "Single"; break;
                                            case "ToString": resultValue = Convert.ToString(argValue); resultType = "String"; break;
                                            case "ToByte": resultValue = Convert.ToByte(argValue); resultType = "Byte"; break;
                                            case "ToInt16": resultValue = Convert.ToInt16(argValue); resultType = "Int16"; break;
                                            case "ToUInt32": resultValue = Convert.ToUInt32(argValue); resultType = "UInt32"; break;
                                            case "ToUInt16": resultValue = Convert.ToUInt16(argValue); resultType = "UInt16"; break;
                                            case "ToUInt64": resultValue = Convert.ToUInt64(argValue); resultType = "UInt64"; break;
                                            case "ToSByte": resultValue = Convert.ToSByte(argValue); resultType = "SByte"; break;
                                            case "ToBoolean": resultValue = Convert.ToBoolean(argValue); resultType = "Boolean"; break;
                                            default: continue; // Неизвестный метод Convert
                                        }
                                    }
                                    else
                                    {
                                        continue; // Не тот класс
                                    }

                                    // Если вычисление успешно, выполняем замену
                                    if (resultValue != null)
                                    {
                                        // 1. Обновляем текущую инструкцию (константу) новым значением
                                        SetConstantValue(current, resultValue, resultType);

                                        // 2. Превращаем вызов в NOP
                                        next.OpCode = OpCodes.Nop;
                                        next.Operand = null;

                                        totalOptimized++;
                                        methodChanged = true;
                                        globalChanged = true;
                                        
                                        _log?.Invoke($"[MathOptimizer] Свернуто: {methodName}({argValue}) -> {resultValue}");
                                        
                                        // Прерываем цикл, чтобы пересчитать индексы после изменения (безопаснее)
                                        break; 
                                    }
                                }
                                catch
                                {
                                    // Ошибка вычисления (например, деление на ноль или переполнение), пропускаем
                                    continue;
                                }
                            }
                        }
                        
                        // После обработки метода очищаем NOP, если были изменения
                        if (methodChanged)
                        {
                            CleanupNops(method);
                            body.UpdateInstructionOffsets();
                        }
                    }
                }
            }

            return totalOptimized;
        }

        #region Helper Methods

        private bool IsLdc(Instruction instr)
        {
            var code = instr.OpCode.Code;
            return code == Code.Ldc_I4 || code == Code.Ldc_I4_S ||
                   code == Code.Ldc_I8 || code == Code.Ldc_R4 ||
                   code == Code.Ldc_R8 || code == Code.Ldc_Null;
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
                case Code.Ldc_Null:
                    return null;
                default:
                    return null;
            }
        }

        private void SetConstantValue(Instruction instr, object value, string typeName)
        {
            // Нормализуем типы для корректной записи
            if (value is int iVal)
            {
                instr.OpCode = OpCodes.Ldc_I4;
                instr.Operand = iVal;
                return;
            }
            if (value is long lVal)
            {
                instr.OpCode = OpCodes.Ldc_I8;
                instr.Operand = lVal;
                return;
            }
            if (value is double dVal)
            {
                instr.OpCode = OpCodes.Ldc_R8;
                instr.Operand = dVal;
                return;
            }
            if (value is float fVal)
            {
                instr.OpCode = OpCodes.Ldc_R4;
                instr.Operand = fVal;
                return;
            }
            if (value is string sVal)
            {
                instr.OpCode = OpCodes.Ldstr;
                instr.Operand = sVal;
                return;
            }
            if (value is bool bVal)
            {
                instr.OpCode = OpCodes.Ldc_I4;
                instr.Operand = bVal ? 1 : 0;
                return;
            }

            // Фоллбэк для других типов через конвертацию
            switch (typeName)
            {
                case "Int32":
                case "Int16":
                case "Byte":
                case "SByte":
                case "UInt16":
                case "UInt32":
                    instr.OpCode = OpCodes.Ldc_I4;
                    instr.Operand = Convert.ToInt32(value);
                    break;
                case "Int64":
                case "UInt64":
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
                case "Boolean":
                    instr.OpCode = OpCodes.Ldc_I4;
                    instr.Operand = Convert.ToBoolean(value) ? 1 : 0;
                    break;
            }
        }

        private void CleanupNops(MethodDef method)
        {
            var body = method.Body;
            if (body == null) return;

            // Удаляем NOP, которые никуда не ведут (не являются целями ветвлений)
            // Делаем это в обратном порядке, чтобы не сбить индексы
            for (int i = body.Instructions.Count - 1; i >= 0; i--)
            {
                var instr = body.Instructions[i];
                if (instr.OpCode.Code == Code.Nop)
                {
                    bool isTarget = false;
                    // Проверяем, ссылается ли какая-либо инструкция на этот NOP
                    foreach (var checkInstr in body.Instructions)
                    {
                        if (checkInstr.Operand == instr)
                        {
                            isTarget = true;
                            break;
                        }
                        if (checkInstr.Operand is Instruction[] targets && targets.Contains(instr))
                        {
                            isTarget = true;
                            break;
                        }
                    }

                    if (!isTarget)
                    {
                        body.Instructions.RemoveAt(i);
                    }
                }
            }
            
            // Обновляем оффсеты и стек
            body.UpdateInstructionOffsets();
        }

        #endregion
    }
}
