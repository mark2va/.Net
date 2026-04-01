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
            if (args.Length >= 2)
            {
                // Если указан второй аргумент, используем его как путь выхода
                outputFile = args[1];
            }
            else
            {
                // Если аргумент один, создаем имя в той же папке с суффиксом _deob
                string directory = Path.GetDirectoryName(inputFile);
                string fileNameWithoutExt = Path.GetFileNameWithoutExtension(inputFile);
                string extension = Path.GetExtension(inputFile);
                
                if (string.IsNullOrEmpty(directory))
                {
                    // Если файл в текущей папке
                    outputFile = fileNameWithoutExt + "_deob" + extension;
                }
                else
                {
                    outputFile = Path.Combine(directory, fileNameWithoutExt + "_deob" + extension);
                }
                
                Console.WriteLine("[*] Output file not specified. Using: " + outputFile);
            }

            if (!File.Exists(inputFile))
            {
                Console.WriteLine("[Error] File not found: " + inputFile);
                return 1;
            }

            // Парсинг аргументов вручную для совместимости
            var aiConfig = new AiConfig();
            
            for (int i = 1; i < args.Length; i++) // Начинаем с 1, так как 0 это файл
            {
                switch (args[i])
                {
                    case "--ai":
                        aiConfig.Enabled = true;
                        break;
                    case "--ai-url":
                        if (i + 1 < args.Length) aiConfig.ApiUrl = args[++i];
                        break;
                    case "--ai-model":
                        if (i + 1 < args.Length) aiConfig.Model = args[++i];
                        break;
                    case "--ai-timeout":
                        if (i + 1 < args.Length && int.TryParse(args[++i], out int t))
                            aiConfig.TimeoutSeconds = t;
                        break;
                }
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
                Console.WriteLine("[Fatal Error]: " + ex.Message);
                Console.WriteLine(ex.StackTrace);
                return 1;
            }
        }

        static void PrintUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  Deobfuscator.exe <input.exe>                     (Saved as input_deob.exe)");
            Console.WriteLine("  Deobfuscator.exe <input.exe> <output.exe>        (Custom output path)");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --ai                 Enable AI renaming (requires local server)");
            Console.WriteLine("  --ai-url <url>       AI Server URL (default: http://localhost:11434)");
            Console.WriteLine("  --ai-model <name>    Model name (default: codellama)");
            Console.WriteLine("  --ai-timeout <sec>   Request timeout in seconds (default: 120)");
        }
    }
}
