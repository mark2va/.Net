using System;
using System.Collections.Generic;

namespace Deobfuscator
{
    /// <summary>
    /// Конфигурация для подключения к локальному AI-серверу
    /// </summary>
    public class AiConfig
    {
        /// <summary>
        /// URL API сервера (по умолчанию Ollama)
        /// Примеры:
        /// - http://localhost:11434 (Ollama)
        /// - http://localhost:1234 (LM Studio)
        /// </summary>
        public string ApiUrl { get; set; } = "http://localhost:11434";

        /// <summary>
        /// Имя модели для использования
        /// Примеры: llama3, codellama, deepseek-coder, mistral
        /// </summary>
        public string Model { get; set; } = "llama3";

        /// <summary>
        /// Таймаут запроса в секундах
        /// </summary>
        public int TimeoutSeconds { get; set; } = 120;

        /// <summary>
        /// Включить ли AI-функциональность
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Создать конфигурацию по умолчанию
        /// </summary>
        public AiConfig() { }

        /// <summary>
        /// Создать конфигурацию с указанными параметрами
        /// </summary>
        public AiConfig(string apiUrl, string model, int timeoutSeconds = 120)
        {
            ApiUrl = apiUrl;
            Model = model;
            TimeoutSeconds = timeoutSeconds;
            Enabled = true;
        }

        /// <summary>
        /// Получить базовый URL для API (без завершающего слэша)
        /// </summary>
        public string GetBaseUrl()
        {
            return ApiUrl.TrimEnd('/');
        }

        /// <summary>
        /// Получить URL для генерации (Ollama-style API)
        /// </summary>
        public string GetGenerateUrl()
        {
            return $"{GetBaseUrl()}/api/generate";
        }

        /// <summary>
        /// Переопределение ToString для удобного отображения
        /// </summary>
        public override string ToString()
        {
            return $"AI Config: URL={ApiUrl}, Model={Model}, Timeout={TimeoutSeconds}s, Enabled={Enabled}";
        }
    }
}
