using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Deobfuscator
{
    public class AiAssistant : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly AiConfig _config;
        private bool _isConnected;

        public AiAssistant(AiConfig config)
        {
            _config = config;
            
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = 
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds)
            };
            
            // Проверка подключения
            _isConnected = CheckConnectionAsync().Result;
        }

        public bool IsConnected => _isConnected;

        private async System.Threading.Tasks.Task<bool> CheckConnectionAsync()
        {
            try
            {
                // Для Ollama проверяем корень или специфичный эндпоинт
                var baseUrl = _config.ApiUrl;
                if (baseUrl.EndsWith("/generate")) 
                    baseUrl = baseUrl.Replace("/api/generate", "");
                else if (baseUrl.EndsWith("/completions"))
                    baseUrl = baseUrl.Replace("/v1/completions", "");

                var response = await _httpClient.GetAsync(baseUrl);
                return response.IsSuccessStatusCode;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public string GetSuggestedName(string currentName, string codeSnippet, string? returnType)
        {
            if (!_isConnected) return currentName;

            try
            {
                var prompt = $@"Analyze this obfuscated C# method and suggest a clear, descriptive name in English (PascalCase). 
Output ONLY the name, no explanations.
Return type: {returnType ?? "void"}
Current name: {currentName}

Code:
{codeSnippet}

Name:";

                if (_config.ApiType.ToLower() == "ollama")
                {
                    var payload = new
                    {
                        model = _config.ModelName,
                        prompt = prompt,
                        stream = false,
                        options = new { temperature = 0.2, num_predict = 20 }
                    };
                    var json = JsonSerializer.Serialize(payload);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");

                    var response = _httpClient.PostAsync(_config.ApiUrl, content).Result;
                    if (response.IsSuccessStatusCode)
                    {
                        var respStr = response.Content.ReadAsStringAsync().Result;
                        using var doc = JsonDocument.Parse(respStr);
                        if (doc.RootElement.TryGetProperty("response", out var el))
                        {
                            return CleanName(el.GetString());
                        }
                    }
                }
                else
                {
                    // OpenAI format
                    var payload = new
                    {
                        model = _config.ModelName,
                        prompt = prompt, // Для старых моделей completion
                        max_tokens = 20
                    };
                    // Примечание: для чат-моделей формат другой, но для кодовых часто используют completions
                    var json = JsonSerializer.Serialize(payload);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");
                    
                    var response = _httpClient.PostAsync(_config.ApiUrl, content).Result;
                    if (response.IsSuccessStatusCode)
                    {
                        var respStr = response.Content.ReadAsStringAsync().Result;
                        using var doc = JsonDocument.Parse(respStr);
                        if (doc.RootElement.TryGetProperty("choices", out var choices))
                        {
                            var first = choices[0];
                            if (first.TryGetProperty("text", out var text))
                                return CleanName(text.GetString());
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AI] Error: {ex.Message}");
                _isConnected = false;
            }

            return currentName;
        }

        private string CleanName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            var clean = raw.Replace("\"", "").Replace("\n", "").Replace("\r", "").Trim();
            var parts = clean.Split(' ');
            return parts.Length > 0 ? parts[0] : clean;
        }

        public void Dispose() => _httpClient.Dispose();
    }
}
