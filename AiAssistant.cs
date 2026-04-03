using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Deobfuscator
{
    public class AiConfig
    {
        public bool Enabled { get; set; }
        public string ModelName { get; set; } = "llama3"; // Или любой другой, установленный у вас
        public string ApiUrl { get; set; } = "http://localhost:11434/api/generate";
        public int TimeoutSeconds { get; set; } = 30;
    }

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
                // Игнорируем ошибки SSL если вдруг используется https с самоподписанным сертификатом
                ServerCertificateCustomValidationCallback = 
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds)
            };
            
            // Проверка подключения при инициализации
            _isConnected = CheckConnectionAsync().Result;
        }

        public bool IsConnected => _isConnected;

        private async Task<bool> CheckConnectionAsync()
        {
            try
            {
                // Простой запрос к корню API для проверки доступности сервиса
                var rootUrl = _config.ApiUrl.Replace("/api/generate", "");
                var response = await _httpClient.GetAsync(rootUrl);
                return response.IsSuccessStatusCode;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public string GetSuggestedName(string currentName, string codeSnippet, string? returnType)
        {
            if (!_isConnected)
            {
                Console.WriteLine("[AI] Warning: Not connected to local model. Skipping rename.");
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

                var payload = new
                {
                    model = _config.ModelName,
                    prompt = prompt,
                    stream = false,
                    options = new
                    {
                        temperature = 0.3, // Низкая температура для более точных имен
                        num_predict = 50   // Ограничиваем длину ответа
                    }
                };

                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = _httpClient.PostAsync(_config.ApiUrl, content).Result;

                if (response.IsSuccessStatusCode)
                {
                    var responseString = response.Content.ReadAsStringAsync().Result;
                    using var doc = JsonDocument.Parse(responseString);
                    
                    if (doc.RootElement.TryGetProperty("response", out var element))
                    {
                        var suggested = element.GetString()?.Trim();
                        if (!string.IsNullOrEmpty(suggested))
                        {
                            // Очистка от лишних символов, если модель всё же добавила текст
                            return CleanName(suggested);
                        }
                    }
                }
                
                Console.WriteLine($"[AI] API returned error or empty response: {response.StatusCode}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AI] Error during request: {ex.Message}");
                _isConnected = false; // Помечаем как отключенный при ошибке
            }

            return currentName;
        }

        private string CleanName(string rawName)
        {
            // Удаляем кавычки, точки с запятой и прочие артефакты
            var clean = rawName.Replace("\"", "").Replace(";", "").Replace("`", "");
            // Берем первое слово, если модель выдала фразу
            var parts = clean.Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[0] : clean;
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }
    }
}
