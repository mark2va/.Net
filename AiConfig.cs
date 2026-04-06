using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Deobfuscator
{
    public class AiConfig
    {
        public bool Enabled { get; set; } = false;
        public string ServerKey { get; set; } = "default";
        public string ModelKey { get; set; } = "default";
        
        // Данные, загруженные из JSON
        public string ApiUrl { get; set; } = "http://localhost:11434/api/generate";
        public string ModelName { get; set; } = "llama3";
        public int TimeoutSeconds { get; set; } = 60;
        public string ApiType { get; set; } = "ollama"; // ollama или openai

        public static AiConfig Load(string configPath, string serverKey, string modelKey)
        {
            var config = new AiConfig
            {
                Enabled = true,
                ServerKey = serverKey,
                ModelKey = modelKey
            };

            if (!File.Exists(configPath))
            {
                CreateDefaultConfig(configPath);
            }

            try
            {
                var json = File.ReadAllText(configPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Поиск сервера
                if (root.TryGetProperty("servers", out var serversElem))
                {
                    foreach (var server in serversElem.EnumerateObject())
                    {
                        if (server.Name == serverKey)
                        {
                            if (server.Value.TryGetProperty("url", out var url)) config.ApiUrl = url.GetString();
                            if (server.Value.TryGetProperty("type", out var type)) config.ApiType = type.GetString();
                            if (server.Value.TryGetProperty("timeout", out var timeout)) config.TimeoutSeconds = timeout.GetInt32();
                            
                            // Поиск модели внутри сервера или глобально
                            if (server.Value.TryGetProperty("models", out var modelsElem))
                            {
                                foreach (var model in modelsElem.EnumerateObject())
                                {
                                    if (model.Name == modelKey)
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

                // Глобальный поиск модели, если не найдена в сервере
                if (config.ModelName == "llama3" && root.TryGetProperty("models", out var globalModels))
                {
                     foreach (var model in globalModels.EnumerateObject())
                    {
                        if (model.Name == modelKey)
                        {
                            if (model.Value.TryGetProperty("name", out var mName)) config.ModelName = mName.GetString();
                            break;
                        }
                    }
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
        ""codellama"": { ""name"": ""codellama"" },
        ""deepseek"": { ""name"": ""deepseek-coder"" }
      }
    }
  },
  ""models"": {
    ""fast"": { ""name"": ""tinyllama"" },
    ""smart"": { ""name"": ""mistral"" }
  }
}";
            File.WriteAllText(path, defaultJson);
            Console.WriteLine($"[*] Created default config file: {path}");
        }
    }
}
