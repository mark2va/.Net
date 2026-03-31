using System;
using System.IO;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Threading.Tasks;
using dnlib.DotNet;

namespace Deobfuscator
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            Console.WriteLine("=== Универсальный .NET Деобфускатор ===");
            Console.WriteLine("На основе dnlib с поддержкой AI-ассистента\n");

            // Определяем аргументы командной строки
            var inputArg = new Argument<string>("input", "Входной обфусцированный файл (.exe/.dll)");
            var outputArg = new Argument<string>("output", "Выходной очищенный файл");
            
            var aiOption = new Option<bool>("--ai", "Включить AI-ассистента для переименования")
            {
                Arity = ArgumentArity.Zero
            };
            
            var aiUrlOption = new Option<string>(
                new[] { "--ai-url" }, 
                () => "http://localhost:11434",
                "URL AI-сервера (по умолчанию: http://localhost:11434 для Ollama)"
            );
            
            var aiModelOption = new Option<string>(
                new[] { "--ai-model" }, 
                () => "llama3",
                "Имя AI-модели (например: llama3, codellama, deepseek-coder, mistral)"
            );
            
            var aiTimeoutOption = new Option<int>(
                new[] { "--ai-timeout" }, 
                () => 120,
                "Таймаут запросов к AI в секундах (по умолчанию: 120)"
            );

            var rootCommand = new RootCommand("Универсальный деобфускатор .NET сборок с поддержкой AI")
            {
                inputArg,
                outputArg,
                aiOption,
                aiUrlOption,
                aiModelOption,
                aiTimeoutOption
            };

            rootCommand.Handler = CommandHandler.Create(
                async (string input, string output, bool ai, string aiUrl, string aiModel, int aiTimeout) =>
                {
                    try
                    {
                        // Проверка входного файла
                        if (!File.Exists(input))
                        {
                            Console.WriteLine($"[!] Ошибка: Файл '{input}' не найден.");
                            return 1;
                        }

                        Console.WriteLine($"[*] Загрузка сборки: {input}");
                        
                        // Загружаем модуль с помощью dnlib
                        var module = ModuleDefMD.Load(input);
                        
                        // Настраиваем AI конфигурацию
                        AiConfig aiConfig = null;
                        if (ai)
                        {
                            aiConfig = new AiConfig(aiUrl, aiModel, aiTimeout);
                            Console.WriteLine($"[*] AI включен: {aiConfig}");
                        }
                        else
                        {
                            Console.WriteLine("[*] AI отключен (используйте --ai для включения)");
                        }

                        // Создаем деобфускатор и запускаем процесс
                        var deobfuscator = new UniversalDeobfuscator(module, aiConfig);
                        var changesCount = await deobfuscator.DeobfuscateAsync();

                        // Сохраняем результат
                        Console.WriteLine($"\n[*] Сохранение результата в: {output}");
                        module.Write(output);
                        
                        Console.WriteLine($"\n[+] Деобфускация завершена успешно!");
                        Console.WriteLine($"[+] Внесено изменений: {changesCount}");
                        Console.WriteLine($"[+] Результат сохранен в: {output}");
                        
                        // Освобождаем ресурсы
                        module.Dispose();
                        
                        return 0;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"\n[!] Критическая ошибка: {ex.Message}");
                        Console.WriteLine($"[!] Stack trace: {ex.StackTrace}");
                        return 1;
                    }
                });

            // Парсим и выполняем команду
            return await rootCommand.InvokeAsync(args);
        }
    }
}
