# Universal Deobfuscator for .NET Assemblies

Универсальный деобфускатор для .NET сборок, основанный на библиотеке dnlib.

## Возможности

### 1. Упрощение константных условий
Автоматически упрощает конструкции вида:
```csharp
int num = 1;
if (num == 2) { ... }  // Условие всегда false - удаляется
if (num == 1) { ... }  // Условие всегда true - упрощается
```

Поддерживаемые типы:
- `int` (Int32)
- `long` (Int64)
- `float` (Single)
- `double` (Double)
- `byte`, `sbyte`, `short`, `ushort`, `uint`, `ulong`

### 2. Распутывание потоков управления (Control Flow Unraveling)

#### Goto Chains
```csharp
// До:
goto label1;
label1: goto label2;
label2: return;

// После:
return;
```

#### While/Do-While с константными условиями
```csharp
// До:
while (true) { ... }  // Бесконечный цикл
while (false) { ... } // Мертвый код

// После:
{ ... }  // Тело цикла без проверки
// или просто удаляется
```

#### Switch с константными значениями
```csharp
// До:
int x = 1;
switch (x) {
    case 0: ... break;
    case 1: ... break;
    case 2: ... break;
}

// После:
// Прямой переход к case 1
```

### 3. Удаление мертвого кода
Автоматическое обнаружение и удаление недостижимого кода.

### 4. Упрощение ветвлений
Оптимизация инструкций ветвления.

### 5. AI-ассистированное переименование (экспериментально)
Интеграция с локальными LLM-серверами (Ollama, LM Studio и др.) для:
- Автоматического переименования методов на основе их функциональности
- Осмысленного переименования локальных переменных
- Добавления комментариев к сложным методам

