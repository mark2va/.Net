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

                _currentRequest = (HttpWebRequest)WebRequest.Create($"{_config.ApiUrl}/api/generate");
                _currentRequest.Method = "POST";
                _currentRequest.ContentType = "application/json";
                _currentRequest.Timeout = _config.TimeoutSeconds * 1000;

                using (var stream = _currentRequest.GetRequestStream())
                {
                    stream.Write(data, 0, data.Length);
                }

                using (var response = (HttpWebResponse)_currentRequest.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                {
                    string result = reader.ReadToEnd();
                    var jsonResponse = JObject.Parse(result);
                    return jsonResponse["response"]?.ToString().Trim();
                }
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
