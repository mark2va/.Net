using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Deobfuscator
{
    /// <summary>
    /// Отвечает за оптимизацию математических выражений и сворачивание констант.
    /// Использует симуляцию стека для вычисления сложных выражений.
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

            // Несколько проходов для обработки вложенных вызовов
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

                            for (int i = 0; i < instrs.Count; i++)
                            {
                                var instr = instrs[i];

                                // Ищем вызовы методов
                                if (instr.OpCode.Code == Code.Call && instr.Operand is IMethodDefOrRef calledMethod)
                                {
                                    string typeName = calledMethod.DeclaringType?.FullName ?? "";
                                    string methodName = calledMethod.Name;

                                    // Проверяем, является ли это методом Math или Convert, который мы можем свернуть
                                    bool isMathMethod = typeName == "System.Math" && 
                                                        (methodName == "Ceiling" || methodName == "Floor" || methodName == "Round" || 
                                                         methodName == "Sin" || methodName == "Cos" || methodName == "Tan" || 
                                                         methodName == "Log" || methodName == "Log10" || methodName == "Abs" || 
                                                         methodName == "Sqrt" || methodName == "Exp" || methodName == "Tanh");
                                    
                                    bool isConvertMethod = typeName == "System.Convert" && 
                                                           (methodName == "ToInt32" || methodName == "ToDouble" || methodName == "ToSingle" || 
                                                            methodName == "ToInt64" || methodName == "ToString" || methodName == "ToByte" ||
                                                            methodName == "ToInt16" || methodName == "ToUInt32" || methodName == "ToUInt16" ||
                                                            methodName == "ToUInt64" || methodName == "ToSByte" || methodName == "ToBoolean");

                                    if (isMathMethod || isConvertMethod)
                                    {
                                        // Пытаемся вычислить аргументы, лежащие на стеке перед вызовом
                                        if (TryEvaluateStackArguments(instrs, i, calledMethod, out object? result, out int argsCount, out string resultTypeName))
                                        {
                                            if (result != null)
                                            {
                                                // Удаляем инструкции аргументов и сам вызов
                                                int removeCount = argsCount + 1;
                                                int startRemoveIndex = i - argsCount;
                                                
                                                // Заменяем первую инструкцию аргумента на константу результата
                                                var firstInstr = instrs[startRemoveIndex];
                                                SetConstantValue(firstInstr, result, resultTypeName);

                                                // Превращаем остальные инструкции аргументов и вызов в Nop
                                                for (int k = 1; k < removeCount; k++)
                                                {
                                                    if (startRemoveIndex + k < instrs.Count)
                                                    {
                                                        instrs[startRemoveIndex + k].OpCode = OpCodes.Nop;
                                                        instrs[startRemoveIndex + k].Operand = null;
                                                    }
                                                }

                                                optimizedCount++;
                                                changed = true;
                                                globalChanged = true;
                                                _log?.Invoke($"  Optimized {typeName}.{methodName}(...) -> {result} (removed {removeCount} instructions)");
                                            }
                                        }
                                    }
                                }
                                
                                // Дополнительно: обработка XOR и арифметики, если они остались как отдельные блоки
                                // (Хотя основная логика теперь через стек выше, это страховка для простых случаев)
                                if (i >= 2 && (instr.OpCode == OpCodes.Xor || instr.OpCode == OpCodes.Add || instr.OpCode == OpCodes.Sub || instr.OpCode == OpCodes.Mul || instr.OpCode == OpCodes.Div))
                                {
                                    if (IsLdcI4(instrs[i-1]) && IsLdcI4(instrs[i-2]))
                                    {
                                        long v1 = GetLdcValue(instrs[i-2]);
                                        long v2 = GetLdcValue(instrs[i-1]);
                                        long res = 0;
                                        
                                        if (instr.OpCode == OpCodes.Xor) res = v1 ^ v2;
                                        else if (instr.OpCode == OpCodes.Add) res = v1 + v2;
                                        else if (instr.OpCode == OpCodes.Sub) res = v1 - v2;
                                        else if (instr.OpCode == OpCodes.Mul) res = v1 * v2;
                                        else if (instr.OpCode == OpCodes.Div && v2 != 0) res = v1 / v2;

                                        SetConstantValue(instrs[i-2], res, "Int64");
                                        instrs[i-1].OpCode = OpCodes.Nop; instrs[i-1].Operand = null;
                                        instr.OpCode = OpCodes.Nop; instr.Operand = null;
                                        
                                        optimizedCount++;
                                        changed = true;
                                        globalChanged = true;
                                    }
                                    else if (instrs[i-1].OpCode.Code == Code.Ldc_R8 && instrs[i-2].OpCode.Code == Code.Ldc_R8)
                                    {
                                        double v1 = (double)instrs[i-2].Operand;
                                        double v2 = (double)instrs[i-1].Operand;
                                        double res = 0;

                                        if (instr.OpCode == OpCodes.Add) res = v1 + v2;
                                        else if (instr.OpCode == OpCodes.Sub) res = v1 - v2;
                                        else if (instr.OpCode == OpCodes.Mul) res = v1 * v2;
                                        else if (instr.OpCode == OpCodes.Div && v2 != 0.0) res = v1 / v2;

                                        SetConstantValue(instrs[i-2], res, "Double");
                                        instrs[i-1].OpCode = OpCodes.Nop; instrs[i-1].Operand = null;
                                        instr.OpCode = OpCodes.Nop; instr.Operand = null;

                                        optimizedCount++;
                                        changed = true;
                                        globalChanged = true;
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

        /// <summary>
        /// Симулирует выполнение инструкций перед вызовом метода, чтобы вычислить значение аргументов.
        /// </summary>
        private bool TryEvaluateStackArguments(IList<Instruction> instrs, int callIndex, IMethodDefOrRef method, out object? result, out int argsCount, out string resultTypeName)
        {
            result = null;
            argsCount = 0;
            resultTypeName = "Object";

            // Определяем количество аргументов
            int paramCount = method.Parameters.Count;
            if (paramCount == 0) return false; // Нет аргументов? Странно, но бывает для расширений (здесь не поддерживаем)

            // Нам нужно найти paramCount значений на стеке перед вызовом.
            // Идем назад от callIndex
            Stack<object?> stack = new Stack<object?>();
            int currentIdx = callIndex - 1;
            int foundArgs = 0;

            // Простая эмуляция: идем назад и собираем константы и результаты операций
            // Это упрощенная модель, работающая для линейных последовательностей ldc/call/arithmetic
            
            // Для корректной работы нам нужно выполнить "свертку" инструкций назад до тех пор, пока не наберем аргументы
            // Но так как мы уже в цикле оптимизации, мы можем предположить, что внутренние вызовы уже свернуты?
            // Нет, в данном случае (46.5 - 15.5) это две константы и sub.
            
            // Попробуем собрать инструкции, формирующие последний аргумент (верхний элемент стека)
            // В IL аргументы пушатся слева направо, последний аргумент - на вершине.
            
            // Рекурсивный спуск для вычисления значения по инструкциям
            // Мы будем пытаться "съесть" инструкции с конца, вычисляя значение.
            
            int tempIdx = currentIdx;
            List<Instruction> argInstructions = new List<Instruction>();
            
            // Попытка выделить блок инструкций для одного аргумента (самого верхнего)
            // Это сложно сделать идеально без полного эмулятора стека, поэтому сделаем эвристику:
            // Идем назад, считаем баланс стека. Как только баланс становится 0 (относительно начала аргумента), стоп.
            
            int stackBalance = 0;
            bool hasValidSequence = false;
            
            // Нам нужно найти последовательность, которая кладет ровно 1 значение на стек
            // Начинаем с конца. Push увеличивает баланс, Pop/Call уменьшает.
            
            // Упрощение: попробуем просто выполнить инструкции в обратном порядке логически? Нет.
            // Давайте просто возьмем блок инструкций до callIndex и попробуем их выполнить виртуально.
            
            // Создадим копию инструкций от начала метода до callIndex и выполним их в песочнице? Слишком тяжело.
            // Сделаем локальную эмуляцию только для нужных нам инструкций.
            
            // Стратегия: Идем назад от callIndex. 
            // Если видим Ldc - пушим.
            // Если видим бинарную операцию (Add, Sub, Mul, Div, Xor) - нужны 2 операнда сверху.
            // Если видим унарную (Neg, Not) - нужен 1.
            // Если видим Call (Math) - нужен 1 (или больше, если метод многоаргументный).
            
            // Так как нам нужно вычислить только аргументы для текущего вызова, 
            // мы можем попытаться вычислить состояние стека прямо перед вызовом.
            
            // Попробуем другой подход: соберем все инструкции до callIndex, которые участвуют в вычислении аргументов.
            // Но проще всего: запустить мини-эмулятор, идя с начала метода до callIndex, но это медленно.
            
            // Вернемся к простому варианту: идем назад и пытаемся свернуть дерево выражений.
            // Для случая Convert.ToInt32(46.5 - 15.5):
            // [i-3] ldc.r8 46.5
            // [i-2] ldc.r8 15.5
            // [i-1] sub
            // [i]   call ToInt32
            
            // Алгоритм backwards evaluation:
            int readCount = 0;
            Stack<object?> evalStack = new Stack<object?>();
            int scanIndex = callIndex - 1;
            
            while (scanIndex >= 0 && evalStack.Count < paramCount + 10) // +10 защита от дурака
            {
                var curr = instrs[scanIndex];
                
                // Пропускаем Nop
                if (curr.OpCode.Code == Code.Nop) { scanIndex--; continue; }

                bool consumed = false;

                // Бинарные операции
                if (curr.OpCode.Code == Code.Add || curr.OpCode.Code == Code.Sub || curr.OpCode.Code == Code.Mul || 
                    curr.OpCode.Code == Code.Div || curr.OpCode.Code == Code.Xor || curr.OpCode.Code == Code.And || 
                    curr.OpCode.Code == Code.Or || curr.OpCode.Code == Code.Rem)
                {
                    if (evalStack.Count >= 2)
                    {
                        var b = evalStack.Pop();
                        var a = evalStack.Pop();
                        var res = PerformBinaryOp(curr.OpCode.Code, a, b);
                        if (res.HasValue)
                        {
                            evalStack.Push(res.Value);
                            consumed = true;
                        }
                    }
                }
                // Унарные операции
                else if (curr.OpCode.Code == Code.Neg || curr.OpCode.Code == Code.Not)
                {
                    if (evalStack.Count >= 1)
                    {
                        var a = evalStack.Pop();
                        var res = PerformUnaryOp(curr.OpCode.Code, a);
                        if (res.HasValue)
                        {
                            evalStack.Push(res.Value);
                            consumed = true;
                        }
                    }
                }
                // Вызовы Math (унарные для простоты, можно расширить)
                else if (curr.OpCode.Code == Code.Call && curr.Operand is IMethodDefOrRef m)
                {
                    string mName = m.Name;
                    string tName = m.DeclaringType?.FullName ?? "";
                    
                    if (tName == "System.Math")
                    {
                        int pCount = m.Parameters.Count;
                        if (evalStack.Count >= pCount)
                        {
                            var args = new object?[pCount];
                            for(int k=pCount-1; k>=0; k--) args[k] = evalStack.Pop();
                            
                            var res = PerformMathCall(mName, args);
                            if (res.HasValue)
                            {
                                evalStack.Push(res.Value);
                                consumed = true;
                            }
                        }
                    }
                    else if (tName == "System.Convert")
                    {
                         // Вложенные конверты тоже пробуем
                        int pCount = m.Parameters.Count;
                        if (evalStack.Count >= pCount)
                        {
                            var args = new object?[pCount];
                            for(int k=pCount-1; k>=0; k--) args[k] = evalStack.Pop();
                            
                            var res = PerformConvertCall(mName, args);
                            if (res.HasValue)
                            {
                                evalStack.Push(res.Value);
                                consumed = true;
                            }
                        }
                    }
                }
                // Константы
                else if (IsLdc(curr))
                {
                    var val = GetConstantValue(curr);
                    if (val != null)
                    {
                        evalStack.Push(val);
                        consumed = true;
                    }
                }
                
                // Если инструкция не была потреблена (например, ldloc, который мы не умеем обрабатывать, или конец блока)
                // то прерываемся. Но нам нужно найти границу аргументов.
                // В нашем случае (линейное выражение) мы должны добраться до момента, когда на стеке лежит ровно paramCount элементов
                // и следующая инструкция (слева) уже не относится к этому выражению.
                // Эвристика: если мы прочитали достаточно инструкций, чтобы заполнить аргументы, и стек стабилизировался?
                
                // Для простоты: если мы смогли полностью свернуть выражение до констант, то evalStack будет содержать 1 элемент (для последнего аргумента)
                // Но нам нужно paramCount элементов.
                
                // Остановимся, если мы набрали paramCount элементов И следующий элемент (слева) не является частью этого выражения?
                // Сложно определить границу без анализа потока.
                // Допустим, мы просто идем назад, пока не наберем paramCount элементов и не увидим, что дальше идут инструкции, которые не трогают стек так, как нам нужно?
                
                // Простой критерий остановки: если мы набрали paramCount элементов, и текущий scanIndex указывает на инструкцию, 
                // которая НЕ является частью вычисления (например, stloc, br, или просто другая ветка).
                // Но в примере array2[Convert.ToInt32(46.5 - 15.5)] перед этим идет вычисление адреса массива или push this.
                // Нас интересует только верхний элемент стека (последний аргумент).
                
                // Если мы успешно свернули всё до констант, то в стеке будут конкретные значения.
                // Проверим: если consumed == false, значит мы уперлись в нечто, что не можем вычислить (ldloc, call неизвестного).
                // Тогда прерываем.
                
                if (!consumed)
                {
                    // Если это ldloc или что-то еще, что мы не обрабатываем - стоп.
                    // Но возможно, это просто предыдущий аргумент?
                    // Если стек содержит >= paramCount, проверим, можем ли мы остановиться.
                    break;
                }
                
                scanIndex--;
                
                // Проверка: если у нас есть paramCount элементов, и мы только что завершили операцию, 
                // можно попробовать выйти, но надежнее идти до упора, пока не упремся в границу基本блока или неконстанту.
                // Однако, чтобы не уйти слишком далеко, ограничим количество шагов?
                if (callIndex - scanIndex > 50) break; // Защита от бесконечного сканирования
            }
            
            // Теперь проверяем, получилось ли вычислить аргументы
            // В стеке должны быть значения в обратном порядке (последний аргумент внизу? нет, стек растет вверх).
            // При обратном чтении: мы пушим значения. В итоге в стеке должны лежать аргументы.
            // Но порядок в стеке при обратном чтении будет инвертирован относительно порядка чтения?
            // Нет. Пример: A B Add. Читаем назад: Add (ждет 2), B (пушит), A (пушит). Стек: [A, B]. Add берет B, A, пушит Res. Стек: [Res].
            // Значит, если всё верно, в стеке должно быть ровно paramCount элементов (если мы вычислили все аргументы)
            // Или больше, если мы захватили лишние предыдущие аргументы метода.
            
            if (evalStack.Count >= paramCount)
            {
                // Берем верхние paramCount элементов. Они будут в правильном порядке?
                // Стек: Bottom [..., Arg1, Arg2, ..., ArgN] Top.
                // Pop() вернет ArgN, затем ArgN-1...
                var finalArgs = new object?[paramCount];
                for (int k = paramCount - 1; k >= 0; k--)
                {
                    if (evalStack.Count == 0) return false;
                    finalArgs[k] = evalStack.Pop();
                }
                
                // Вычисляем результат вызова
                if (isConvertMethod(method.Name))
                {
                    var res = PerformConvertCall(method.Name, finalArgs);
                    if (res.HasValue)
                    {
                        result = res.Value;
                        argsCount = callIndex - (scanIndex + 1); // Сколько инструкций съели
                        // Уточним argsCount: это разница между callIndex и последней прочитанной инструкцией
                        // Но мы читали пока consumed==true. 
                        // Реальное количество удаленных инструкций = callIndex - (scanIndex + 1)
                        // Но scanIndex сейчас указывает на инструкцию, которую мы НЕ съели (или -1).
                        // Значит съели от scanIndex+1 до callIndex-1.
                        argsCount = callIndex - 1 - scanIndex;
                        
                        resultTypeName = GetTypeNameForResult(result);
                        return true;
                    }
                }
                else if (isMathMethod(method.Name))
                {
                     var res = PerformMathCall(method.Name, finalArgs);
                     if (res.HasValue)
                     {
                         result = res.Value;
                         argsCount = callIndex - 1 - scanIndex;
                         resultTypeName = GetTypeNameForResult(result);
                         return true;
                     }
                }
            }

            return false;
        }
        
        private bool isConvertMethod(string name) => 
            name == "ToInt32" || name == "ToDouble" || name == "ToSingle" || name == "ToInt64" || 
            name == "ToString" || name == "ToByte" || name == "ToInt16" || name == "ToUInt32" || 
            name == "ToUInt16" || name == "ToUInt64" || name == "ToSByte" || name == "ToBoolean";

        private bool isMathMethod(string name) => 
            name == "Ceiling" || name == "Floor" || name == "Round" || name == "Sin" || name == "Cos" || 
            name == "Tan" || name == "Log" || name == "Log10" || name == "Abs" || name == "Sqrt" || 
            name == "Exp" || name == "Tanh";

        private object? PerformBinaryOp(Code op, object? a, object? b)
        {
            if (a == null || b == null) return null;
            try
            {
                if (a is double da && b is double db)
                {
                    if (op == Code.Add) return da + db;
                    if (op == Code.Sub) return da - db;
                    if (op == Code.Mul) return da * db;
                    if (op == Code.Div) return db != 0 ? da / db : null;
                }
                if ((a is int ia || a is long la) && (b is int ib || b is long lb))
                {
                    long va = Convert.ToInt64(a);
                    long vb = Convert.ToInt64(b);
                    if (op == Code.Add) return va + vb;
                    if (op == Code.Sub) return va - vb;
                    if (op == Code.Mul) return va * vb;
                    if (op == Code.Div) return vb != 0 ? va / vb : null;
                    if (op == Code.Xor) return va ^ vb;
                    if (op == Code.And) return va & vb;
                    if (op == Code.Or) return va | vb;
                    if (op == Code.Rem) return vb != 0 ? va % vb : null;
                }
                // Смешанные типы приводим к double для арифметики
                double v1 = Convert.ToDouble(a);
                double v2 = Convert.ToDouble(b);
                if (op == Code.Add) return v1 + v2;
                if (op == Code.Sub) return v1 - v2;
                if (op == Code.Mul) return v1 * v2;
                if (op == Code.Div) return v2 != 0 ? v1 / v2 : null;
            }
            catch { }
            return null;
        }

        private object? PerformUnaryOp(Code op, object? a)
        {
            if (a == null) return null;
            try
            {
                if (op == Code.Neg)
                {
                    if (a is double d) return -d;
                    if (a is int i) return -i;
                    if (a is long l) return -l;
                    return -Convert.ToDouble(a);
                }
                if (op == Code.Not)
                {
                    if (a is int i) return ~i;
                    if (a is long l) return ~l;
                    return ~(long)Convert.ToDouble(a);
                }
            }
            catch { }
            return null;
        }

        private object? PerformMathCall(string methodName, object?[] args)
        {
            if (args.Length == 0 || args[0] == null) return null;
            try
            {
                double val = Convert.ToDouble(args[0]);
                double res = 0;
                switch (methodName)
                {
                    case "Ceiling": res = Math.Ceiling(val); break;
                    case "Floor": res = Math.Floor(val); break;
                    case "Round": res = Math.Round(val); break;
                    case "Sin": res = Math.Sin(val); break;
                    case "Cos": res = Math.Cos(val); break;
                    case "Tan": res = Math.Tan(val); break;
                    case "Log": res = Math.Log(val); break;
                    case "Log10": res = Math.Log10(val); break;
                    case "Abs": res = Math.Abs(val); break;
                    case "Sqrt": res = Math.Sqrt(val); break;
                    case "Exp": res = Math.Exp(val); break;
                    case "Tanh": res = Math.Tanh(val); break;
                    default: return null;
                }
                return res;
            }
            catch { }
            return null;
        }

        private object? PerformConvertCall(string methodName, object?[] args)
        {
            if (args.Length == 0 || args[0] == null) return null;
            try
            {
                object val = args[0];
                switch (methodName)
                {
                    case "ToInt32": return Convert.ToInt32(val);
                    case "ToDouble": return Convert.ToDouble(val);
                    case "ToSingle": return Convert.ToSingle(val);
                    case "ToInt64": return Convert.ToInt64(val);
                    case "ToString": return Convert.ToString(val);
                    case "ToByte": return Convert.ToByte(val);
                    case "ToInt16": return Convert.ToInt16(val);
                    case "ToUInt32": return Convert.ToUInt32(val);
                    case "ToUInt16": return Convert.ToUInt16(val);
                    case "ToUInt64": return Convert.ToUInt64(val);
                    case "ToSByte": return Convert.ToSByte(val);
                    case "ToBoolean": return Convert.ToBoolean(val);
                    default: return null;
                }
            }
            catch { }
            return null;
        }

        private string GetTypeNameForResult(object? val)
        {
            if (val == null) return "Object";
            return val.GetType().Name;
        }

        #region Helper Methods (Old helpers kept for simple cases)

        private bool IsLdc(Instruction instr)
        {
            return instr.OpCode.Code == Code.Ldc_I4 || instr.OpCode.Code == Code.Ldc_I4_S ||
                   instr.OpCode.Code == Code.Ldc_I8 || instr.OpCode.Code == Code.Ldc_R4 ||
                   instr.OpCode.Code == Code.Ldc_R8;
        }
        
        private bool IsLdcI4(Instruction instr)
        {
            return instr.OpCode.Code == Code.Ldc_I4 || instr.OpCode.Code == Code.Ldc_I4_S;
        }

        private long GetLdcValue(Instruction instr)
        {
            if (instr.OpCode.Code == Code.Ldc_I4 || instr.OpCode.Code == Code.Ldc_I4_S)
                return (int)(instr.Operand ?? 0);
            if (instr.OpCode.Code == Code.Ldc_I8)
                return (long)(instr.Operand ?? 0L);
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
                case "Boolean":
                    instr.OpCode = OpCodes.Ldc_I4;
                    instr.Operand = Convert.ToBoolean(value) ? 1 : 0;
                    break;
                default:
                    if (value is int iVal) { instr.OpCode = OpCodes.Ldc_I4; instr.Operand = iVal; }
                    else if (value is long lVal) { instr.OpCode = OpCodes.Ldc_I8; instr.Operand = lVal; }
                    else if (value is double dVal) { instr.OpCode = OpCodes.Ldc_R8; instr.Operand = dVal; }
                    else if (value is float fVal) { instr.OpCode = OpCodes.Ldc_R4; instr.Operand = fVal; }
                    else if (value is string sVal) { instr.OpCode = OpCodes.Ldstr; instr.Operand = sVal; }
                    break;
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

        #endregion
    }
}
