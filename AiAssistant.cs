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
        private readonly bool _debugMode;
        private bool _isConnected;
        private bool _isModelAvailable;

        public AiAssistant(AiConfig config, bool debugMode = false)
        {
            _config = config;
            _debugMode = debugMode;
            
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = 
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds)
            };
            
            if (_debugMode)
            {
                Console.WriteLine("[AI] Testing connection...");
            }
            
            _isConnected = CheckConnectionAsync().Result;
            
            if (_debugMode)
            {
                if (_isConnected)
                    Console.WriteLine($"[AI] Connection successful to {_config.ApiUrl}");
                else
                    Console.WriteLine($"[AI] Connection FAILED to {_config.ApiUrl}");
            }
            
            if (_isConnected)
            {
                if (_debugMode)
                {
                    Console.WriteLine($"[AI] Checking model '{_config.ModelName}' availability...");
                }
                _isModelAvailable = CheckModelAsync().Result;
                
                if (_debugMode)
                {
                    if (_isModelAvailable)
                        Console.WriteLine($"[AI] Model '{_config.ModelName}' is available.");
                    else
                        Console.WriteLine($"[AI] Model '{_config.ModelName}' NOT found or API type unknown.");
                }
            }
        }

        public bool IsConnected => _isConnected;
        public bool IsModelAvailable() => _isModelAvailable;

        private async System.Threading.Tasks.Task<bool> CheckConnectionAsync()
        {
            try
            {
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

        private async System.Threading.Tasks.Task<bool> CheckModelAsync()
        {
            try
            {
                // Для Ollama: GET /api/tags возвращает список моделей
                var tagsUrl = _config.ApiUrl.Replace("/api/generate", "/api/tags");
                if (_config.ApiType.ToLower() == "ollama")
                {
                    var response = await _httpClient.GetAsync(tagsUrl);
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("models", out var modelsElem))
                        {
                            foreach (var model in modelsElem.EnumerateArray())
                            {
                                if (model.TryGetProperty("name", out var nameElem))
                                {
                                    if (nameElem.GetString() == _config.ModelName)
                                        return true;
                                }
                            }
                        }
                    }
                }
                else
                {
                    // Для OpenAI совместимых можно попробовать запрос к /v1/models
                    // Упрощенно считаем доступной, если сервер жив (для кастомных серверов сложно проверить без спецификации)
                    return true; 
                }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public string GetSuggestedName(string currentName, string codeSnippet, string? returnType)
        {
            if (!_isConnected || !_isModelAvailable) return currentName;

            try
            {
                var prompt = $@"Rename this C# member to a clear, descriptive English name (PascalCase). Output ONLY the name.
Type: {returnType ?? "void"}
Current: {currentName}
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
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AI] Error: {ex.Message}");
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
