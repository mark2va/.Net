using System;
using System.IO;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Deobfuscator
{
    public class AiAssistant : IDisposable
    {
        private readonly AiConfig _config;
        private HttpWebRequest? _currentRequest;

        public bool Enabled => _config.Enabled;

        public AiAssistant(AiConfig config)
        {
            _config = config;
        }

        public string? GetSuggestedName(string currentName, string methodBodyIl, string returnType)
        {
            if (!_config.Enabled) return null;

            try
            {
                // Ограничим входные данные, чтобы не превысить лимиты токенов
                string ilSnippet = methodBodyIl.Length > 1000 ? methodBodyIl.Substring(0, 1000) + "..." : methodBodyIl;

                string prompt = $"You are a reverse engineering expert. Analyze this C# IL code snippet and suggest a concise, meaningful camelCase name for the method. Return ONLY the name, no explanation, no quotes.\n" +
                                $"Return type: {returnType}\n" +
                                $"Current obfuscated name: {currentName}\n" +
                                $"IL Code:\n{ilSnippet}";

                // Подготовка тела запроса для Ollama / LM Studio
                var requestBody = new
                {
                    model = _config.Model,
                    prompt = prompt,
                    stream = false,
                    options = new { temperature = 0.1, num_predict = 20, stop = new[] { "\n", ".", " " } }
                };

                string json = JsonConvert.SerializeObject(requestBody);
                byte[] data = Encoding.UTF8.GetBytes(json);

                // Пробуем основной URL
                string endpoint = $"{_config.ApiUrl}/api/generate";
                
                // Если используется OpenAI-compatible API (LM Studio часто использует /v1/chat/completions)
                // Но для простоты оставим /api/generate как стандарт Ollama. 
                // Если 404, попробуем альтернативу в catch блоке или проверим ответ.

                _currentRequest = (HttpWebRequest)WebRequest.Create(endpoint);
                _currentRequest.Method = "POST";
                _currentRequest.ContentType = "application/json";
                _currentRequest.Timeout = _config.TimeoutSeconds * 1000;

                using (var stream = _currentRequest.GetRequestStream())
                {
                    stream.Write(data, 0, data.Length);
                }

                HttpWebResponse response;
                try
                {
                    response = (HttpWebResponse)_currentRequest.GetResponse();
                }
                catch (WebException we)
                {
                    if (we.Response is HttpWebResponse errResp)
                    {
                        Console.WriteLine($"[AI] Server returned status: {(int)errResp.StatusCode} {errResp.StatusCode}");
                        using (var reader = new StreamReader(errResp.GetResponseStream()))
                        {
                            Console.WriteLine($"[AI] Error details: {reader.ReadToEnd()}");
                        }
                    }
                    throw;
                }

                using (response)
                using (var reader = new StreamReader(response.GetResponseStream()))
                {
                    string result = reader.ReadToEnd();
                    
                    // Парсинг ответа Ollama
                    var jsonResponse = JObject.Parse(result);
                    string generatedText = jsonResponse["response"]?.ToString().Trim() ?? "";
                    
                    // Очистка от лишних символов
                    generatedText = generatedText.Replace("\"", "").Replace("`", "").Trim();
                    
                    if (string.IsNullOrEmpty(generatedText))
                    {
                        Console.WriteLine("[AI] Empty response received.");
                        return null;
                    }

                    return generatedText;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AI Error]: {ex.Message}");
                if (ex.Message.Contains("404"))
                {
                    Console.WriteLine("[AI Hint]: Ensure your server supports '/api/generate' (Ollama default). For LM Studio, check settings or use compatible mode.");
                }
                return null;
            }
            finally
            {
                _currentRequest = null;
            }
        }

        public void Dispose()
        {
            _currentRequest?.Abort();
        }
    }
}
