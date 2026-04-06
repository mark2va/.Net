using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Deobfuscator
{
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
                ServerCertificateCustomValidationCallback = 
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds)
            };
            
            // Формируем правильный URL для API
            string baseUrl = _config.ApiUrl.TrimEnd('/');
            if (!baseUrl.Contains("/api/"))
            {
                _endpoint = $"{baseUrl}/api/generate";
            }
            else
            {
                _endpoint = baseUrl;
            }
            
            Console.WriteLine($"[AI] Initialized with endpoint: {_endpoint}");
            _isConnected = CheckConnectionAsync().Result;
            
            if (_isConnected)
            {
                Console.WriteLine($"[AI] Successfully connected to Ollama server.");
            }
            else
            {
                Console.WriteLine($"[AI] Warning: Cannot connect to Ollama server at {_endpoint}");
                Console.WriteLine("[AI] Make sure Ollama is running: ollama serve");
            }
        }
        
        private readonly string _endpoint;

        public bool IsConnected => _isConnected;

        private async System.Threading.Tasks.Task<bool> CheckConnectionAsync()
        {
            try
            {
                var rootUrl = _endpoint.Replace("/api/generate", "");
                var response = await _httpClient.GetAsync(rootUrl);
                return response.IsSuccessStatusCode;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public string? GetSuggestedName(string currentName, string ilSnippet, string returnType)
        {
            if (!_isConnected)
            {
                Console.WriteLine("[AI] Not connected to server, skipping rename request.");
                return null;
            }

            try
            {
                var prompt = $@"Analyze this obfuscated C# method and suggest a meaningful name.
Return ONLY the name, no explanation.
Return Type: {returnType}
Current Name: {currentName}
IL Code:
{ilSnippet}

Suggested name:";

                var payload = new
                {
                    model = _config.Model,
                    prompt = prompt,
                    stream = false,
                    options = new { temperature = 0.1 }
                };

                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                Console.WriteLine($"[AI] Sending request to {_endpoint}...");
                var response = _httpClient.PostAsync(_endpoint, content).Result;
                
                if (response.IsSuccessStatusCode)
                {
                    var responseString = response.Content.ReadAsStringAsync().Result;
                    using var doc = JsonDocument.Parse(responseString);
                    if (doc.RootElement.TryGetProperty("response", out var el))
                    {
                        var name = el.GetString()?.Trim();
                        if (!string.IsNullOrEmpty(name))
                        {
                            Console.WriteLine($"[AI] Received suggestion: {name}");
                            return name;
                        }
                    }
                    Console.WriteLine("[AI] Empty response from server.");
                }
                else
                {
                    Console.WriteLine($"[AI] HTTP Error: {response.StatusCode} - {response.ReasonPhrase}");
                }
            }
            catch (AggregateException ae) when (ae.InnerException is HttpRequestException hre)
            {
                Console.WriteLine($"[AI] Connection failed: {hre.Message}");
                Console.WriteLine("[AI] Make sure Ollama server is running at the specified URL.");
                _isConnected = false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AI] Request failed: {ex.Message}");
                _isConnected = false;
            }
            return null;
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }
    }
}
