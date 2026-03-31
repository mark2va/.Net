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
- `codellama` - специализированная модель для кода
- `llama2` - универсальная модель
- `mistral` - быстрая и эффективная модель
- `deepseek-coder` - отличная модель для анализа кода

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
# С настройками по умолчанию (Ollama + codellama)
dotnet run -- obfuscated.exe cleaned.exe --ai

# С указанием URL сервера (например, LM Studio)
dotnet run -- obfuscated.exe cleaned.exe --ai --ai-url http://localhost:1234

# С выбором модели
dotnet run -- obfuscated.exe cleaned.exe --ai --ai-model llama2

# Полная конфигурация
dotnet run -- obfuscated.exe cleaned.exe --ai --ai-url http://localhost:11434 --ai-model deepseek-coder
```

### Примеры использования AI

#### Ollama
1. Установите Ollama: https://ollama.ai
2. Загрузите модель: `ollama pull codellama`
3. Запустите: `dotnet run -- input.exe output.exe --ai`

#### LM Studio
1. Установите LM Studio: https://lmstudio.ai
2. Загрузите любую модель для кода
3. Запустите локальный сервер в LM Studio
4. Запустите деобфускатор: `dotnet run -- input.exe output.exe --ai --ai-url http://localhost:1234 --ai-model your-model-name`

## Архитектура

### Основные компоненты

1. **UniversalDeobfuscator** - главный класс, координирующий все этапы деобфускации
2. **AiAssistant** - класс для взаимодействия с локальными AI-серверами
3. **SimplifyConstantConditions** - упрощение условий с константами
4. **UnravelControlFlow** - распутывание потоков управления
5. **RemoveDeadCode** - удаление мертвого кода
6. **SimplifyBranches** - оптимизация ветвлений
7. **ApplyAiRenamingAsync** - AI-ассистированное переименование

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

1. **Выбор модели**: Используйте специализированные модели для кода (codellama, deepseek-coder)
2. **Размер контекста**: Убедитесь, что модель поддерживает достаточный размер контекста
3. **Локальность**: AI работает полностью локально - ваши данные не отправляются в облако
4. **Отключение**: Если AI не нужен, просто не используйте флаг `--ai`

## Лицензия

Этот проект использует dnlib под лицензией MIT.
