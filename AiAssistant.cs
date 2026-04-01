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
            // Убедимся, что URL заканчивается правильно
            if (!_config.ApiUrl.EndsWith("/")) 
            {
                // Для Ollama базовый URL обычно http://localhost:11434, путь добавим в запросе
            }
        }

        public string? GetSuggestedName(string currentName, string methodBodyIl, string returnType)
        {
            if (!_config.Enabled) return null;

            try
            {
                string prompt = $"Analyze this C# method IL code and suggest a meaningful name in camelCase. Return ONLY the name, no explanation.\n" +
                                $"Return type: {returnType}\n" +
                                $"Current name: {currentName}\n" +
                                $"IL Code snippet:\n{methodBodyIl}";

                var requestBody = new
                {
                    model = _config.Model,
                    prompt = prompt,
                    stream = false,
                    options = new { temperature = 0.1, num_predict = 50 }
                };

                string json = JsonConvert.SerializeObject(requestBody);
                byte[] data = Encoding.UTF8.GetBytes(json);

                // Формируем правильный URL
                string endpoint = _config.ApiUrl.TrimEnd('/');
                if (!endpoint.Contains("/api/"))
                {
                    endpoint += "/api/generate";
                }

                _currentRequest = (HttpWebRequest)WebRequest.Create(endpoint);
                _currentRequest.Method = "POST";
                _currentRequest.ContentType = "application/json";
                _currentRequest.Timeout = _config.TimeoutSeconds * 1000;

                using (var stream = _currentRequest.GetRequestStream())
                {
                    stream.Write(data, 0, data.Length);
                }

                using (var response = (HttpWebResponse)_currentRequest.GetResponse())
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        Console.WriteLine($"[AI] Server returned status: {response.StatusCode}");
                        return null;
                    }

                    using (var reader = new StreamReader(response.GetResponseStream()))
                    {
                        string result = reader.ReadToEnd();
                        var jsonResponse = JObject.Parse(result);
                        
                        // Ollama возвращает поле "response"
                        if (jsonResponse.TryGetValue("response", out var token))
                        {
                            return token.ToString().Trim();
                        }
                        return null;
                    }
                }
            }
            catch (WebException we)
            {
                if (we.Response is HttpWebResponse resp)
                {
                     Console.WriteLine($"[AI HTTP Error]: {resp.StatusCode} ({resp.StatusDescription})");
                }
                else
                {
                    Console.WriteLine($"[AI Network Error]: {we.Message}");
                }
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AI Error]: {ex.Message}");
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
