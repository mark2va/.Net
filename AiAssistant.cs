using System;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;

namespace Deobfuscator
{
    public class AiServerConfig
    {
        public string Url { get; set; } = "http://localhost:11434";
        public string ApiPath { get; set; } = "/api/generate";
        public string Type { get; set; } = "Ollama"; // Ollama или OpenAI
        public int TimeoutSeconds { get; set; } = 60;
        
        public string FullUrl => Url.TrimEnd('/') + ApiPath;
    }

    public class AiConfigFile
    {
        public string DefaultServer { get; set; } = "local";
        public Dictionary<string, AiServerConfig> Servers { get; set; } = new();
        public Dictionary<string, string> Models { get; set; } = new();
    }

    public class AiConfig
    {
        public bool Enabled { get; set; }
        public string ModelName { get; set; } = "llama3";
        public string ApiUrl { get; set; } = "http://localhost:11434/api/generate";
        public int TimeoutSeconds { get; set; } = 60;
        public string ServerType { get; set; } = "Ollama";

        public static AiConfig Load(string? serverKey = null, string? modelKey = null)
        {
            string configPath = Path.Combine(AppContext.BaseDirectory, "ai_config.json");
            
            // Если файла нет, создаем дефолтный
            if (!File.Exists(configPath))
            {
                CreateDefaultConfig(configPath);
            }

            try
            {
                var json = File.ReadAllText(configPath);
                var fileConfig = JsonSerializer.Deserialize<AiConfigFile>(json);
                
                if (fileConfig == null) return CreateDefaultManualConfig();

                // Выбираем сервер
                string sKey = serverKey ?? fileConfig.DefaultServer;
                if (!fileConfig.Servers.ContainsKey(sKey))
                {
                    Console.WriteLine($"[!] Server '{sKey}' not found in config. Using default.");
                    sKey = fileConfig.DefaultServer;
                }

                var server = fileConfig.Servers[sKey];

                // Выбираем модель
                string mKey = modelKey ?? "default";
                string modelName = "llama3";
                
                if (fileConfig.Models.ContainsKey(mKey))
                {
                    modelName = fileConfig.Models[mKey];
                }
                else if (fileConfig.Models.ContainsKey(modelKey ?? ""))
                {
                     modelName = fileConfig.Models[modelKey!];
                }
                else 
                {
                    // Если ключ не найден, возможно пользователь ввел имя модели напрямую
                    modelName = modelKey ?? fileConfig.Models["default"];
                }

                return new AiConfig
                {
                    Enabled = true,
                    ModelName = modelName,
                    ApiUrl = server.FullUrl,
                    TimeoutSeconds = server.TimeoutSeconds,
                    ServerType = server.Type
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Error reading ai_config.json: {ex.Message}. Using defaults.");
                return CreateDefaultManualConfig();
            }
        }

        private static void CreateDefaultConfig(string path)
        {
            var defaultConfig = new AiConfigFile
            {
                DefaultServer = "local",
                Servers = new Dictionary<string, AiServerConfig>
                {
                    { "local", new AiServerConfig { Url = "http://localhost:11434", Type = "Ollama" } },
                    { "remote", new AiServerConfig { Url = "http://192.168.31.130:11434", Type = "Ollama" } }
                },
                Models = new Dictionary<string, string>
                {
                    { "default", "llama3" },
                    { "code", "codellama" }
                }
            };

            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, JsonSerializer.Serialize(defaultConfig, options));
            Console.WriteLine("[*] Created default ai_config.json");
        }

        private static AiConfig CreateDefaultManualConfig()
        {
            return new AiConfig
            {
                Enabled = true,
                ModelName = "llama3",
                ApiUrl = "http://localhost:11434/api/generate",
                TimeoutSeconds = 60,
                ServerType = "Ollama"
            };
        }
    }
}
