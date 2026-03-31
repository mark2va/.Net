# Универсальный деобфускатор .NET сборок

Деобфускатор для .NET сборок на основе библиотеки dnlib с поддержкой AI-ассистента для переименования методов и переменных.

## Возможности

### Базовая деобфускация
- **Упрощение константных условий** - обработка сравнений вида `if (num == 2)` где `num = 1`
  - Поддерживаемые типы: `int`, `long`, `float`, `double`
  
- **Распутывание потоков управления**
  - Цепочки goto: `goto L1; L1: goto L2; L2: ret;` → `ret;`
  - While/do-while с константными условиями: `while(true)`, `while(false)`
  - Switch с константными значениями
  
- **Удаление мертвого кода** - анализ достижимости инструкций
  
- **Упрощение ветвлений** - удаление лишних nop инструкций

### AI-функциональность (опционально)
- Автоматическое переименование обфусцированных методов
- Осмысленное переименование локальных переменных
- Генерация комментариев к методам
- Полная локальность - данные не отправляются в облако

## Требования

- Visual Studio 2017 или новее
- .NET Framework 4.7.2
- NuGet пакеты:
  - dnlib (версия 4.4.0)
  - Newtonsoft.Json (версия 13.0.3)

## Установка

### Вариант 1: Через NuGet Package Manager Console
```
Install-Package dnlib -Version 4.4.0
Install-Package Newtonsoft.Json -Version 13.0.3
```

### Вариант 2: Через NuGet UI
1. Откройте проект в Visual Studio
2. Правой кнопкой на проекте → Manage NuGet Packages
3. Найдите и установите dnlib и Newtonsoft.Json

## Сборка

1. Откройте `Deobfuscator.csproj` в Visual Studio 2017
2. Выберите конфигурацию Debug или Release
3. Нажмите Build → Build Solution (Ctrl+Shift+B)
4. Executable файл появится в папке `bin\Debug\` или `bin\Release\`

## Использование

### Базовая деобфускация
```bash
Deobfuscator.exe obfuscated.exe cleaned.exe
```

### С AI-ассистентом (Ollama по умолчанию)
```bash
Deobfuscator.exe input.exe output.exe --ai
```

### С LM Studio
```bash
Deobfuscator.exe input.exe output.exe --ai --ai-url http://localhost:1234 --ai-model codellama
```

### Полная конфигурация
```bash
Deobfuscator.exe input.exe output.exe --ai --ai-url http://localhost:11434 --ai-model deepseek-coder --ai-timeout 180
```

### Параметры командной строки

| Параметр | Описание | По умолчанию |
|----------|----------|--------------|
| `<входной_файл>` | Путь к обфусцированной сборке | Обязательно |
| `<выходной_файл>` | Путь для сохранения результата | Обязательно |
| `--ai` | Включить AI переименование | Выключено |
| `--ai-url <url>` | URL AI сервера | http://localhost:11434 |
| `--ai-model <name>` | Имя модели | llama3 |
| `--ai-timeout <s>` | Таймаут запроса (сек) | 120 |
| `--help`, `-h` | Показать справку | - |

## Поддерживаемые AI серверы

- **Ollama** - http://localhost:11434
- **LM Studio** - http://localhost:1234
- Любые OpenAI-совместимые API серверы

## Примеры использования AI

### Ollama с моделью llama3
```bash
# Сначала установите модель в Ollama
ollama pull llama3

# Запустите деобфускатор
Deobfuscator.exe protected.exe cleaned.exe --ai --ai-model llama3
```

### LM Studio с CodeLlama
```bash
# Запустите LM Studio и загрузите модель
# Затем используйте:
Deobfuscator.exe protected.exe cleaned.exe --ai --ai-url http://localhost:1234 --ai-model codellama
```

## Структура проекта

```
Deobfuscator/
├── Deobfuscator.csproj      # Файл проекта VS 2017
├── App.config               # Конфигурация приложения
├── packages.config          # Список NuGet пакетов
├── Program.cs               # Точка входа, парсинг аргументов
├── AiConfig.cs              # Конфигурация AI подключения
├── AiAssistant.cs           # Класс для работы с AI API
├── UniversalDeobfuscator.cs # Основной класс деобфускации
├── Properties/
│   └── AssemblyInfo.cs      # Информация о сборке
└── README.md                # Документация
```

## Алгоритм работы

1. **Загрузка сборки** - загрузка .NET assembly через dnlib
2. **Сбор констант** - отслеживание присваиваний констант локальным переменным
3. **Упрощение условий** - замена сравнений с известными константами
4. **Распутывание goto** - упрощение цепочек переходов
5. **Обработка циклов** - упрощение while/do-while с константными условиями
6. **Switch оптимизация** - замена switch на прямые переходы
7. **Удаление мертвого кода** - анализ достижимости инструкций
8. **AI переименование** (опционально) - осмысленные имена через LLM
9. **Сохранение** - запись деобфусцированной сборки

## Примечания

- AI функциональность работает только при запущенном локальном сервере
- Для больших методов увеличьте таймаут параметром `--ai-timeout`
- Деобфускатор создает новую сборку, оригинальный файл не изменяется
- Поддерживаются .NET Framework и .NET Core/5+/6+ сборки

## Лицензия

MIT License
