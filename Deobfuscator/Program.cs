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
// Пример использования в Program.cs
var aiConfig = new AiConfig 
{ 
    Enabled = true, 
    Endpoint = "http://192.168.31.130:11434/api/generate", // Ваш адрес
    Model = "codellama" 
};

using var deob = new UniversalDeobfuscator(args[0], aiConfig);
deob.Deobfuscate();
deob.Save(args[0] + ".deob.exe");

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
