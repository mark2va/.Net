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
            Console.WriteLine("Usage: Deobfuscator <input.exe> <output.exe> [--ai] [--ai-url http://localhost:11434] [--ai-model codellama]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --ai              Enable AI-assisted renaming using local LLM server");
            Console.WriteLine("  --ai-url <url>    URL of the AI server (default: http://localhost:11434 for Ollama)");
            Console.WriteLine("  --ai-model <name> Model name to use (default: codellama)");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  Deobfuscator input.exe output.exe");
            Console.WriteLine("  Deobfuscator input.exe output.exe --ai");
            Console.WriteLine("  Deobfuscator input.exe output.exe --ai --ai-url http://localhost:11434 --ai-model llama2");
            return;
        }

        string inputPath = args[0];
        string outputPath = args[1];
        
        bool useAi = false;
        string aiUrl = "http://localhost:11434";
        string aiModel = "codellama";
        
        // Parse command line arguments
        for (int i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--ai":
                    useAi = true;
                    break;
                case "--ai-url":
                    if (i + 1 < args.Length)
                    {
                        aiUrl = args[++i];
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
                        aiModel = args[++i];
                    }
                    else
                    {
                        Console.WriteLine("Error: --ai-model requires a model name argument");
                        return;
                    }
                    break;
            }
        }

        Console.WriteLine($"Loading: {inputPath}");
        var module = ModuleDefMD.Load(inputPath);
        
        if (useAi)
        {
            Console.WriteLine($"[AI] Using AI assistant at {aiUrl} with model: {aiModel}");
        }
        
        var deobfuscator = new UniversalDeobfuscator(module, useAi, aiUrl, aiModel);
        await deobfuscator.ProcessAsync();
        
        Console.WriteLine($"Saving: {outputPath}");
        module.Write(outputPath);
        
        Console.WriteLine("Done!");
    }
}
