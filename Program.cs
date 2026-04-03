using System;
using System.IO;
using System.Linq;

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
            bool debugMode = args.Contains("--debug");

            if (args.Length >= 2 && !args[1].StartsWith("--"))
            {
                outputFile = args[1];
            }
            else
            {
                string dir = Path.GetDirectoryName(inputFile) ?? "";
                string name = Path.GetFileNameWithoutExtension(inputFile);
                string ext = Path.GetExtension(inputFile);
                outputFile = Path.Combine(dir, name + "_deob" + ext);
                if (debugMode)
                    Console.WriteLine($"[*] Output file not specified. Using: {outputFile}");
            }

            if (!File.Exists(inputFile))
            {
                Console.WriteLine($"[Error] File not found: {inputFile}");
                return 1;
            }

            var aiConfig = new AiConfig 
            { 
                Enabled = false,
                ApiUrl = "http://localhost:11434",
                Model = "codellama",
                TimeoutSeconds = 120
            };

            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--ai":
                        aiConfig.Enabled = true;
                        Console.WriteLine("[AI] AI assistant enabled.");
                        break;
                    case "--ai-url":
                        if (i + 1 < args.Length)
                        {
                            aiConfig.ApiUrl = args[++i];
                            Console.WriteLine($"[AI] Using API URL: {aiConfig.ApiUrl}");
                        }
                        break;
                    case "--ai-model":
                        if (i + 1 < args.Length)
                        {
                            aiConfig.Model = args[++i];
                            Console.WriteLine($"[AI] Using model: {aiConfig.Model}");
                        }
                        break;
                    case "--ai-timeout":
                        if (i + 1 < args.Length && int.TryParse(args[++i], out int timeout))
                        {
                            aiConfig.TimeoutSeconds = timeout;
                            Console.WriteLine($"[AI] Using timeout: {timeout}s");
                        }
                        break;
                    case "--debug":
                        break;
                }
            }

            try
            {
                using (var deobfuscator = new UniversalDeobfuscator(inputFile, aiConfig, debugMode))
                {
                    deobfuscator.Deobfuscate();
                    deobfuscator.Save(outputFile);
                }
                
                if (debugMode)
                {
                    string logPath = Path.Combine(Path.GetDirectoryName(inputFile) ?? "", "deob_log.txt");
                    Console.WriteLine($"[+] Debug log saved to: {logPath}");
                }
                
                Console.WriteLine($"[+] Successfully saved to: {outputFile}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Fatal Error]: {ex.Message}");
                if (debugMode)
                {
                    Console.WriteLine(ex.StackTrace);
                }
                return 1;
            }
        }

        static void PrintUsage()
        {
            Console.WriteLine("Usage: Deobfuscator.exe <input.exe> [output.exe] [options]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --ai                 Enable AI renaming (requires local Ollama server)");
            Console.WriteLine("  --ai-url <url>       AI Server URL (default: http://localhost:11434)");
            Console.WriteLine("  --ai-model <name>    Model name (default: codellama)");
            Console.WriteLine("  --ai-timeout <sec>   Request timeout in seconds (default: 120)");
            Console.WriteLine("  --debug              Enable detailed logging to console and file");
        }
    }
}
