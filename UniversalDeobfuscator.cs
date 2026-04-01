using System;
using System.Collections.Generic;
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

        public UniversalDeobfuscator(string filePath, AiConfig aiConfig)
        {
            _module = ModuleDefMD.Load(filePath);
            _aiAssistant = aiConfig.Enabled ? new AiAssistant(aiConfig) : null;
        }

        public void Deobfuscate()
        {
            Console.WriteLine("[*] Starting deep deobfuscation...");
            int count = 0;
            
            foreach (var type in _module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody || !method.Body.IsIL) continue;

                    try
                    {
                        // Запускаем глубокую очистку потока управления
                        UnravelControlFlow(method);
                        
                        // Чистим оставшийся мусор (NOP)
                        CleanupMethod(method);

                        if (_aiAssistant != null && IsObfuscatedName(method.Name))
                        {
                            RenameWithAi(method);
                        }
                        count++;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[!] Error in {method.FullName}: {ex.Message}");
                    }
                }
            }
            Console.WriteLine($"[*] Processed {count} methods.");
        }

        /// <summary>
        /// Основной алгоритм распутывания: эмуляция значений переменных и упрощение ветвлений.
        /// </summary>
        private void UnravelControlFlow(MethodDef method)
        {
            var body = method.Body;
            var instructions = body.Instructions;
            bool changed = true;
            int maxPasses = 20; // Защита от зависания
            int pass = 0;

            while (changed && pass < maxPasses)
            {
                changed = false;
                pass++;

                // 1. Словарь для хранения известных значений локальных переменных в текущем проходе
                // Ключ: индекс локальной переменной, Значение: константа
                var knownValues = new Dictionary<int, object>();

                // Проходим по инструкциям, пытаясь вычислить значения и упростить условия
                for (int i = 0; i < instructions.Count; i++)
                {
                    var instr = instructions[i];

                    // --- АНАЛИЗ ПРИСВАИВАНИЯ КОНСТАНТ ---
                    // Ищем паттерн: ldc... -> stloc
                    if (i + 1 < instructions.Count)
                    {
                        var current = instr;
                        var next = instructions[i + 1];

                        if (IsStloc(next, out int localIdx))
                        {
                            var val = GetConstantValue(current);
                            if (val != null)
                            {
                                knownValues[localIdx] = val;
                                
                                // Оптимизация: если сразу после присваивания идет проверка этой переменной,
                                // мы можем попытаться упростить её позже.
                            }
                        }
                    }

                    // --- УПРОЩЕНИЕ УСЛОВНЫХ ПЕРЕХОДОВ (if (num == X) goto) ---
                    // Паттерн: ldloc, ldc, ceq/cgt/clt, brtrue/brfalse
                    // Или более сложный: ldloc, ldc, bne.un / beq
                    if (instr.OpCode.FlowControl == FlowControl.Cond_Branch)
                    {
                        // Пытаемся найти сравнение перед ветвлением
                        // В dnlib часто: ldloc, ldc, br.eq (совмещенное) ИЛИ ldloc, ldc, ceq, brtrue
                        
                        // Проверка на прямое сравнение с константой (ldloc, ldc, branch)
                        if (i >= 2)
                        {
                            var prev1 = instructions[i - 1]; // ldc
                            var prev2 = instructions[i - 2]; // ldloc

                            if (IsLdloc(prev2, out int checkLocalIdx) && GetConstantValue(prev1) != null)
                            {
                                var constVal = GetConstantValue(prev1);
                                
                                // Если у нас есть известное значение этой переменной из предыдущих присваиваний
                                if (knownValues.ContainsKey(checkLocalIdx))
                                {
                                    var actualVal = knownValues[checkLocalIdx];
                                    
                                    // Сравниваем фактическое значение с тем, что проверяется
                                    bool result = CompareValues(actualVal, constVal, instr.OpCode.Code);
                                    
                                    if (result.HasValue)
                                    {
                                        if (result.Value)
                                        {
                                            // Условие истинно: заменяем на безусловный переход (Br)
                                            instr.OpCode = OpCodes.Br;
                                            changed = true;
                                        }
                                        else
                                        {
                                            // Условие ложно: превращаем в Nop (переход не выполняется)
                                            instr.OpCode = OpCodes.Nop;
                                            instr.Operand = null;
                                            changed = true;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // --- УДАЛЕНИЕ БЕСКОНЕЧНЫХ ЦИКЛОВ С BREAK (for(;;) { if(x) break; }) ---
                    // Если видим безусловный выход из цикла (Br) на инструкцию сразу после цикла
                    // Это сложно детектировать без графа, но можно упростить:
                    // Если цикл состоит только из проверок констант и одного выхода, он схлопнется сам
                    // после упрощения условий выше.
                }

                // После изменения инструкций нужно обновить оффсеты и пересчитать стек, 
                // чтобы следующие проходы работали корректно
                if (changed)
                {
                    body.UpdateInstructionOffsets();
                    // Пересборка макросов может помочь, но иногда ломает метки, поэтому осторожно
                    // body.SimplifyMacros(method.Parameters); 
                }
            }

            // Финальный проход: удаление недостижимого кода
            RemoveUnreachableBlocks(method);
        }

        /// <summary>
        /// Сравнение значений с учетом типа операции ветвления.
        /// Возвращает true (ветвь выполняется), false (не выполняется) или null (неизвестно).
        /// </summary>
        private bool? CompareValues(object? actual, object? expected, Code opCode)
        {
            if (actual == null || expected == null) return null;

            // Приводим к общему типу для сравнения (поддержка int, long, double, float)
            double dActual = Convert.ToDouble(actual);
            double dExpected = Convert.ToDouble(expected);

            switch (opCode)
            {
                case Code.Beq:
                case Code.Beq_S:
                    return dActual == dExpected;
                case Code.Bne_Un:
                case Code.Bne_Un_S:
                    return dActual != dExpected;
                case Code.Bgt:
                case Code.Bgt_S:
                case Code.Bgt_Un:
                case Code.Bgt_Un_S:
                    return dActual > dExpected;
                case Code.Bge:
                case Code.Bge_S:
                case Code.Bge_Un:
                case Code.Bge_Un_S:
                    return dActual >= dExpected;
                case Code.Blt:
                case Code.Blt_S:
                case Code.Blt_Un:
                case Code.Blt_Un_S:
                    return dActual < dExpected;
                case Code.Ble:
                case Code.Ble_S:
                case Code.Ble_Un:
                case Code.Ble_Un_S:
                    return dActual <= dExpected;
                
                // Обработка brtrue/brfalse (проверка на ноль/не ноль)
                case Code.Brtrue:
                case Code.Brtrue_S:
                    return dActual != 0;
                case Code.Brfalse:
                case Code.Brfalse_S:
                    return dActual == 0;
            }
            return null;
        }

        private void RemoveUnreachableBlocks(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            if (instructions.Count == 0) return;

            var reachable = new HashSet<Instruction>();
            var queue = new Queue<Instruction>();

            // Старт с первой инструкции
            queue.Enqueue(instructions[0]);
            reachable.Add(instructions[0]);

            while (queue.Count > 0)
            {
                var curr = queue.Dequeue();
                int idx = instructions.IndexOf(curr);
                if (idx == -1) continue;

                // Если это не безусловный переход и не возврат, следующая инструкция достижима
                if (curr.OpCode.Code != Code.Br && curr.OpCode.Code != Code.Ret && curr.OpCode.Code != Code.Throw)
                {
                    if (idx + 1 < instructions.Count)
                    {
                        var next = instructions[idx + 1];
                        if (reachable.Add(next))
                            queue.Enqueue(next);
                    }
                }

                // Цель перехода достижима
                if (curr.Operand is Instruction target)
                {
                    if (reachable.Add(target))
                        queue.Enqueue(target);
                }
                else if (curr.Operand is Instruction[] targets)
                {
                    foreach (var t in targets)
                        if (reachable.Add(t)) queue.Enqueue(t);
                }
            }

            // Замена недостижимого на Nop
            bool changed = false;
            for (int i = instructions.Count - 1; i >= 0; i--)
            {
                if (!reachable.Contains(instructions[i]))
                {
                    instructions[i].OpCode = OpCodes.Nop;
                    instructions[i].Operand = null;
                    changed = true;
                }
            }

            if (changed)
            {
                method.Body.UpdateInstructionOffsets();
            }
        }

        private void CleanupMethod(MethodDef method)
        {
            var body = method.Body;
            var instructions = body.Instructions;
            bool changed = true;

            while (changed)
            {
                changed = false;
                // Удаляем NOP, на которые нет ссылок
                for (int i = instructions.Count - 1; i >= 0; i--)
                {
                    if (instructions[i].OpCode.Code == Code.Nop)
                    {
                        bool isTarget = false;
                        foreach (var instr in instructions)
                        {
                            if (instr.Operand == instructions[i]) { isTarget = true; break; }
                            if (instr.Operand is Instruction[] arr && arr.Contains(instructions[i])) { isTarget = true; break; }
                        }

                        if (!isTarget)
                        {
                            instructions.RemoveAt(i);
                            changed = true;
                        }
                    }
                }
            }
            
            // Попытка преобразовать оставшиеся простые конструкции в высокоуровневые (опционально)
            // body.SimplifyMacros(method.Parameters); 
            body.UpdateInstructionOffsets();
        }

        #region Helpers

        private bool IsStloc(Instruction instr, out int index)
        {
            index = -1;
            if (instr.Operand is Local l) index = l.Index;
            else if (instr.OpCode.Code == Code.Stloc_0) index = 0;
            else if (instr.OpCode.Code == Code.Stloc_1) index = 1;
            else if (instr.OpCode.Code == Code.Stloc_2) index = 2;
            else if (instr.OpCode.Code == Code.Stloc_3) index = 3;
            
            return index != -1 && (instr.OpCode.Code == Code.Stloc || instr.OpCode.Code == Code.Stloc_S || 
                   instr.OpCode.Code == Code.Stloc_0 || instr.OpCode.Code == Code.Stloc_1 || 
                   instr.OpCode.Code == Code.Stloc_2 || instr.OpCode.Code == Code.Stloc_3);
        }

        private bool IsLdloc(Instruction instr, out int index)
        {
            index = -1;
            if (instr.Operand is Local l) index = l.Index;
            else if (instr.OpCode.Code == Code.Ldloc_0) index = 0;
            else if (instr.OpCode.Code == Code.Ldloc_1) index = 1;
            else if (instr.OpCode.Code == Code.Ldloc_2) index = 2;
            else if (instr.OpCode.Code == Code.Ldloc_3) index = 3;

            return index != -1 && (instr.OpCode.Code == Code.Ldloc || instr.OpCode.Code == Code.Ldloc_S ||
                   instr.OpCode.Code == Code.Ldloc_0 || instr.OpCode.Code == Code.Ldloc_1 ||
                   instr.OpCode.Code == Code.Ldloc_2 || instr.OpCode.Code == Code.Ldloc_3);
        }

        private object? GetConstantValue(Instruction instr)
        {
            switch (instr.OpCode.Code)
            {
                case Code.Ldc_I4: return instr.Operand as int?;
                case Code.Ldc_I4_0: return 0;
                case Code.Ldc_I4_1: return 1;
                case Code.Ldc_I4_2: return 2;
                case Code.Ldc_I4_3: return 3;
                case Code.Ldc_I4_4: return 4;
                case Code.Ldc_I4_5: return 5;
                case Code.Ldc_I4_6: return 6;
                case Code.Ldc_I4_7: return 7;
                case Code.Ldc_I4_8: return 8;
                case Code.Ldc_I4_M1: return -1;
                case Code.Ldc_I8: return instr.Operand as long?;
                case Code.Ldc_R4: return instr.Operand as float?;
                case Code.Ldc_R8: return instr.Operand as double?;
                default: return null;
            }
        }

        private bool IsObfuscatedName(string name) => !string.IsNullOrEmpty(name) && (name.Length == 1 || name.StartsWith("?"));

        private void RenameWithAi(MethodDef method)
        {
            if (_aiAssistant == null) return;
            try
            {
                var snippet = string.Join("\n", method.Body.Instructions.Take(15).Select(x => x.ToString()));
                var newName = _aiAssistant.GetSuggestedName(method.Name, snippet, method.ReturnType?.ToString());
                if (!string.IsNullOrEmpty(newName) && IsValidIdentifier(newName))
                {
                    method.Name = newName;
                    Console.WriteLine($"[AI] Renamed {method.Name}");
                }
            }
            catch { }
        }

        private bool IsValidIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (!char.IsLetter(name[0]) && name[0] != '_') return false;
            foreach (var c in name) if (!char.IsLetterOrDigit(c) && c != '_') return false;
            return true;
        }

        #endregion

        public void Save(string path)
        {
            Console.WriteLine($"[*] Saving to: {path}");
            var opts = new ModuleWriterOptions(_module)
            {
                Logger = DummyLogger.NoThrowInstance,
                MetadataOptions = new MetadataOptions { Flags = MetadataFlags.KeepOldMaxStack }
            };
            _module.Write(path, opts);
            Console.WriteLine("[+] Done.");
        }

        public void Dispose()
        {
            _aiAssistant?.Dispose();
            _module.Dispose();
        }
    }
}
