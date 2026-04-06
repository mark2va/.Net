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
                return;
            }

            string input = args[0];
            if (!File.Exists(input)) { Console.WriteLine("File not found"); return; }

            bool useAi = args.Contains("--ai");
            bool debug = args.Contains("--debug");
            
            string srv = "default";
            string mdl = "default";
            
            if (args.Contains("--server")) { var i = Array.IndexOf(args, "--server"); if (i+1 < args.Length) srv = args[i+1]; }
            if (args.Contains("--model")) { var i = Array.IndexOf(args, "--model"); if (i+1 < args.Length) mdl = args[i+1]; }

            AiConfig cfg = new AiConfig();
            if (useAi)
            {
                string cfgPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ai_config.json");
                cfg = AiConfig.Load(cfgPath, srv, mdl);
                cfg.Enabled = true;
            }

            string outPath = Path.Combine(Path.GetDirectoryName(input) ?? "", Path.GetFileNameWithoutExtension(input) + "_clean.exe");

            try
            {
                using (var deob = new UniversalDeobfuscator(input, cfg, debug))
                {
                    deob.Process();
                    deob.Save(outPath);
                }
                Console.WriteLine("[+] Done! Output: " + outPath);
                if (debug) Console.WriteLine("[+] Log saved to deob_trace.log");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Error: {ex.Message}");
                if (debug) Console.WriteLine(ex.StackTrace);
            }
        }
    }
}
