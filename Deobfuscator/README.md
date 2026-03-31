# Универсальный .NET Деобфускатор

Деобфускатор .NET сборок на основе библиотеки **dnlib** с поддержкой локальных AI-серверов для умного переименования методов и переменных.

## Возможности

### Базовая деобфускация:
- ✅ Упрощение константных условий (int, long, float, double)
- ✅ Распутывание цепочек goto
- ✅ Упрощение циклов while/do-while с константными условиями
- ✅ Упрощение switch с константными значениями
- ✅ Удаление мертвого кода (недостижимых инструкций)
- ✅ Очистка от лишних nop инструкций

### AI-функциональность (опционально):
- 🤖 Автоматическое переименование обфусцированных методов
- 🤖 Осмысленное переименование переменных
- 🤖 Генерация комментариев к методам
- 🤖 Полная локальность - данные не отправляются в облако

## Требования

- **Visual Studio 2017** или новее
- **.NET Framework 4.7.2** или новее
- **dnlib** (добавить через NuGet Package Manager)
- **Newtonsoft.Json** (добавить через NuGet Package Manager)
- **System.CommandLine** (добавить через NuGet Package Manager)

## Установка зависимостей в Visual Studio 2017

1. Откройте проект в Visual Studio 2017
2. Правой кнопкой мыши на проекте → **Manage NuGet Packages**
3. Найдите и установите:
   - `dnlib` (автор: 0xd4d)
   - `Newtonsoft.Json`
   - `System.CommandLine` (версия 2.0.0-beta1.21308.1 или новее)

Или через Package Manager Console:
```powershell
Install-Package dnlib
Install-Package Newtonsoft.Json
Install-Package System.CommandLine -Version 2.0.0-beta1.21308.1
```

## Сборка проекта

1. Откройте `Deobfuscator.csproj` в Visual Studio 2017
2. Выберите конфигурацию **Debug** или **Release**
3. Нажмите **Build → Build Solution** (Ctrl+Shift+B)
4.Executable файл появится в папке `bin\Debug\` или `bin\Release\`

## Использование

### Базовая деобфускация:
```bash
Deobfuscator.exe input.exe output.exe
```

### С AI-ассистентом (Ollama по умолчанию):
```bash
Deobfuscator.exe input.exe output.exe --ai
```

### С указанием сервера и модели:
```bash
# LM Studio на порту 1234
Deobfuscator.exe input.exe output.exe --ai --ai-url http://localhost:1234 --ai-model codellama

# Ollama с моделью deepseek-coder
Deobfuscator.exe input.exe output.exe --ai --ai-url http://localhost:11434 --ai-model deepseek-coder

# С увеличенным таймаутом для больших методов
Deobfuscator.exe input.exe output.exe --ai --ai-timeout 300
```

### Все параметры:
```
input               Входной обфусцированный файл (.exe/.dll)
output              Выходной очищенный файл
--ai                Включить AI-ассистента для переименования
--ai-url <url>      URL AI-сервера (по умолчанию: http://localhost:11434)
--ai-model <name>   Имя AI-модели (по умолчанию: llama3)
--ai-timeout <sec>  Таймаут запросов в секундах (по умолчанию: 120)
```

## Поддерживаемые AI-серверы

Проект совместим с любыми серверами, поддерживающими Ollama-style API:

| Сервер | URL по умолчанию | Примеры моделей |
|--------|-----------------|-----------------|
| Ollama | http://localhost:11434 | llama3, codellama, deepseek-coder, mistral |
| LM Studio | http://localhost:1234 | Любые GGUF модели |
| LocalAI | http://localhost:8080 | Различные модели |

## Примеры работы AI

### До деобфускации:
```csharp
class A {
    void a() {
        int V_0 = 1;
        if (V_0 == 2) { ... }
    }
}
```

### После деобфускации с AI:
```csharp
class Program {
    void DecryptConfig() {
        // Метод расшифровывает конфигурационные данные
        int encryptionKey = 1;
        if (encryptionKey == 2) { ... }
    }
}
```

## Алгоритм работы

1. **Загрузка сборки** - модуль загружается через dnlib
2. **Анализ констант** - отслеживаются присваивания констант локальным переменным
3. **Упрощение условий** - условия с известными значениями заменяются на true/false
4. **Распутывание goto** - цепочки переходов сокращаются до минимума
5. **Очистка циклов** - while(true)/while(false) обрабатываются
6. **Удаление мертвого кода** - недостижимые инструкции заменяются на nop
7. **AI-переименование** (опционально) - методы и переменные получают осмысленные имена
8. **Сохранение** - результат записывается в указанный файл

## Настройка AI-моделей

### Рекомендуемые модели для анализа кода:

| Модель | Размер | Качество | Скорость |
|--------|--------|----------|----------|
| codellama:7b | ~4GB | Хорошее | Быстро |
| deepseek-coder:6.7b | ~4GB | Отличное | Быстро |
| llama3:8b | ~5GB | Очень хорошее | Средне |
| mistral:7b | ~4GB | Хорошее | Быстро |

### Установка моделей в Ollama:
```bash
ollama pull codellama
ollama pull deepseek-coder
ollama pull llama3
```

## Структура проекта

```
Deobfuscator/
├── Deobfuscator.csproj    # Файл проекта VS 2017
├── App.config             # Конфигурация приложения
├── Program.cs             # Точка входа, CLI парсинг
├── AiConfig.cs            # Конфигурация AI-сервера
├── AiAssistant.cs         # Класс для работы с AI API
├── UniversalDeobfuscator.cs  # Основной класс деобфускации
└── README.md              # Документация
```

## Отладка в Visual Studio 2017

1. Откройте проект в Visual Studio
2. Перейдите в свойства проекта → Debug
3. В поле "Start external program" укажите путь к вашему тестовому .exe
4. В "Command line arguments" укажите параметры:
   ```
   obfuscated.exe cleaned.exe --ai --ai-url http://localhost:11434
   ```
5. Запустите отладку (F5)

## Известные ограничения

- Не поддерживает сложные виды обфускации (например, ConfuserEx с максимальными настройками)
- AI-функции требуют запущенного локального сервера
- Некоторые методы могут остаться с обфусцированными именами если AI не смог определить назначение
- Большие методы (>1000 инструкций) могут обрабатываться долго

## Лицензия

Проект использует библиотеку dnlib (MIT License). Сам код распространяется под лицензией MIT.

## Благодарности

- [dnlib](https://github.com/0xd4d/dnlib) - 0xd4d
- Ollama команда - за локальный AI сервер
- Сообщество reverse engineering
