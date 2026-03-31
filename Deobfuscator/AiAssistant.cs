using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Deobfuscator
{
    /// <summary>
    /// Класс для взаимодействия с локальными AI-серверами (Ollama, LM Studio и др.)
    /// </summary>
    public class AiAssistant
    {
        private readonly AiConfig _config;
        private readonly HttpClient _httpClient;

        /// <summary>
        /// Создать AI-ассистента с указанной конфигурацией
        /// </summary>
        public AiAssistant(AiConfig config)
        {
            _config = config;
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds);
            _httpClient.DefaultRequestHeaders.Add("Content-Type", "application/json");
        }

        /// <summary>
        /// Переименовать метод на основе его кода
        /// </summary>
        public async Task<string> SuggestMethodName(string methodCode, string currentName)
        {
            if (!_config.Enabled)
                return currentName;

            try
            {
                var prompt = $@"Analyze this C# method and suggest a meaningful name (single word or short phrase, camelCase). 
Current obfuscated name: {currentName}
Method code:
{methodCode}

Respond with ONLY the suggested name, nothing else.";

                var response = await SendPromptAsync(prompt);
                
                if (!string.IsNullOrWhiteSpace(response))
                {
                    // Очистить ответ от лишних символов
                    var cleanName = response.Trim().Trim('"', '\'', '`', '.');
                    if (!string.IsNullOrWhiteSpace(cleanName) && cleanName.Length <= 50)
                        return cleanName;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AI] Ошибка при переименовании метода: {ex.Message}");
            }

            return currentName;
        }

        /// <summary>
        /// Переименовать переменную на основе контекста
        /// </summary>
        public async Task<string> SuggestVariableName(string context, string currentName, string variableType)
        {
            if (!_config.Enabled)
                return currentName;

            try
            {
                var prompt = $@"Analyze this C# code context and suggest a meaningful variable name (camelCase).
Variable type: {variableType}
Current obfuscated name: {currentName}
Context:
{context}

Respond with ONLY the suggested name, nothing else.";

                var response = await SendPromptAsync(prompt);
                
                if (!string.IsNullOrWhiteSpace(response))
                {
                    var cleanName = response.Trim().Trim('"', '\'', '`', '.');
                    if (!string.IsNullOrWhiteSpace(cleanName) && cleanName.Length <= 30)
                        return cleanName;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AI] Ошибка при переименовании переменной: {ex.Message}");
            }

            return currentName;
        }

        /// <summary>
        /// Добавить комментарий к методу
        /// </summary>
        public async Task<string> GenerateMethodComment(string methodCode)
        {
            if (!_config.Enabled)
                return string.Empty;

            try
            {
                var prompt = $@"Analyze this C# method and write a brief XML documentation comment describing what it does.
Method code:
{methodCode}

Respond with ONLY the XML comment (/// lines), nothing else.";

                var response = await SendPromptAsync(prompt);
                
                if (!string.IsNullOrWhiteSpace(response))
                {
                    return response.Trim();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AI] Ошибка при генерации комментария: {ex.Message}");
            }

            return string.Empty;
        }

        /// <summary>
        /// Отправить промпт к AI-серверу
        /// </summary>
        private async Task<string> SendPromptAsync(string prompt)
        {
            var requestBody = new
            {
                model = _config.Model,
                prompt = prompt,
                stream = false,
                options = new
                {
                    temperature = 0.3,
                    top_p = 0.9
                }
            };

            var json = JsonConvert.SerializeObject(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(_config.GetGenerateUrl(), content);
            
            if (response.IsSuccessStatusCode)
            {
                var responseJson = await response.Content.ReadAsStringAsync();
                var jsonObject = JObject.Parse(responseJson);
                
                // Ollama format
                if (jsonObject["response"] != null)
                {
                    return jsonObject["response"].ToString();
                }
                
                // Generic format
                if (jsonObject["choices"] != null && jsonObject["choices"][0]["text"] != null)
                {
                    return jsonObject["choices"][0]["text"].ToString();
                }
            }
            else
            {
                var errorText = await response.Content.ReadAsStringAsync();
                throw new Exception($"HTTP {response.StatusCode}: {errorText}");
            }

            return string.Empty;
        }

        /// <summary>
        /// Проверить доступность AI-сервера
        /// </summary>
        public async Task<bool> CheckConnectionAsync()
        {
            try
            {
                // Попытка получить список моделей (Ollama API)
                var modelsUrl = $"{_config.GetBaseUrl()}/api/tags";
                var response = await _httpClient.GetAsync(modelsUrl);
                
                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[AI] Успешное подключение к {_config.ApiUrl}");
                    return true;
                }
                
                // Если не удалось получить список моделей, пробуем простой запрос
                var testResponse = await SendPromptAsync("Say 'OK'");
                if (!string.IsNullOrWhiteSpace(testResponse))
                {
                    Console.WriteLine($"[AI] Успешное подключение к {_config.ApiUrl}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AI] Ошибка подключения к {_config.ApiUrl}: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Освободить ресурсы
        /// </summary>
        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}
