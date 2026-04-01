using System;
using System.IO;
using System.Reflection;

namespace Deobfuscator
{
    class Program
    {
        static int Main(string[] args)
        {
            Console.WriteLine("=== Universal .NET Deobfuscator ===");
            Console.WriteLine("Based on dnlib");
            Console.WriteLine();

            if (args.Length < 1)
            {
                PrintUsage();
                return 1;
            }

            string inputFile = args[0];
            string outputFile;

            // Логика определения выходного файла
            if (args.Length >= 2 && !args[1].StartsWith("--"))
            {
                outputFile = args[1];
            }
            else
            {
                string dir = Path.GetDirectoryName(inputFile) ?? ".";
                string name = Path.GetFileNameWithoutExtension(inputFile);
                string ext = Path.GetExtension(inputFile);
                outputFile = Path.Combine(dir, name + "_deob" + ext);
                Console.WriteLine($"[*] Output file not specified. Using: {outputFile}");
            }

            if (!File.Exists(inputFile))
            {
                Console.WriteLine($"[Error] File not found: {inputFile}");
                return 1;
            }

            var aiConfig = new AiConfig();
            
            // Парсинг флагов
            for (int i = 2; i < args.Length; i++) // Начинаем с 2, т.к. 0=file, 1=output(опционально)
            {
                // Если второй аргумент был флагом, то outputFile еще не задан явно, значит i=1 это флаг
                // Корректировка логики парсинга:
                // Если args[1] начинается с --, то output генерируется автоматически, и парсим с 1
                int startIdx = (args.Length > 1 && !args[1].StartsWith("--")) ? 2 : 1;
                
                // Перезапустим цикл правильно
                for (int j = startIdx; j < args.Length; j++)
                {
                    switch (args[j])
                    {
                        case "--ai":
                            aiConfig.Enabled = true;
                            break;
                        case "--ai-url":
                            if (j + 1 < args.Length) aiConfig.ApiUrl = args[++j];
                            break;
                        case "--ai-model":
                            if (j + 1 < args.Length) aiConfig.Model = args[++j];
                            break;
                        case "--ai-timeout":
                            if (j + 1 < args.Length && int.TryParse(args[++j], out int t))
                                aiConfig.TimeoutSeconds = t;
                            break;
                    }
                }
                break; // Выход из внешнего цикла, так как внутренний все сделал
            }

            try
            {
                using (var deobfuscator = new UniversalDeobfuscator(inputFile, aiConfig))
                {
                    deobfuscator.Deobfuscate();
                    deobfuscator.Save(outputFile);
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Fatal Error]: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                return 1;
            }
        }

        static void PrintUsage()
        {
            Console.WriteLine("Usage: Deobfuscator.exe <input.exe> [output.exe] [options]");
            Console.WriteLine();
            Console.WriteLine("Arguments:");
            Console.WriteLine("  input.exe       Path to obfuscated file");
            Console.WriteLine("  output.exe      (Optional) Path to save result. Default: <input>_deob.exe");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --ai                 Enable AI renaming");
            Console.WriteLine("  --ai-url <url>       AI Server URL (default: http://localhost:11434)");
            Console.WriteLine("  --ai-model <name>    Model name (default: llama3)");
            Console.WriteLine("  --ai-timeout <sec>   Request timeout (default: 120)");
        }
    }
}
