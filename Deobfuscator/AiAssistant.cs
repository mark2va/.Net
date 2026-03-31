using System;
using System.IO;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Deobfuscator
{
    /// <summary>
    /// Класс для взаимодействия с локальными AI серверами (Ollama, LM Studio и др.)
    /// </summary>
    public class AiAssistant
    {
        private readonly AiConfig _config;

        public AiAssistant(AiConfig config)
        {
            _config = config;
        }

        /// <summary>
        /// Генерирует осмысленное имя для метода на основе его кода
        /// </summary>
        public string GenerateMethodName(string methodCode, string currentName)
        {
            if (!_config.Enabled)
                return currentName;

            try
            {
                var prompt = $"Analyze this C# decompiled method and suggest a meaningful name (single word or camelCase). Return ONLY the name, nothing else.\n\nMethod code:\n{methodCode}\n\nCurrent obfuscated name: {currentName}\n\nSuggested name:";

                var response = SendRequest(prompt);
                if (!string.IsNullOrEmpty(response))
                {
                    // Очищаем ответ от лишних символов
                    var cleanName = response.Trim().Replace("\"", "").Replace("\n", "").Replace("\r", "");
                    if (!string.IsNullOrEmpty(cleanName) && cleanName.Length <= 50)
                        return cleanName;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AI] Ошибка генерации имени метода: {ex.Message}");
            }

            return currentName;
        }

        /// <summary>
        /// Генерирует осмысленное имя для переменной
        /// </summary>
        public string GenerateVariableName(string variableType, string context, string currentName)
        {
            if (!_config.Enabled)
                return currentName;

            try
            {
                var prompt = $"Suggest a meaningful variable name for type '{variableType}' in this context. Return ONLY the name (camelCase), nothing else.\n\nContext:\n{context}\n\nCurrent name: {currentName}\n\nSuggested name:";

                var response = SendRequest(prompt);
                if (!string.IsNullOrEmpty(response))
                {
                    var cleanName = response.Trim().Replace("\"", "").Replace("\n", "").Replace("\r", "");
                    if (!string.IsNullOrEmpty(cleanName) && cleanName.Length <= 30)
                        return cleanName;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AI] Ошибка генерации имени переменной: {ex.Message}");
            }

            return currentName;
        }

        /// <summary>
        /// Добавляет комментарий к методу
        /// </summary>
        public string GenerateMethodComment(string methodCode)
        {
            if (!_config.Enabled)
                return string.Empty;

            try
            {
                var prompt = $"Write a brief one-line comment explaining what this C# method does. Return ONLY the comment text without // markers.\n\nMethod code:\n{methodCode}\n\nComment:";

                var response = SendRequest(prompt);
                if (!string.IsNullOrEmpty(response))
                {
                    return response.Trim().Replace("\"", "").Replace("\n", " ");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AI] Ошибка генерации комментария: {ex.Message}");
            }

            return string.Empty;
        }

        /// <summary>
        /// Отправляет запрос к AI серверу
        /// </summary>
        private string SendRequest(string prompt)
        {
            var requestUrl = $"{_config.ApiUrl}/api/generate";
            
            var requestData = new
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

            var jsonRequest = JsonConvert.SerializeObject(requestData);
            var jsonBytes = Encoding.UTF8.GetBytes(jsonRequest);

            var request = (HttpWebRequest)WebRequest.Create(requestUrl);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Timeout = _config.TimeoutSeconds * 1000;
            request.ReadWriteTimeout = _config.TimeoutSeconds * 1000;

            using (var requestStream = request.GetRequestStream())
            {
                requestStream.Write(jsonBytes, 0, jsonBytes.Length);
            }

            using (var response = (HttpWebResponse)request.GetResponse())
            {
                using (var reader = new StreamReader(response.GetResponseStream()))
                {
                    var jsonResponse = reader.ReadToEnd();
                    var jsonObject = JObject.Parse(jsonResponse);
                    
                    if (jsonObject.ContainsKey("response"))
                    {
                        return jsonObject["response"].ToString();
                    }
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// Проверяет доступность AI сервера
        /// </summary>
        public bool IsServerAvailable()
        {
            try
            {
                var testUrl = $"{_config.ApiUrl}/api/tags";
                var request = (HttpWebRequest)WebRequest.Create(testUrl);
                request.Method = "GET";
                request.Timeout = 5000;

                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    return response.StatusCode == HttpStatusCode.OK;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
