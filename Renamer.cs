using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;

namespace Deobfuscator
{
    /// <summary>
    /// Отвечает за переименование обфусцированных элементов (типы, методы, поля).
    /// </summary>
    public class Renamer
    {
        private readonly AiConfig _aiConfig;
        private readonly Action<string>? _log;
        private readonly bool _useAi;
        private AiAssistant? _ai;

        public Renamer(AiConfig aiConfig, Action<string>? log = null)
        {
            _aiConfig = aiConfig;
            _log = log;
            _useAi = aiConfig.Enabled;
        }

        public void Rename(ModuleDef module)
        {
            if (_useAi)
            {
                try
                {
                    _ai = new AiAssistant(_aiConfig);
                    _log?.Invoke("AI assistant initialized for renaming.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[!] Failed to initialize AI: {ex.Message}. Falling back to generic naming.");
                    _log?.Invoke($"AI init failed: {ex.Message}");
                }
            }

            int renamedTypes = 0;
            int renamedMethods = 0;
            int renamedFields = 0;

            // Переименование типов
            foreach (var type in module.GetTypes())
            {
                if (IsObfuscated(type.Name))
                {
                    string newName = GenerateTypeName(type);
                    _log?.Invoke($"  Renaming type: {type.FullName} -> {newName}");
                    type.Name = newName;
                    renamedTypes++;
                }
            }

            // Переименование методов
            foreach (var type in module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (IsObfuscated(method.Name))
                    {
                        string newName = GenerateMethodName(method);
                        _log?.Invoke($"  Renaming method: {method.FullName} -> {newName}");
                        method.Name = newName;
                        renamedMethods++;
                    }
                }
            }

            // Переименование полей
            foreach (var type in module.GetTypes())
            {
                foreach (var field in type.Fields)
                {
                    if (IsObfuscated(field.Name))
                    {
                        string newName = GenerateFieldName(field);
                        _log?.Invoke($"  Renaming field: {field.FullName} -> {newName}");
                        field.Name = newName;
                        renamedFields++;
                    }
                }
            }

            Console.WriteLine($"[+] Renamed: {renamedTypes} types, {renamedMethods} methods, {renamedFields} fields.");
            _log?.Invoke($"Rename summary: {renamedTypes} types, {renamedMethods} methods, {renamedFields} fields.");
        }

        private bool IsObfuscated(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;

            // Специальные имена компилятора
            if (name.StartsWith("<") || name.StartsWith(".") || name.Contains(">")) return false;

            // Если имя содержит только один символ или странные Unicode символы
            if (name.Length <= 2) return true;

            // Проверка на "мусорные" имена (не ASCII или странные комбинации)
            int nonAsciiCount = 0;
            foreach (char c in name)
            {
                if (c > 127 || !char.IsLetterOrDigit(c))
                {
                    nonAsciiCount++;
                }
            }

            return nonAsciiCount > name.Length / 2;
        }

        private string GenerateTypeName(TypeDef type)
        {
            if (_ai != null && _useAi)
            {
                try
                {
                    return _ai.GetSuggestedName(type.Name, "", "Class");
                }
                catch { }
            }

            return $"Class_{GetHashCode(type)}";
        }

        private string GenerateMethodName(MethodDef method)
        {
            if (_ai != null && _useAi)
            {
                try
                {
                    return _ai.GetSuggestedName(method.Name, "", method.ReturnType?.ToString() ?? "void");
                }
                catch { }
            }

            return $"Method_{GetHashCode(method)}";
        }

        private string GenerateFieldName(FieldDef field)
        {
            if (_ai != null && _useAi)
            {
                try
                {
                    return _ai.GetSuggestedName(field.Name, "", field.FieldType.ToString());
                }
                catch { }
            }

            return $"Field_{GetHashCode(field)}";
        }

        private int GetHashCode(object obj)
        {
            return Math.Abs(obj.GetHashCode() % 10000);
        }
    }
}
