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
                Console.WriteLine("Examples:");
                Console.WriteLine("  Deobfuscator.exe sample.exe");
                Console.WriteLine("  Deobfuscator.exe sample.exe --ai --server remote --model codellama --debug");
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
            string serverKey = GetArgValue(args, "--server", "default");
            string modelKey = GetArgValue(args, "--model", "default");

            AiConfig config = new AiConfig();
            if (enableAi)
            {
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ai_config.json");
                config = AiConfig.Load(configPath, serverKey, modelKey);
                config.Enabled = true;
                
                Console.WriteLine($"[*] AI Config: Server='{serverKey}', Model='{modelKey}'");
                Console.WriteLine($"[*] Target URL: {config.ApiUrl}");
            }

            string dir = Path.GetDirectoryName(inputPath) ?? Directory.GetCurrentDirectory();
            string outputPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(inputPath) + "_deob.exe");

            try
            {
                using (var deob = new UniversalDeobfuscator(inputPath, config, debugMode))
                {
                    deob.Deobfuscate();
                    deob.Save(outputPath);
                }
                Console.WriteLine($"[+] Output saved to: {outputPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Fatal Error: {ex.Message}");
                if (debugMode) Console.WriteLine(ex.StackTrace);
            }
        }

        static string GetArgValue(string[] args, string key, string defaultValue)
        {
            int idx = Array.IndexOf(args, key);
            if (idx >= 0 && idx + 1 < args.Length) return args[idx + 1];
            return defaultValue;
        }
    }
}
