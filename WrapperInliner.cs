using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Deobfuscator
{
    /// <summary>
    /// Отвечает за инлайн тривиальных wrapper-методов и обнаружение циклических зависимостей.
    /// </summary>
    public class WrapperInliner
    {
        private readonly Action<string>? _log;

        public WrapperInliner(Action<string>? log = null)
        {
            _log = log;
        }

        public int Inline(ModuleDef module)
        {
            var wrappers = new Dictionary<MethodDef, MethodDef>();
            int inlinedCount = 0;

            foreach (var type in module.GetTypes())
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
                            _log?.Invoke($"  Found wrapper: {method.FullName} -> {targetMethodDef.FullName}");
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
                    _log?.Invoke($"  Detected {cyclicMethods.Count} methods in cyclic dependencies, skipping them.");
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

                    foreach (var type in module.GetTypes())
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
                                        _log?.Invoke($"    Inlined: {calledMethodDef.FullName} -> {target.FullName}");
                                    }
                                }
                            }
                        }
                    }
                }
            }

            Console.WriteLine($"[+] Inlined {inlinedCount} wrapper calls across {wrappers.Count} wrapper methods.");
            return inlinedCount;
        }

        /// <summary>
        /// Обнаруживает методы, участвующие в циклических зависимостях.
        /// </summary>
        public HashSet<MethodDef> DetectCyclicDependencies(Dictionary<MethodDef, MethodDef> wrappers)
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
    }
}
