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
        public bool IsModelLoaded { get; private set; }

        public AiAssistant(AiConfig config)
        {
            _config = config;
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds) };
            
            // Проверка при инициализации
            IsConnected = CheckConnection().Result;
            if (IsConnected)
            {
                IsModelLoaded = CheckModel().Result;
            }
        }

        private async System.Threading.Tasks.Task<bool> CheckConnection()
        {
            try
            {
                var baseUrl = _config.ApiUrl.Replace("/api/generate", "").Replace("/v1/completions", "");
                var resp = await _httpClient.GetAsync(baseUrl);
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        private async System.Threading.Tasks.Task<bool> CheckModel()
        {
            try
            {
                // Попытка сделать мини-запрос к модели
                var payload = new { model = _config.ModelName, prompt = ".", stream = false, options = new { num_predict = 1 } };
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                
                // Для Ollama
                var resp = await _httpClient.PostAsync(_config.ApiUrl, content);
                if (resp.IsSuccessStatusCode) return true;

                // Если ошибка 404, возможно модель не найдена
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    Console.WriteLine($"[AI] Model '{_config.ModelName}' not found on server.");
                    return false;
                }
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AI] Model check failed: {ex.Message}");
                return false;
            }
        }

        public string GetSuggestedName(string context, string codeSnippet)
        {
            if (!IsConnected || !IsModelLoaded) return "";

            try
            {
                var prompt = $"Name this C# method based on logic and constants used. Return ONLY one PascalCase name.\nContext: {context}\nCode:\n{codeSnippet}";
                var payload = new { model = _config.ModelName, prompt = prompt, stream = false, options = new { temperature = 0.1, num_predict = 30 } };
                
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var resp = _httpClient.PostAsync(_config.ApiUrl, content).Result;

                if (resp.IsSuccessStatusCode)
                {
                    var respStr = resp.Content.ReadAsStringAsync().Result;
                    using var doc = JsonDocument.Parse(respStr);
                    if (doc.RootElement.TryGetProperty("response", out var el))
                    {
                        var name = el.GetString()?.Trim().Replace("\"", "");
                        return string.IsNullOrEmpty(name) ? "" : name.Split(' ')[0];
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AI] Rename error: {ex.Message}");
                IsConnected = false;
            }
            return "";
        }

        public void Dispose() => _httpClient.Dispose();
    }
}
