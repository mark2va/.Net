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
                // Формируем промпт
                string prompt = $"Analyze this C# method IL code and suggest a short, meaningful camelCase name. Return ONLY the name.\n" +
                                $"Context: {returnType}\n" +
                                $"IL Snippet:\n{methodBodyIl}";

                // Подготовка JSON для Ollama / совместимых API
                var requestBody = new
                {
                    model = _config.Model,
                    prompt = prompt,
                    stream = false,
                    options = new { temperature = 0.1, num_predict = 20 }
                };

                string json = JsonConvert.SerializeObject(requestBody);
                byte[] data = Encoding.UTF8.GetBytes(json);

                // Создаем запрос
                var request = (HttpWebRequest)WebRequest.Create($"{_config.ApiUrl}/api/generate");
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Timeout = _config.TimeoutSeconds * 1000;

                using (var stream = request.GetRequestStream())
                {
                    stream.Write(data, 0, data.Length);
                }

                HttpWebResponse response;
                try
                {
                    response = (HttpWebResponse)request.GetResponse();
                }
                catch (WebException we)
                {
                    if (we.Response != null)
                    {
                        using (var errReader = new StreamReader(we.Response.GetResponseStream()))
                        {
                            Console.WriteLine($"[AI HTTP Error]: {we.Status} - {errReader.ReadToEnd()}");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"[AI Network Error]: {we.Message}");
                    }
                    return null;
                }

                using (response)
                using (var reader = new StreamReader(response.GetResponseStream()))
                {
                    string result = reader.ReadToEnd();
                    var jsonResponse = JObject.Parse(result);
                    
                    if (jsonResponse["response"] != null)
                    {
                        return jsonResponse["response"].ToString().Trim();
                    }
                    return null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AI Exception]: {ex.Message}");
                return null;
            }
        }

        public void Dispose()
        {
            // Ресурсы освобождаются автоматически в using блоках
        }
    }
}
