namespace Deobfuscator
{
    public class AiConfig
    {
        public bool Enabled { get; set; } = false;
        public string ApiUrl { get; set; } = "http://localhost:11434";
        public string Model { get; set; } = "codellama";
        public int TimeoutSeconds { get; set; } = 120;

        public AiConfig() { }

        public AiConfig(bool enabled, string url, string model, int timeout)
        {
            Enabled = enabled;
            ApiUrl = url;
            Model = model;
            TimeoutSeconds = timeout;
        }
    }
}
