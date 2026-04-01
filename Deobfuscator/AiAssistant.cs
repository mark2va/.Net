using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Deobfuscator
{
    public class AiConfig
    {
        public bool Enabled { get; set; }
        public string Endpoint { get; set; } = "http://192.168.31.130:11434/api/generate"; // Пример для Ollama
        public string Model { get; set; } = "codellama"; // Или ваша модель

        public AiConfig() { }
    }

    public class AiAssistant : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly AiConfig _config;

        public AiAssistant(AiConfig config)
        {
            _config = config;
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
        }

        public string? GetSuggestedName(string currentName, string ilSnippet, string returnType)
        {
            try
            {
                var prompt = $@"Analyze this obfuscated C# method and suggest a meaningful name.
Return ONLY the name, no explanation.
Return Type: {returnType}
Current Name: {currentName}
IL Code:
{ilSnippet}

Suggested name:";

                var payload = new
                {
                    model = _config.Model,
                    prompt = prompt,
                    stream = false,
                    options = new { temperature = 0.1 }
                };

                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = _httpClient.PostAsync(_config.Endpoint, content).Result;
                
                if (response.IsSuccessStatusCode)
                {
                    var responseString = response.Content.ReadAsStringAsync().Result;
                    using var doc = JsonDocument.Parse(responseString);
                    if (doc.RootElement.TryGetProperty("response", out var el))
                    {
                        var name = el.GetString()?.Trim();
                        if (!string.IsNullOrEmpty(name))
                            return name;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AI] Request failed: {ex.Message}");
            }
            return null;
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }
    }
}
