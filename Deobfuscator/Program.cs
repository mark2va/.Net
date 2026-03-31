using System;
using System.IO;

namespace Deobfuscator
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 2)
            {
                PrintUsage();
                return;
            }

            string inputFile = args[0];
            string outputFile = args[1];
            
            // Настройки AI по умолчанию
            var aiConfig = new AiConfig
            {
                Enabled = false,
                ApiUrl = "http://localhost:11434",
                Model = "llama3",
                TimeoutSeconds = 120
            };

            // Парсинг аргументов командной строки
            for (int i = 2; i < args.Length; i++)
            {
                switch (args[i].ToLower())
                {
                    case "--ai":
                        aiConfig.Enabled = true;
                        break;
                    
                    case "--ai-url":
                        if (i + 1 < args.Length)
                        {
                            aiConfig.ApiUrl = args[++i];
                        }
                        else
                        {
                            Console.WriteLine("[!] Ошибка: --ai-url требует указания URL");
                            return;
                        }
                        break;
                    
                    case "--ai-model":
                        if (i + 1 < args.Length)
                        {
                            aiConfig.Model = args[++i];
                        }
                        else
                        {
                            Console.WriteLine("[!] Ошибка: --ai-model требует указания имени модели");
                            return;
                        }
                        break;
                    
                    case "--ai-timeout":
                        if (i + 1 < args.Length)
                        {
                            int timeout;
                            if (int.TryParse(args[++i], out timeout))
                            {
                                aiConfig.TimeoutSeconds = timeout;
                            }
                            else
                            {
                                Console.WriteLine("[!] Ошибка: --ai-timeout требует числовое значение");
                                return;
                            }
                        }
                        else
                        {
                            Console.WriteLine("[!] Ошибка: --ai-timeout требует указания времени в секундах");
                            return;
                        }
                        break;
                    
                    case "--help":
                    case "-h":
                        PrintUsage();
                        return;
                }
            }

            // Проверка входного файла
            if (!File.Exists(inputFile))
            {
                Console.WriteLine($"[!] Ошибка: Файл не найден: {inputFile}");
                return;
            }

            Console.WriteLine("===========================================");
            Console.WriteLine("   Универсальный деобфускатор .NET сборок");
            Console.WriteLine("===========================================");
            Console.WriteLine();
            Console.WriteLine($"Входной файл:  {Path.GetFullPath(inputFile)}");
            Console.WriteLine($"Выходной файл: {Path.GetFullPath(outputFile)}");
            Console.WriteLine();

            if (aiConfig.Enabled)
            {
                Console.WriteLine("AI настройки:");
                Console.WriteLine($"  Включено:     {aiConfig.Enabled}");
                Console.WriteLine($"  URL сервера:  {aiConfig.ApiUrl}");
                Console.WriteLine($"  Модель:       {aiConfig.Model}");
                Console.WriteLine($"  Таймаут:      {aiConfig.TimeoutSeconds} сек.");
                Console.WriteLine();
            }

            try
            {
                using (var deobfuscator = new UniversalDeobfuscator(inputFile, aiConfig))
                {
                    deobfuscator.Deobfuscate();
                    deobfuscator.Save(outputFile);
                }

                Console.WriteLine();
                Console.WriteLine("[+] Деобфускация успешно завершена!");
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"[!] Произошла ошибка: {ex.Message}");
                Console.WriteLine($"Детали: {ex.StackTrace}");
                Environment.Exit(1);
            }
        }

        static void PrintUsage()
        {
            Console.WriteLine("Использование:");
            Console.WriteLine("  Deobfuscator.exe <входной_файл> <выходной_файл> [опции]");
            Console.WriteLine();
            Console.WriteLine("Опции:");
            Console.WriteLine("  --ai              Включить AI переименование методов и переменных");
            Console.WriteLine("  --ai-url <url>    URL AI сервера (по умолчанию: http://localhost:11434)");
            Console.WriteLine("  --ai-model <name> Имя модели (по умолчанию: llama3)");
            Console.WriteLine("  --ai-timeout <s>  Таймаут запроса в секундах (по умолчанию: 120)");
            Console.WriteLine("  --help, -h        Показать эту справку");
            Console.WriteLine();
            Console.WriteLine("Примеры:");
            Console.WriteLine("  Deobfuscator.exe obfuscated.exe cleaned.exe");
            Console.WriteLine("  Deobfuscator.exe input.exe output.exe --ai");
            Console.WriteLine("  Deobfuscator.exe input.exe output.exe --ai --ai-url http://localhost:1234 --ai-model codellama");
            Console.WriteLine();
            Console.WriteLine("Поддерживаемые AI серверы:");
            Console.WriteLine("  - Ollama (http://localhost:11434)");
            Console.WriteLine("  - LM Studio (http://localhost:1234)");
            Console.WriteLine("  - Любые OpenAI-совместимые API серверы");
        }
    }
}
