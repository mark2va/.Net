using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.MD;

namespace Deobfuscator;

class Program
{
    static async Task Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: Deobfuscator <input.exe> <output.exe> [options]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --ai                  Enable AI-assisted renaming using local LLM server");
            Console.WriteLine("  --ai-url <url>        URL of the AI server (default: http://localhost:11434 for Ollama)");
            Console.WriteLine("  --ai-model <name>     Model name to use (default: llama3)");
            Console.WriteLine("  --ai-timeout <secs>   Timeout in seconds for AI requests (default: 120)");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  Deobfuscator input.exe output.exe");
            Console.WriteLine("  Deobfuscator input.exe output.exe --ai");
            Console.WriteLine("  Deobfuscator input.exe output.exe --ai --ai-url http://localhost:11434 --ai-model llama3");
            Console.WriteLine("  Deobfuscator input.exe output.exe --ai --ai-url http://localhost:1234 --ai-model codellama --ai-timeout 180");
            Console.WriteLine();
            Console.WriteLine("Supported AI Servers:");
            Console.WriteLine("  - Ollama (http://localhost:11434)");
            Console.WriteLine("  - LM Studio (http://localhost:1234)");
            Console.WriteLine("  - Any OpenAI-compatible API server");
            return;
        }

        string inputPath = args[0];
        string outputPath = args[1];
        
        var aiConfig = new AiConfig();
        
        // Parse command line arguments
        for (int i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--ai":
                    aiConfig.Enabled = true;
                    break;
                case "--ai-url":
                    if (i + 1 < args.Length)
                    {
                        aiConfig.ApiUrl = args[++i];
                    }
                    else
                    {
                        Console.WriteLine("Error: --ai-url requires a URL argument");
                        return;
                    }
                    break;
                case "--ai-model":
                    if (i + 1 < args.Length)
                    {
                        aiConfig.Model = args[++i];
                    }
                    else
                    {
                        Console.WriteLine("Error: --ai-model requires a model name argument");
                        return;
                    }
                    break;
                case "--ai-timeout":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int timeout))
                    {
                        aiConfig.TimeoutSeconds = timeout;
                    }
                    else
                    {
                        Console.WriteLine("Error: --ai-timeout requires a numeric argument");
                        return;
                    }
                    break;
            }
        }

        Console.WriteLine($"Loading: {inputPath}");
        var module = ModuleDefMD.Load(inputPath);
        
        if (aiConfig.Enabled)
        {
            Console.WriteLine($"[AI] Using AI assistant at {aiConfig.ApiUrl} with model: {aiConfig.Model} (timeout: {aiConfig.TimeoutSeconds}s)");
        }
        
        var deobfuscator = new UniversalDeobfuscator(module, aiConfig.Enabled ? aiConfig : null);
        await deobfuscator.ProcessAsync();
        
        Console.WriteLine($"Saving: {outputPath}");
        module.Write(outputPath);
        
        Console.WriteLine("Done!");
    }
}
