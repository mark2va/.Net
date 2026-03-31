using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Deobfuscator
{
    /// <summary>
    /// Класс для взаимодействия с локальными ИИ-серверами (Ollama, LM Studio и др.)
    /// </summary>
    public class AiAssistant
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        private readonly string _model;

        public AiAssistant(string baseUrl = "http://localhost:11434", string model = "codellama")
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _model = model;
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromMinutes(2); // Длительный таймаут для анализа кода
        }

        /// <summary>
        /// Анализирует метод и предлагает новые имена для переменных и методов
        /// </summary>
        public async Task<AiAnalysisResult?> AnalyzeMethodAsync(string methodName, string ilCode)
        {
            var prompt = $@"You are an expert .NET reverse engineer. 
Analyze the following IL code for method '{methodName}'.
Your task is to:
1. Identify the purpose of the method.
2. Suggest meaningful names for local variables based on their usage.
3. Suggest a better name for the method itself if 'method' or obfuscated name is used.

Return ONLY a valid JSON object with this structure:
{{
    ""methodName"": ""SuggestedMethodName"",
    ""variables"": {{
        ""V_0"": ""suggestedName1"",
        ""V_1"": ""suggestedName2""
    }},
    ""comment"": ""Brief description of what the method does""
}}

IL Code:
```il
{ilCode}
```

If you cannot determine meaningful names, return an empty JSON object {{}}. Do not include markdown formatting like ```json.";

            try
            {
                var requestBody = new
                {
                    model = _model,
                    prompt = prompt,
                    stream = false,
                    options = new
                    {
                        temperature = 0.2, // Низкая температура для более детерминированного ответа
                        num_predict = 500
                    }
                };

                var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
                
                // Ollama API endpoint
                var response = await _httpClient.PostAsync($"{_baseUrl}/api/generate", content);
                
                if (response.IsSuccessStatusCode)
                {
                    var jsonResponse = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(jsonResponse);
                    
                    if (doc.RootElement.TryGetProperty("response", out var responseElement))
                    {
                        var aiText = responseElement.GetString();
                        return ParseAiResponse(aiText);
                    }
                }
                else
                {
                    Console.WriteLine($"[AI] Error: {response.StatusCode} - {await response.Content.ReadAsStringAsync()}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AI] Exception during analysis: {ex.Message}");
            }

            return null;
        }

        private AiAnalysisResult? ParseAiResponse(string text)
        {
            try
            {
                // Очистка от возможных маркдаун-оберток
                text = text.Replace("```json", "").Replace("```", "").Trim();
                
                // Поиск начала и конца JSON объекта, если ответ содержит лишний текст
                int start = text.IndexOf('{');
                int end = text.LastIndexOf('}');
                
                if (start != -1 && end != -1 && end > start)
                {
                    text = text.Substring(start, end - start + 1);
                }

                var result = JsonSerializer.Deserialize<AiAnalysisResult>(text);
                return result;
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"[AI] Failed to parse JSON response: {ex.Message}");
                Console.WriteLine($"[AI] Raw response snippet: {text.Substring(0, Math.Min(100, text.Length))}...");
                return null;
            }
        }
    }

    public class AiAnalysisResult
    {
        public string? methodName { get; set; }
        public Dictionary<string, string>? variables { get; set; }
        public string? comment { get; set; }
    }
}
