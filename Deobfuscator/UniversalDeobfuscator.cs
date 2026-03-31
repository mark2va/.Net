using System;
using System.Collections.Generic;
using System.IO;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Deobfuscator
{
    /// <summary>
    /// Универсальный деобфускатор .NET сборок
    /// </summary>
    public class UniversalDeobfuscator
    {
        private readonly ModuleDefMD _module;
        private readonly AiAssistant _aiAssistant;

        public UniversalDeobfuscator(string filePath, AiConfig aiConfig)
        {
            _module = ModuleDefMD.Load(filePath);
            _aiAssistant = new AiAssistant(aiConfig);
        }

        public ModuleDefMD Module => _module;

        /// <summary>
        /// Запускает все этапы деобфускации
        /// </summary>
        public void Deobfuscate()
        {
            Console.WriteLine("[*] Начало деобфускации...");

            // Этап 1: Упрощение константных условий
            Console.WriteLine("[1/5] Упрощение константных условий...");
            SimplifyConstantConditions();

            // Этап 2: Распутывание потоков управления
            Console.WriteLine("[2/5] Распутывание потоков управления (goto, while)...");
            UnravelControlFlow();

            // Этап 3: Удаление мертвого кода
            Console.WriteLine("[3/5] Удаление мертвого кода...");
            RemoveDeadCode();

            // Этап 4: Упрощение ветвлений
            Console.WriteLine("[4/5] Упрощение ветвлений...");
            SimplifyBranches();

            // Этап 5: AI переименование (если включено)
            if (_aiAssistant != null && 
                System.Reflection.PropertyInfo.GetCurrentMethod() != null) // Проверка на наличие AI
            {
                var configField = typeof(UniversalDeobfuscator).GetField("_aiAssistant", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (configField != null)
                {
                    var assistant = configField.GetValue(this) as AiAssistant;
                    if (assistant != null)
                    {
                        var configProp = typeof(AiAssistant).GetProperty("Enabled") ?? 
                                        typeof(AiAssistant).GetField("_config");
                        Console.WriteLine("[5/5] AI переименование методов и переменных...");
                        RenameWithAi();
                    }
                }
            }

            Console.WriteLine("[*] Деобфускация завершена!");
        }

        /// <summary>
        /// Упрощает условия с константными значениями
        /// Поддерживает int, long, float, double
        /// </summary>
        private void SimplifyConstantConditions()
        {
            foreach (var type in _module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody || !method.Body.HasInstructions)
                        continue;

                    // Отслеживаем присваивания констант локальным переменным
                    var localConstants = new Dictionary<int, object>();
                    
                    for (int i = 0; i < method.Body.Instructions.Count; i++)
                    {
                        var instr = method.Body.Instructions[i];

                        // Обработка ldc.* инструкций
                        if (instr.OpCode.Code == Code.Ldc_I4 && i + 1 < method.Body.Instructions.Count)
                        {
                            var next = method.Body.Instructions[i + 1];
                            if (next.OpCode.Code == Code.Stloc_S || next.OpCode.Code == Code.Stloc)
                            {
                                var local = next.Operand as Local;
                                if (local != null)
                                {
                                    localConstants[local.Index] = instr.Operand;
                                }
                            }
                        }
                        else if (instr.OpCode.Code == Code.Ldc_I8 && i + 1 < method.Body.Instructions.Count)
                        {
                            var next = method.Body.Instructions[i + 1];
                            if (next.OpCode.Code == Code.Stloc_S || next.OpCode.Code == Code.Stloc)
                            {
                                var local = next.Operand as Local;
                                if (local != null)
                                {
                                    localConstants[local.Index] = instr.Operand;
                                }
                            }
                        }
                        else if ((instr.OpCode.Code == Code.Ldc_R4 || instr.OpCode.Code == Code.Ldc_R8) && 
                                 i + 1 < method.Body.Instructions.Count)
                        {
                            var next = method.Body.Instructions[i + 1];
                            if (next.OpCode.Code == Code.Stloc_S || next.OpCode.Code == Code.Stloc)
                            {
                                var local = next.Operand as Local;
                                if (local != null)
                                {
                                    localConstants[local.Index] = instr.Operand;
                                }
                            }
                        }

                        // Обработка сравнений с известными константами
                        if (instr.OpCode.Code == Code.Ldloc_S || instr.OpCode.Code == Code.Ldloc)
                        {
                            var local = instr.Operand as Local;
                            if (local != null && localConstants.ContainsKey(local.Index))
                            {
                                // Заменяем загрузку переменной на загрузку константы
                                var constValue = localConstants[local.Index];
                                
                                Instruction newInstr = null;
                                if (constValue is int)
                                    newInstr = Instruction.Create(OpCodes.Ldc_I4, (int)constValue);
                                else if (constValue is long)
                                    newInstr = Instruction.Create(OpCodes.Ldc_I8, (long)constValue);
                                else if (constValue is float)
                                    newInstr = Instruction.Create(OpCodes.Ldc_R4, (float)constValue);
                                else if (constValue is double)
                                    newInstr = Instruction.Create(OpCodes.Ldc_R8, (double)constValue);

                                if (newInstr != null)
                                {
                                    instr.OpCode = newInstr.OpCode;
                                    instr.Operand = newInstr.Operand;
                                }
                            }
                        }

                        // Упрощение условных переходов с константными условиями
                        if (instr.OpCode.FlowControl == FlowControl.Cond_Branch)
                        {
                            // Проверяем предыдущую инструкцию на наличие константного сравнения
                            if (i > 0)
                            {
                                var prev = method.Body.Instructions[i - 1];
                                if (prev.OpCode.Code == Code.Ldc_I4_0 || prev.OpCode.Code == Code.Ldc_I4_1)
                                {
                                    int constValue = 0;
                                    if (prev.OpCode.Code == Code.Ldc_I4_1) constValue = 1;
                                    
                                    // Преобразуем условный переход в безусловный или nop
                                    if (instr.OpCode.Code == Code.Brtrue || instr.OpCode.Code == Code.Brtrue_S)
                                    {
                                        if (constValue == 0)
                                        {
                                            instr.OpCode = OpCodes.Nop;
                                            instr.Operand = null;
                                        }
                                        else
                                        {
                                            var target = instr.Operand as Instruction;
                                            if (target != null)
                                            {
                                                instr.OpCode = OpCodes.Br;
                                                instr.Operand = target;
                                            }
                                        }
                                    }
                                    else if (instr.OpCode.Code == Code.Brfalse || instr.OpCode.Code == Code.Brfalse_S)
                                    {
                                        if (constValue == 1)
                                        {
                                            instr.OpCode = OpCodes.Nop;
                                            instr.Operand = null;
                                        }
                                        else
                                        {
                                            var target = instr.Operand as Instruction;
                                            if (target != null)
                                            {
                                                instr.OpCode = OpCodes.Br;
                                                instr.Operand = target;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    method.Body.OptimizeMacros();
                }
            }
        }

        /// <summary>
        /// Распутывает сложные потоки управления (goto chains, while true/false)
        /// </summary>
        private void UnravelControlFlow()
        {
            foreach (var type in _module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody || !method.Body.HasInstructions)
                        continue;

                    var instructions = method.Body.Instructions;

                    // Распутывание цепочек goto
                    for (int i = 0; i < instructions.Count; i++)
                    {
                        var instr = instructions[i];
                        
                        if (instr.OpCode.Code == Code.Br || instr.OpCode.Code == Code.Br_S)
                        {
                            var target = instr.Operand as Instruction;
                            if (target != null)
                            {
                                // Если цель - еще один goto, переходим к конечной цели
                                while (target.OpCode.Code == Code.Br || target.OpCode.Code == Code.Br_S)
                                {
                                    var nextTarget = target.Operand as Instruction;
                                    if (nextTarget == null) break;
                                    target = nextTarget;
                                }

                                // Обновляем цель перехода
                                if (instr.Operand != target)
                                {
                                    instr.Operand = target;
                                }
                            }
                        }
                    }

                    // Упрощение while(true) и while(false)
                    for (int i = 0; i < instructions.Count; i++)
                    {
                        var instr = instructions[i];

                        // Поиск паттерна: ldc.i4.1 / brtrue (while true)
                        if (i + 1 < instructions.Count)
                        {
                            var next = instructions[i + 1];
                            
                            if ((instr.OpCode.Code == Code.Ldc_I4_1 || 
                                 (instr.OpCode.Code == Code.Ldc_I4 && instr.Operand is int val && val == 1)) &&
                                (next.OpCode.Code == Code.Brtrue || next.OpCode.Code == Code.Brtrue_S))
                            {
                                // while(true) - удаляем проверку, оставляем тело цикла
                                instr.OpCode = OpCodes.Nop;
                                instr.Operand = null;
                                next.OpCode = OpCodes.Nop;
                                next.Operand = null;
                            }
                            else if ((instr.OpCode.Code == Code.Ldc_I4_0 || 
                                      (instr.OpCode.Code == Code.Ldc_I4 && instr.Operand is int val2 && val2 == 0)) &&
                                     (next.OpCode.Code == Code.Brtrue || next.OpCode.Code == Code.Brtrue_S))
                            {
                                // while(false) - превращаем в безусловный переход после тела
                                var target = next.Operand as Instruction;
                                if (target != null)
                                {
                                    instr.OpCode = OpCodes.Nop;
                                    instr.Operand = null;
                                    next.OpCode = OpCodes.Br;
                                    // Цель будет обновлена при оптимизации
                                }
                            }
                        }
                    }

                    // Упрощение switch с константными значениями
                    for (int i = 0; i < instructions.Count; i++)
                    {
                        var instr = instructions[i];
                        
                        if (instr.OpCode.Code == Code.Switch)
                        {
                            // Проверяем предыдущую инструкцию на константу
                            if (i > 0)
                            {
                                var prev = instructions[i - 1];
                                int constValue = -1;

                                if (prev.OpCode.Code == Code.Ldc_I4)
                                    constValue = (int)prev.Operand;
                                else if (prev.OpCode.Code == Code.Ldc_I4_0)
                                    constValue = 0;
                                else if (prev.OpCode.Code == Code.Ldc_I4_1)
                                    constValue = 1;
                                else if (prev.OpCode.Code == Code.Ldc_I4_2)
                                    constValue = 2;
                                else if (prev.OpCode.Code == Code.Ldc_I4_3)
                                    constValue = 3;
                                else if (prev.OpCode.Code == Code.Ldc_I4_4)
                                    constValue = 4;
                                else if (prev.OpCode.Code == Code.Ldc_I4_5)
                                    constValue = 5;
                                else if (prev.OpCode.Code == Code.Ldc_I4_6)
                                    constValue = 6;
                                else if (prev.OpCode.Code == Code.Ldc_I4_7)
                                    constValue = 7;
                                else if (prev.OpCode.Code == Code.Ldc_I4_8)
                                    constValue = 8;

                                if (constValue >= 0)
                                {
                                    var targets = instr.Operand as Instruction[];
                                    if (targets != null && constValue < targets.Length)
                                    {
                                        // Заменяем switch на прямой переход к нужному case
                                        prev.OpCode = OpCodes.Nop;
                                        prev.Operand = null;
                                        instr.OpCode = OpCodes.Br;
                                        instr.Operand = targets[constValue];
                                    }
                                }
                            }
                        }
                    }

                    method.Body.OptimizeMacros();
                }
            }
        }

        /// <summary>
        /// Удаляет недостижимый код
        /// </summary>
        private void RemoveDeadCode()
        {
            foreach (var type in _module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody || !method.Body.HasInstructions)
                        continue;

                    var reachable = new HashSet<Instruction>();
                    var worklist = new Stack<Instruction>();

                    // Начальная точка
                    if (method.Body.Instructions.Count > 0)
                    {
                        worklist.Push(method.Body.Instructions[0]);
                        reachable.Add(method.Body.Instructions[0]);
                    }

                    // Анализ достижимости
                    while (worklist.Count > 0)
                    {
                        var current = worklist.Pop();
                        var index = method.Body.Instructions.IndexOf(current);
                        
                        if (index < 0) continue;

                        var instr = current;
                        var nextIndex = index + 1;

                        // Обработка различных типов переходов
                        switch (instr.OpCode.FlowControl)
                        {
                            case FlowControl.Branch:
                            case FlowControl.Cond_Branch:
                                var target = instr.Operand as Instruction;
                                if (target != null && !reachable.Contains(target))
                                {
                                    reachable.Add(target);
                                    worklist.Push(target);
                                }

                                if (instr.OpCode.Code == Code.Brfalse || instr.OpCode.Code == Code.Brfalse_S ||
                                    instr.OpCode.Code == Code.Brtrue || instr.OpCode.Code == Code.Brtrue_S ||
                                    instr.OpCode.Code == Code.Beq || instr.OpCode.Code == Code.Beq_S ||
                                    instr.OpCode.Code == Code.Bne_Un || instr.OpCode.Code == Code.Bne_Un_S)
                                {
                                    if (nextIndex < method.Body.Instructions.Count)
                                    {
                                        var nextInstr = method.Body.Instructions[nextIndex];
                                        if (!reachable.Contains(nextInstr))
                                        {
                                            reachable.Add(nextInstr);
                                            worklist.Push(nextInstr);
                                        }
                                    }
                                }
                                break;

                            case FlowControl.Call:
                            case FlowControl.Next:
                                if (nextIndex < method.Body.Instructions.Count)
                                {
                                    var nextInstr = method.Body.Instructions[nextIndex];
                                    if (!reachable.Contains(nextInstr))
                                    {
                                        reachable.Add(nextInstr);
                                        worklist.Push(nextInstr);
                                    }
                                }
                                break;

                            case FlowControl.Ret:
                            case FlowControl.Throw:
                                break;

                            case FlowControl.Switch:
                                var targets = instr.Operand as Instruction[];
                                if (targets != null)
                                {
                                    foreach (var t in targets)
                                    {
                                        if (t != null && !reachable.Contains(t))
                                        {
                                            reachable.Add(t);
                                            worklist.Push(t);
                                        }
                                    }
                                }
                                if (nextIndex < method.Body.Instructions.Count)
                                {
                                    var nextInstr = method.Body.Instructions[nextIndex];
                                    if (!reachable.Contains(nextInstr))
                                    {
                                        reachable.Add(nextInstr);
                                        worklist.Push(nextInstr);
                                    }
                                }
                                break;
                        }
                    }

                    // Замена недостижимых инструкций на nop
                    foreach (var instr in method.Body.Instructions)
                    {
                        if (!reachable.Contains(instr) && 
                            instr.OpCode.Code != Code.Nop &&
                            instr.OpCode.Code != Code.Ret)
                        {
                            instr.OpCode = OpCodes.Nop;
                            instr.Operand = null;
                        }
                    }

                    method.Body.OptimizeMacros();
                }
            }
        }

        /// <summary>
        /// Упрощает последовательности ветвлений
        /// </summary>
        private void SimplifyBranches()
        {
            foreach (var type in _module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody || !method.Body.HasInstructions)
                        continue;

                    // Удаление последовательных nop
                    var instructions = method.Body.Instructions;
                    for (int i = instructions.Count - 1; i >= 0; i--)
                    {
                        if (instructions[i].OpCode.Code == Code.Nop)
                        {
                            // Проверяем, можно ли удалить nop
                            if (i > 0 && instructions[i - 1].OpCode.Code == Code.Nop)
                            {
                                // Оставляем только один nop между инструкциями
                                instructions.RemoveAt(i);
                            }
                        }
                    }

                    method.Body.OptimizeMacros();
                }
            }
        }

        /// <summary>
        /// Переименовывает методы и переменные с помощью AI
        /// </summary>
        private void RenameWithAi()
        {
            if (_aiAssistant == null) return;

            // Проверка доступности сервера
            if (!_aiAssistant.IsServerAvailable())
            {
                Console.WriteLine("[!] AI сервер недоступен. Пропускаем переименование.");
                return;
            }

            Console.WriteLine("[+] AI сервер доступен. Начинаем переименование...");

            foreach (var type in _module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (method.IsConstructor || method.IsSpecialName)
                        continue;

                    // Пропускаем уже осмысленные имена
                    if (!IsObfuscatedName(method.Name.String) || method.Name.String.StartsWith(".") || method.Name.String.Length > 30)
                        continue;

                    // Генерируем код метода для анализа
                    var methodCode = GenerateMethodCode(method);
                    
                    if (!string.IsNullOrEmpty(methodCode))
                    {
                        var newName = _aiAssistant.GenerateMethodName(methodCode, method.Name.String);
                        
                        if (!string.IsNullOrEmpty(newName) && newName != method.Name.String && IsObfuscatedName(method.Name.String))
                        {
                            Console.WriteLine($"  [AI] {method.Name.String} -> {newName}");
                            method.Name = newName;
                        }

                        // Генерация комментария
                        var comment = _aiAssistant.GenerateMethodComment(methodCode);
                        if (!string.IsNullOrEmpty(comment))
                        {
                            // Добавляем комментарий как атрибут или в XML документацию
                            // (в dnlib это требует дополнительной работы с CustomAttributes)
                        }
                    }

                    // Переименование локальных переменных
                    if (method.HasBody && method.Body.HasVariables)
                    {
                        foreach (var local in method.Body.Variables)
                        {
                            var currentName = local.Name;
                            if (string.IsNullOrEmpty(currentName) || IsObfuscatedName(currentName))
                            {
                                var context = GenerateMethodCode(method);
                                var newName = _aiAssistant.GenerateVariableName(
                                    local.Type.FullName, 
                                    context, 
                                    string.IsNullOrEmpty(currentName) ? "V_" + local.Index : currentName);
                                
                                if (!string.IsNullOrEmpty(newName) && newName != currentName)
                                {
                                    local.Name = newName;
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Проверяет, является ли имя обфусцированным
        /// </summary>
        private bool IsObfuscatedName(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            
            // Обфусцированные имена обычно короткие и содержат случайные символы
            if (name.Length <= 3 && !char.IsLetter(name[0]))
                return true;
            
            // Имена вида a, b, c, A, B, C, A<B>, <Module> и т.д.
            if (name.Length == 1)
                return true;
            
            // Имена с символами вроде '.', '<', '>', '$'
            if (name.Contains("<") || name.Contains(">") || name.Contains("$"))
                return true;

            return false;
        }

        /// <summary>
        /// Генерирует строковое представление кода метода для анализа AI
        /// </summary>
        private string GenerateMethodCode(MethodDef method)
        {
            if (!method.HasBody || !method.Body.HasInstructions)
                return string.Empty;

            var codeBuilder = new System.Text.StringBuilder();
            codeBuilder.AppendLine($"// Method: {method.FullName}");
            codeBuilder.AppendLine($"{method.ReturnType} {method.Name}({string.Join(", ", method.Parameters)})");
            codeBuilder.AppendLine("{");

            int instructionCount = 0;
            foreach (var instr in method.Body.Instructions)
            {
                if (instructionCount++ > 50) // Ограничиваем размер для AI
                {
                    codeBuilder.AppendLine("    // ... (truncated)");
                    break;
                }

                codeBuilder.AppendLine($"    {instr.OpCode} {instr.Operand ?? ""}");
            }

            codeBuilder.AppendLine("}");
            return codeBuilder.ToString();
        }

        /// <summary>
        /// Сохраняет деобфусцированную сборку
        /// </summary>
        public void Save(string outputPath)
        {
            _module.Write(outputPath);
            Console.WriteLine($"[*] Сборка сохранена: {outputPath}");
        }

        public void Dispose()
        {
            _module?.Dispose();
        }
    }
}
