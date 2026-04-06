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
        public bool IsConnected { get; private set; }

        public AiAssistant(AiConfig config)
        {
            _config = config;
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = 
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds) };
            
            // Активная проверка подключения и модели
            IsConnected = VerifyConnectionAndModel();
        }

        private bool VerifyConnectionAndModel()
        {
            try
            {
                Console.WriteLine($"[*] Testing connection to {_config.ApiUrl}...");
                
                // Для Ollama можно проверить наличие модели через GET /api/tags, но проще попробовать мини-запрос
                // Или просто пингануть корень, если это стандартный сервис
                var baseUrl = _config.ApiUrl;
                if (baseUrl.EndsWith("/generate")) baseUrl = baseUrl.Substring(0, baseUrl.LastIndexOf('/'));
                
                var response = _httpClient.GetAsync(baseUrl).Result;
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[!] Server returned {(int)response.StatusCode}. Connection failed.");
                    return false;
                }

                // Пробный запрос к модели (очень короткий)
                var payload = new { model = _config.ModelName, prompt = "hi", stream = false };
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                
                var testResp = _httpClient.PostAsync(_config.ApiUrl, content).Result;
                if (testResp.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[+] Successfully connected to model '{_config.ModelName}'.");
                    return true;
                }
                else
                {
                    var err = testResp.Content.ReadAsStringAsync().Result;
                    Console.WriteLine($"[!] Model '{_config.ModelName}' error: {err}");
                    Console.WriteLine("[!] Ensure the model is pulled: ollama pull " + _config.ModelName);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Connection test failed: {ex.Message}");
                return false;
            }
        }

        public string GetSuggestedName(string currentName, string codeSnippet, string returnType)
        {
            if (!IsConnected) return currentName;

            try
            {
                var prompt = $@"Rename this C# method to a descriptive English name (PascalCase). Output ONLY the name.
Type: {returnType}
Code:
{codeSnippet}
Name:";

                var payload = new
                {
                    model = _config.ModelName,
                    prompt = prompt,
                    stream = false,
                    options = new { temperature = 0.1, num_predict = 15 }
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
                        var name = el.GetString()?.Trim().Replace("\"", "");
                        if (!string.IsNullOrEmpty(name)) return name.Split(' ')[0];
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AI] Error: {ex.Message}");
                IsConnected = false;
            }
            return currentName;
        }

        public void Dispose() => _httpClient.Dispose();
    }
}