Поддерживаемые серверы:
- **Ollama** (http://localhost:11434)
- **LM Studio** (http://localhost:1234)
- Любые другие совместимые API серверы

Рекомендуемые модели:
- `llama3` - современная универсальная модель (рекомендуется по умолчанию)
- `codellama` - специализированная модель для кода
- `mistral` - быстрая и эффективная модель
- `deepseek-coder` - отличная модель для анализа кода
- `qwen2.5-coder` - мощная модель для работы с кодом

## Сборка

### Требования
- .NET 8.0 SDK или новее
- Git

### Инструкция

1. Клонируйте репозиторий:
```bash
git clone https://github.com/0xd4d/dnlib.git
```

2. Соберите проект:
```bash
cd Deobfuscator
dotnet build
```

3. Запустите:
```bash
dotnet run -- <input.exe> <output.exe>
```

## Использование

### Базовое использование (без AI)
```bash
# Через dotnet
dotnet run -- obfuscated.exe cleaned.exe

# Или напрямую
Deobfuscator.exe obfuscated.exe cleaned.exe
```

### С использованием AI-ассистента

```bash
# С настройками по умолчанию (Ollama + llama3)
dotnet run -- obfuscated.exe cleaned.exe --ai

# С указанием URL сервера (например, LM Studio)
dotnet run -- obfuscated.exe cleaned.exe --ai --ai-url http://localhost:1234

# С выбором модели
dotnet run -- obfuscated.exe cleaned.exe --ai --ai-model codellama

# С изменением таймаута (для больших методов)
dotnet run -- obfuscated.exe cleaned.exe --ai --ai-timeout 180

# Полная конфигурация
dotnet run -- obfuscated.exe cleaned.exe --ai --ai-url http://localhost:11434 --ai-model deepseek-coder --ai-timeout 120
```

#### Примеры команд для разных серверов:

**Ollama:**
```bash
# Запуск с моделью llama3
dotnet run -- input.exe output.exe --ai --ai-model llama3

# Запуск с моделью codellama
dotnet run -- input.exe output.exe --ai --ai-model codellama
```

**LM Studio:**
```bash
# Запуск с локальным сервером LM Studio
dotnet run -- input.exe output.exe --ai --ai-url http://localhost:1234 --ai-model your-model-name
```

**Другие совместимые серверы:**
```bash
# Любой OpenAI-совместимый API
dotnet run -- input.exe output.exe --ai --ai-url http://your-server:port --ai-model model-name --ai-timeout 300
```

### Примеры использования AI

#### Ollama
1. Установите Ollama: https://ollama.ai
2. Загрузите модель: `ollama pull llama3` или `ollama pull codellama`
3. Запустите: `dotnet run -- input.exe output.exe --ai --ai-model llama3`

#### LM Studio
1. Установите LM Studio: https://lmstudio.ai
2. Загрузите любую модель для кода (например, Codellama, DeepSeek-Coder)
3. Запустите локальный сервер в LM Studio (обычно на порту 1234)
4. Запустите деобфускатор: `dotnet run -- input.exe output.exe --ai --ai-url http://localhost:1234 --ai-model your-model-name`

#### Настройка таймаута
Для больших методов или медленных моделей увеличьте таймаут:
```bash
dotnet run -- input.exe output.exe --ai --ai-timeout 300
```

## Архитектура

### Основные компоненты

1. **AiConfig** - конфигурация AI-сервера (URL, модель, таймаут)
2. **AiAssistant** - класс для взаимодействия с локальными AI-серверами
3. **UniversalDeobfuscator** - главный класс, координирующий все этапы деобфускации
4. **SimplifyConstantConditions** - упрощение условий с константами
5. **UnravelControlFlow** - распутывание потоков управления
6. **RemoveDeadCode** - удаление мертвого кода
7. **SimplifyBranches** - оптимизация ветвлений
8. **ApplyAiRenamingAsync** - AI-ассистированное переименование

### Алгоритм работы

1. **Первый проход**: Сбор информации о константных значениях локальных переменных
2. **Второй проход**: Упрощение сравнений с известными константами
3. **Третий проход**: Распутывание цепочек goto
4. **Четвертый проход**: Упрощение циклов с константными условиями
5. **Пятый проход**: Упрощение switch statements
6. **Шестой проход**: Удаление недостижимого кода
7. **AI проход** (опционально): Анализ и переименование методов/переменных
8. **Финальный проход**: Очистка и оптимизация

## Примеры трансформаций

### Пример 1: Константное условие
```il
// До
ldloc.0        // num = 1
ldc.i4.2       // 2
ceq            // num == 2 (false)
brtrue.s LABEL // никогда не выполнится

// После
ldc.i4.0       // false
brtrue.s LABEL // никогда не выполнится
```

### Пример 2: Goto chain
```il
// До
IL_0000: br.s IL_0002
IL_0002: br.s IL_0004
IL_0004: ret

// После
IL_0000: ret
```

### Пример 3: While(true)
```il
// До
IL_0000: ldc.i4.1
IL_0001: brfalse.s IL_0010  // никогда не выполнится
IL_0003: ... loop body ...
IL_0008: br.s IL_0000

// После
IL_0000: ... loop body ...
IL_0005: br.s IL_0000
```

### Пример 4: AI переименование
```
[AI] Analyzing method: a.b
[AI] Renaming method 'a.b' -> 'DecryptString'
[AI] Renaming variable V_0 -> 'encryptedData'
[AI] Renaming variable V_1 -> 'decryptionKey'
[AI] Comment: This method decrypts a string using XOR encryption
```

## Производительность AI

- Время анализа одного метода: 2-10 секунд (зависит от модели и размера метода)
- Рекомендуется использовать для небольших и средних сборок
- Для больших сборок можно анализировать только ключевые методы

## Советы по использованию AI

1. **Выбор модели**: Используйте специализированные модели для кода (codellama, deepseek-coder, qwen2.5-coder)
2. **Размер контекста**: Убедитесь, что модель поддерживает достаточный размер контекста
3. **Таймаут**: Для больших методов увеличьте таймаут через `--ai-timeout`
4. **Локальность**: AI работает полностью локально - ваши данные не отправляются в облако
5. **Отключение**: Если AI не нужен, просто не используйте флаг `--ai`
6. **Гибкая настройка**: Меняйте URL сервера, модель и таймаут в зависимости от ваших нужд

## Параметры командной строки AI

| Параметр | Описание | По умолчанию | Пример |
|----------|----------|--------------|--------|
| `--ai` | Включить AI-ассистента | выключено | `--ai` |
| `--ai-url <url>` | URL AI-сервера | `http://localhost:11434` | `--ai-url http://localhost:1234` |
| `--ai-model <name>` | Имя модели | `llama3` | `--ai-model codellama` |
| `--ai-timeout <secs>` | Таймаут в секундах | `120` | `--ai-timeout 300` |

## Лицензия

Этот проект использует dnlib под лицензией MIT.
