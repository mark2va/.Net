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

            if (args.Length < 2)
            {
                PrintUsage();
                return 1;
            }

            string inputFile = args[0];
            string outputFile = args[1];

            if (!File.Exists(inputFile))
            {
                Console.WriteLine($"[Error] File not found: {inputFile}");
                return 1;
            }

            // Парсинг аргументов вручную для совместимости
            var aiConfig = new AiConfig();
            
            for (int i = 2; i < args.Length; i++)
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
            Console.WriteLine("Usage: Deobfuscator.exe <input.exe> <output.exe> [options]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --ai                 Enable AI renaming (requires local server)");
            Console.WriteLine("  --ai-url <url>       AI Server URL (default: http://localhost:11434)");
            Console.WriteLine("  --ai-model <name>    Model name (default: llama3)");
            Console.WriteLine("  --ai-timeout <sec>   Request timeout in seconds (default: 120)");
        }
    }
}
