using System;
using System.IO;

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

            if (args.Length >= 2)
            {
                outputFile = args[1];
            }
            else
            {
                // Генерация имени выходного файла
                string dir = Path.GetDirectoryName(inputFile) ?? "";
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
            
            // Парсинг аргументов начиная с 1 (так как 0 - это входной файл)
            for (int i = 1; i < args.Length; i++)
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
                Console.WriteLine($"[Fatal Error]: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                return 1;
            }
        }

        static void PrintUsage()
        {
            Console.WriteLine("Usage: Deobfuscator.exe <input.exe> [output.exe] [options]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --ai                 Enable AI renaming");
            Console.WriteLine("  --ai-url <url>       AI Server URL (default: http://localhost:11434)");
            Console.WriteLine("  --ai-model <name>    Model name (default: llama3)");
            Console.WriteLine("  --ai-timeout <sec>   Request timeout (default: 120)");
        }
    }
}
