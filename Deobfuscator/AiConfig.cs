using System;

namespace Deobfuscator
{
    /// <summary>
    /// Конфигурация для подключения к локальному AI серверу
    /// </summary>
    public class AiConfig
    {
        /// <summary>
        /// URL API сервера (по умолчанию Ollama)
        /// </summary>
        public string ApiUrl { get; set; } = "http://localhost:11434";

        /// <summary>
        /// Имя модели для использования
        /// </summary>
        public string Model { get; set; } = "llama3";

        /// <summary>
        /// Таймаут запроса в секундах
        /// </summary>
        public int TimeoutSeconds { get; set; } = 120;

        /// <summary>
        /// Включен ли AI ассистент
        /// </summary>
        public bool Enabled { get; set; } = false;

        public AiConfig() { }

        public AiConfig(string apiUrl, string model, int timeoutSeconds, bool enabled)
        {
            ApiUrl = apiUrl;
            Model = model;
            TimeoutSeconds = timeoutSeconds;
            Enabled = enabled;
        }
    }
}
