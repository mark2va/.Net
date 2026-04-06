using System;
using System.IO;
using System.Text.Json;

namespace Deobfuscator
{
    public class AiConfig
    {
        public bool Enabled { get; set; } = false;
        public string ServerKey { get; set; } = "default";
        public string ModelKey { get; set; } = "default";
        
        // Заполняется при загрузке
        public string ApiUrl { get; set; } = "http://localhost:11434/api/generate";
        public string ModelName { get; set; } = "llama3";
        public int TimeoutSeconds { get; set; } = 60;
        public string ApiType { get; set; } = "ollama";

        public static AiConfig Load(string configPath, string serverKey, string modelKey)
        {
            var config = new AiConfig { Enabled = true, ServerKey = serverKey, ModelKey = modelKey };

            if (!File.Exists(configPath))
            {
                CreateDefaultConfig(configPath);
            }

            try
            {
                var json = File.ReadAllText(configPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                bool serverFound = false;
                if (root.TryGetProperty("servers", out var serversElem))
                {
                    foreach (var server in serversElem.EnumerateObject())
                    {
                        if (server.Name.Equals(serverKey, StringComparison.OrdinalIgnoreCase))
                        {
                            serverFound = true;
                            if (server.Value.TryGetProperty("url", out var url)) config.ApiUrl = url.GetString();
                            if (server.Value.TryGetProperty("type", out var type)) config.ApiType = type.GetString();
                            if (server.Value.TryGetProperty("timeout", out var timeout)) config.TimeoutSeconds = timeout.GetInt32();

                            if (server.Value.TryGetProperty("models", out var modelsElem))
                            {
                                foreach (var model in modelsElem.EnumerateObject())
                                {
                                    if (model.Name.Equals(modelKey, StringComparison.OrdinalIgnoreCase))
                                    {
                                        if (model.Value.TryGetProperty("name", out var mName)) config.ModelName = mName.GetString();
                                        break;
                                    }
                                }
                            }
                            break;
                        }
                    }
                }

                if (!serverFound)
                {
                    Console.WriteLine($"[!] Server key '{serverKey}' not found. Using defaults.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Error reading config: {ex.Message}. Using defaults.");
            }

            return config;
        }

        private static void CreateDefaultConfig(string path)
        {
            var defaultJson = @"{
  ""servers"": {
    ""default"": {
      ""url"": ""http://localhost:11434/api/generate"",
      ""type"": ""ollama"",
      ""timeout"": 60,
      ""models"": {
        ""default"": { ""name"": ""llama3"" },
        ""codellama"": { ""name"": ""codellama"" }
      }
    },
    ""remote"": {
      ""url"": ""http://192.168.31.130:11434/api/generate"",
      ""type"": ""ollama"",
      ""timeout"": 120,
      ""models"": {
        ""codellama"": { ""name"": ""codellama"" }
      }
    }
  }
}";
            File.WriteAllText(path, defaultJson);
            Console.WriteLine($"[*] Created default config: {path}");
        }
    }
}
