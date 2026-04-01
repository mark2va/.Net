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
        public UniversalDeobfuscator(string filePath)
        {
            _module = ModuleDefMD.Load(filePath);
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
                        // Основной алгоритм распутывания через эмуляцию
                        EmulateAndUnravel(method);
                        
                        // Очистка мусора (NOP)
                        CleanupMethod(method);
                        count++;
                        Console.WriteLine($"[+] Unraveled: {method.FullName}");
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
        /// Эмулирует выполнение метода для определения реального потока управления.
        /// Заменяет запутанный код на линейный.
        /// </summary>
        private void EmulateAndUnravel(MethodDef method)
        {
            var body = method.Body;
            if (body == null || !body.HasInstructions) return;
            var instructions = body.Instructions;
            if (instructions.Count == 0) return;
            // Словарь для хранения значений локальных переменных во время эмуляции
            var localValues = new Dictionary<int, object?>();
            for (int i = 0; i < method.Body.Variables.Count; i++)
                localValues[i] = null;
            // Список инструкций для нового, чистого метода
            var newInstructions = new List<Instruction>();
            
            // Стек для обработки переходов (эмуляция выполнения)
            var queue = new Queue<Instruction>();
            var visited = new HashSet<Instruction>();
            
            // Начинаем с первой инструкции
            queue.Enqueue(instructions[0]);
            visited.Add(instructions[0]);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                int index = instructions.IndexOf(current);
                
                if (index == -1) continue;
                // Проходим по инструкциям, пока не встретим безусловный переход или возврат
                for (int i = index; i < instructions.Count; i++)
                {
                    var instr = instructions[i];
                    
                    // Пропускаем NOP в исходном коде при эмуляции, если они уже обработаны
                    if (instr.OpCode.Code == Code.Nop && visited.Contains(instr) && i != index)
                        continue;
                    // Обработка присваивания константы локали (ldc -> stloc)
                    if (i + 1 < instructions.Count)
                    {
                        var next = instructions[i + 1];
                        if (IsStloc(next, out int localIdx))
                        {
                            var val = GetConstantValue(instr);
                            if (val != null)
                            {
                                localValues[localIdx] = val;
                                // Добавляем в новый код, но дальше будем использовать значение из словаря
                                newInstructions.Add(CloneInstruction(instr));
                                newInstructions.Add(CloneInstruction(next));
                                i++; // Пропускаем следующую инструкцию, так как обработали пару
                                continue;
                            }
                        }
                    }
                    // Обработка условных переходов
                    if (instr.OpCode.FlowControl == FlowControl.Cond_Branch)
                    {
                        Instruction? target = instr.Operand as Instruction;
                        if (target == null) continue;
                        bool? conditionResult = null;
                        // Пытаемся вычислить условие, глядя назад (ldloc + ldc + branch)
                        if (i >= 2)
                        {
                            var prevInstr = instructions[i - 1];
                            var prevPrevInstr = instructions[i - 2];
                            if (IsLdloc(prevPrevInstr, out int checkLocalIdx))
                            {
                                var constVal = GetConstantValue(prevInstr);
                                if (constVal != null && localValues.ContainsKey(checkLocalIdx))
                                {
                                    var actualVal = localValues[checkLocalIdx];
                                    conditionResult = CompareValues(actualVal, constVal, instr.OpCode.Code);
                                }
                            }
                        }
                        
                        // Если смогли вычислить условие
                        if (conditionResult.HasValue)
                        {
                            if (conditionResult.Value)
                            {
                                // Условие истинно -> переходим к target
                                if (visited.Add(target))
                                    queue.Enqueue(target);
                            }
                            else
                            {
                                // Условие ложно -> идем дальше (следующая инструкция)
                                if (i + 1 < instructions.Count && visited.Add(instructions[i + 1]))
                                    queue.Enqueue(instructions[i + 1]);
                            }
                            
                            // В новый код добавляем только реальный путь, но пока просто пропускаем ветвление
                            // (оно будет заменено на прямой код в другом месте или удалено)
                            continue; 
                        }
                        else
                        {
                            // Не смогли вычислить, добавляем как есть (fallback)
                            newInstructions.Add(CloneInstruction(instr));
                        }
                    }
                    else if (instr.OpCode.FlowControl == FlowControl.Branch)
                    {
                        // Безусловный переход
                        if (instr.Operand is Instruction brTarget)
                        {
                            if (visited.Add(brTarget))
                                queue.Enqueue(brTarget);
                            // Не добавляем сам переход в новый код, если это часть обфускации
                            continue;
                        }
                        newInstructions.Add(CloneInstruction(instr));
                    }
                    else if (instr.OpCode.FlowControl == FlowControl.Return)
                    {
                        newInstructions.Add(CloneInstruction(instr));
                        break; // Конец пути
                    }
                    else
                    {
                        // Обычная инструкция
                        // Избегаем дублирования, если инструкция уже была добавлена через другой путь
                        // Но для простоты эмуляции добавляем всё, что встречаем на пути
                        if (!newInstructions.Any(x => x.Offset == instr.Offset))
                             newInstructions.Add(CloneInstruction(instr));
                    }
                }
            }
            // Если удалось собрать хоть что-то, заменяем тело метода
            if (newInstructions.Count > 0)
            {
                ReplaceMethodBody(method, newInstructions);
            }
        }
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
        private bool? CompareValues(object? actual, object? expected, Code opCode)
        {
            if (actual == null || expected == null) return null;
            try
            {
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
                    case Code.Brtrue:
                    case Code.Brtrue_S:
                        return dActual != 0;
                    case Code.Brfalse:
                    case Code.Brfalse_S:
                        return dActual == 0;
                }
            }
            catch { }
            return null;
        }
        private void CleanupMethod(MethodDef method)
        {
            var body = method.Body;
            if (body == null) return;
            
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = body.Instructions.Count - 1; i >= 0; i--)
                {
                    var instr = body.Instructions[i];
                    if (instr.OpCode.Code == Code.Nop)
                    {
                        bool isTarget = false;
                        foreach (var other in body.Instructions)
                        {
                            if (other.Operand == instr) { isTarget = true; break; }
                            if (other.Operand is Instruction[] arr && arr.Contains(instr)) { isTarget = true; break; }
                        }
                        if (!isTarget)
                        {
                            body.Instructions.RemoveAt(i);
                            changed = true;
                        }
                    }
                }
            }
            body.UpdateInstructionOffsets();
        }
        private Instruction CloneInstruction(Instruction orig)
        {
            var opCode = orig.OpCode;
            var operand = orig.Operand;
            if (operand is Local local)
                return Instruction.Create(opCode, local);
            if (operand is Parameter param)
                return Instruction.Create(opCode, param);
            if (operand is Instruction target)
                return Instruction.Create(opCode, target);
            if (operand is Instruction[] targets)
                return Instruction.Create(opCode, targets);
            if (operand is string str)
                return Instruction.Create(opCode, str);
            if (operand is int i)
                return Instruction.Create(opCode, i);
            if (operand is long l)
                return Instruction.Create(opCode, l);
            if (operand is float f)
                return Instruction.Create(opCode, f);
            if (operand is double d)
                return Instruction.Create(opCode, d);
            if (operand is ITypeDefOrRef type)
                return Instruction.Create(opCode, type);
            if (operand is MethodDef md)
                return Instruction.Create(opCode, md);
            if (operand is FieldDef fd)
                return Instruction.Create(opCode, fd);
            if (operand is MemberRef mr)
                return Instruction.Create(opCode, mr);
            return new Instruction(opCode, operand);
        }
        private void ReplaceMethodBody(MethodDef method, List<Instruction> newInstructions)
        {
            var body = method.Body;
            body.Instructions.Clear();
            foreach (var instr in newInstructions)
                body.Instructions.Add(instr);
            
            body.UpdateInstructionOffsets();
            body.SimplifyMacros(method.Parameters);
        }
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
            _module.Dispose();
        }
    }
}
// Пример использования в Program.cs (не в этом файле):
/*
class Program
{
    static void Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: Deobfuscator.exe <input.dll> <output.dll>");
            return;
        }
        using (var deob = new UniversalDeobfuscator(args[0]))
        {
            deob.Deobfuscate();
            deob.Save(args[1]);
        }
    }
}
