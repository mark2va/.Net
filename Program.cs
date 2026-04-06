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
                Console.WriteLine("Usage: Deobfuscator.exe <input.exe> [--ai] [--server key] [--model key] [--debug]");
                Console.WriteLine("  --ai       Enable AI renaming");
                Console.WriteLine("  --server   Server key from ai_config.json (default: default)");
                Console.WriteLine("  --model    Model key from ai_config.json (default: default)");
                Console.WriteLine("  --debug    Enable detailed logging");
                return;
            }

            string inputPath = args[0];
            if (!File.Exists(inputPath))
            {
                Console.WriteLine($"[!] Error: File not found: {inputPath}");
                return;
            }

            bool enableAi = args.Contains("--ai");
            bool debugMode = args.Contains("--debug");
            
            string serverKey = "default";
            string modelKey = "default";

            // Парсинг аргументов
            if (args.Contains("--server"))
            {
                int idx = Array.IndexOf(args, "--server");
                if (idx + 1 < args.Length) serverKey = args[idx + 1];
            }
            if (args.Contains("--model"))
            {
                int idx = Array.IndexOf(args, "--model");
                if (idx + 1 < args.Length) modelKey = args[idx + 1];
            }

            AiConfig config = new AiConfig();
            if (enableAi)
            {
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ai_config.json");
                config = AiConfig.Load(configPath, serverKey, modelKey);
                config.Enabled = true;
                
                Console.WriteLine($"[*] AI Config Loaded:");
                Console.WriteLine($"    Server: {serverKey} ({config.ApiUrl})");
                Console.WriteLine($"    Model: {modelKey} ({config.ModelName})");
            }

            string outputPath = Path.Combine(
                Path.GetDirectoryName(inputPath) ?? Directory.GetCurrentDirectory(),
                Path.GetFileNameWithoutExtension(inputPath) + "_deob.exe"
            );

            try
            {
                using (var deob = new UniversalDeobfuscator(inputPath, config, debugMode))
                {
                    deob.Deobfuscate();
                    deob.Save(outputPath);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Fatal Error: {ex.Message}");
                if (debugMode) Console.WriteLine(ex.StackTrace);
            }
        }
    }
}
