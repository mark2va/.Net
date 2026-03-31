namespace Deobfuscator;

public class AiConfig
{
    public bool Enabled { get; set; } = false;
    public string ApiUrl { get; set; } = "http://localhost:11434"; // Ollama default
    public string Model { get; set; } = "llama3"; // Default model
    public int TimeoutSeconds { get; set; } = 120; // Long timeout for code analysis

    public string ChatEndpoint => $"{ApiUrl.TrimEnd('/')}/api/chat";
    public string GenerateEndpoint => $"{ApiUrl.TrimEnd('/')}/api/generate";
}
