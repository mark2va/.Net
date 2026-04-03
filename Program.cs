using System;
using System.IO;
using System.Linq;

namespace Deobfuscator
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("Usage: deobfuscator.exe <input_file> [--ai] [--debug]");
                Console.WriteLine("  --ai     Enable AI renaming (requires local Ollama running)");
                Console.WriteLine("  --debug  Enable detailed logging to console and file");
                return;
            }

            string filePath = args[0];
            bool enableAi = args.Contains("--ai");
            bool debugMode = args.Contains("--debug");

            if (!File.Exists(filePath))
            {
                Console.WriteLine($"[!] Error: File not found: {filePath}");
                return;
            }

            // Настройка AI
            AiConfig aiConfig = new AiConfig();
            if (enableAi)
            {
                aiConfig.Enabled = true;
                Console.WriteLine("[*] Initializing AI Assistant...");
                
                // Проверка подключения до начала работы
                using (var tempAssistant = new AiAssistant(aiConfig))
                {
                    if (!tempAssistant.IsConnected)
                    {
                        Console.WriteLine("[!] Error: Cannot connect to local AI model (Ollama).");
                        Console.WriteLine("    Make sure Ollama is running: ollama serve");
                        Console.WriteLine("    Continuing without AI features...");
                        aiConfig.Enabled = false;
                    }
                    else
                    {
                        Console.WriteLine($"[+] Connected to AI model: {aiConfig.ModelName}");
                    }
                }
            }
            else
            {
                aiConfig.Enabled = false;
            }

            string outputPath = Path.Combine(
                Path.GetDirectoryName(filePath) ?? Directory.GetCurrentDirectory(),
                Path.GetFileNameWithoutExtension(filePath) + "_deob.exe"
            );

            try
            {
                Console.WriteLine($"[*] Loading: {filePath}");
                
                using (var deobfuscator = new UniversalDeobfuscator(filePath, aiConfig, debugMode))
                {
                    deobfuscator.Deobfuscate();
                    deobfuscator.Save(outputPath);
                }

                Console.WriteLine($"[+] Successfully saved to: {outputPath}");
                if (debugMode)
                {
                    Console.WriteLine($"[+] Debug log saved to: {Path.Combine(Path.GetDirectoryName(filePath) ?? "", "deob_log.txt")}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Fatal Error: {ex.Message}");
                if (debugMode)
                {
                    Console.WriteLine(ex.StackTrace);
                }
            }
        }
    }
}
