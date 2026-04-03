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
        private bool _useOpenAiFormat; // Флаг для переключения формата

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

            // Проверяем подключение и определяем формат API
            _isConnected = InitializeConnectionAsync().Result;
        }

        public bool IsConnected => _isConnected;

        private async System.Threading.Tasks.Task<bool> InitializeConnectionAsync()
        {
            try
            {
                // Пробуем стандартный путь Ollama
                var rootUrl = _config.ApiUrl.Replace("/api/generate", "").Replace("/v1/completions", "").TrimEnd('/');
                
                // Сначала пробуем получить информацию о сервере (Ollama style)
                var response = await _httpClient.GetAsync(rootUrl);
                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[AI] Connected to server at {rootUrl}");
                    
                    // Пробуем сделать тестовый запрос к генерации, чтобы проверить путь
                    // Если /api/generate не работает, попробуем /v1/completions
                    if (!await TestGenerationEndpoint(rootUrl + "/api/generate"))
                    {
                        Console.WriteLine("[AI] /api/generate not found (404). Trying OpenAI compatible endpoint /v1/completions...");
                        if (await TestGenerationEndpoint(rootUrl + "/v1/completions"))
                        {
                            _useOpenAiFormat = true;
                            _config.ApiUrl = rootUrl + "/v1/completions";
                            Console.WriteLine($"[AI] Switched to OpenAI format: {_config.ApiUrl}");
                            return true;
                        }
                        return false;
                    }
                    
                    _useOpenAiFormat = false;
                    _config.ApiUrl = rootUrl + "/api/generate";
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AI] Connection error: {ex.Message}");
                return false;
            }
        }

        private async System.Threading.Tasks.Task<bool> TestGenerationEndpoint(string url)
        {
            try
            {
                var payload = _useOpenAiFormat 
                    ? new { model = _config.ModelName, prompt = "hi", max_tokens = 5 }
                    : new { model = _config.ModelName, prompt = "hi", stream = false };
                
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                
                var response = await _httpClient.PostAsync(url, content);
                
                // Считаем успехом 200 OK. 404 означает неверный путь.
                return response.StatusCode == System.Net.HttpStatusCode.OK;
            }
            catch
            {
                return false;
            }
        }

        public string GetSuggestedName(string currentName, string codeSnippet, string? returnType)
        {
            if (!_isConnected)
            {
                return currentName;
            }

            try
            {
                var prompt = $@"Analyze this obfuscated C# method and suggest a clear, descriptive name in English (PascalCase). 
Do not output any explanation, just the name.
Return type: {returnType ?? "void"}
Current name: {currentName}

Code snippet:
{codeSnippet}

Suggested name:";

                string json;
                if (_useOpenAiFormat)
                {
                    // Формат OpenAI / vLLM / LocalAI
                    var payload = new
                    {
                        model = _config.ModelName,
                        prompt = prompt,
                        max_tokens = 50,
                        temperature = 0.3,
                        stream = false
                    };
                    json = JsonSerializer.Serialize(payload);
                }
                else
                {
                    // Формат Ollama
                    var payload = new
                    {
                        model = _config.ModelName,
                        prompt = prompt,
                        stream = false,
                        options = new
                        {
                            temperature = 0.3,
                            num_predict = 50
                        }
                    };
                    json = JsonSerializer.Serialize(payload);
                }

                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = _httpClient.PostAsync(_config.ApiUrl, content).Result;

                if (response.IsSuccessStatusCode)
                {
                    var responseString = response.Content.ReadAsStringAsync().Result;
                    
                    string? resultText = null;
                    
                    if (_useOpenAiFormat)
                    {
                        // Парсинг ответа OpenAI: {"choices": [{"text": "..."}]}
                        using var doc = JsonDocument.Parse(responseString);
                        if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                        {
                            if (choices[0].TryGetProperty("text", out var textElem))
                            {
                                resultText = textElem.GetString();
                            }
                        }
                    }
                    else
                    {
                        // Парсинг ответа Ollama: {"response": "..."}
                        using var doc = JsonDocument.Parse(responseString);
                        if (doc.RootElement.TryGetProperty("response", out var element))
                        {
                            resultText = element.GetString();
                        }
                    }

                    if (!string.IsNullOrEmpty(resultText))
                    {
                        return CleanName(resultText);
                    }
                }
                else
                {
                    var errContent = response.Content.ReadAsStringAsync().Result;
                    Console.WriteLine($"[AI] API Error {(int)response.StatusCode}: {errContent}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AI] Request error: {ex.Message}");
                _isConnected = false;
            }

            return currentName;
        }

        private string CleanName(string rawName)
        {
            var clean = rawName.Replace("\"", "").Replace(";", "").Replace("`", "").Trim();
            var parts = clean.Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[0] : clean;
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }
    }
}
