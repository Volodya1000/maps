# Работа с репозиторием

- Перед изменениями прочитайте `docs/development.md`. Установите зависимости через `uv sync --locked`, hooks через `uv run --locked pre-commit install`.
- Перед коммитом добавьте собственный непустой фрагмент `changes/<номер-задачи-или-PR>.<feature|bugfix|refactor|doc>.md`. Обычные PR не редактируют `CHANGELOG.md`; это делает Towncrier в release-коммите.
- Используйте Conventional Commits: `feat(scope): ...`, `fix(scope): ...`, `refactor(scope): ...`, `test(scope): ...`, `docs(scope): ...`, `build(scope): ...`, `ci(scope): ...`. Scope кратко называет изменённую область (`routing`, `ui`, `android`, `ci`, `docs`); описание пишите в повелительном наклонении.
- Перед коммитом и отправкой выполняйте применимые проверки `tools/check.py`; CI использует те же команды. Не обходите hooks, не отправляйте известные локальные ошибки для проверки в Actions. Если платформенная проверка недоступна, явно укажите её, не называйте её успешной.
- C# build использует встроенные анализаторы .NET 8 в режиме `All`, SonarAnalyzer.CSharp и Meziantou.Analyzer. Предупреждения являются ошибками; исправляйте реализацию, а не подавляйте диагностики. Coverage gate измеряет прикладные библиотеки через Coverlet и требует не менее 85% line/80% branch coverage.
- Тестируйте наблюдаемые результаты пользовательских действий и доменные правила. Не проверяйте private-методы, число внутренних вызовов или имена CSS-классов. Подготовку SQLite/временных файлов держите в изолированных disposable-фикстурах; не скрывайте ошибки очистки.
- Не включайте в Git полные карты, APK или SDK. Они находятся в `artifacts/` либо внешних каталогах. Приложение остаётся автономным; загрузка пакетов выполняется отдельным инструментом подготовки.
- Предметные правила проходимости, стоимости и размещения точек размещайте в Domain; Domain использует только BCL и не ссылается на другие проекты решения или внешние библиотеки.
- Геометрические операции NTS, пространственные индексы и алгоритмы поиска размещайте в Infrastructure. UI и ViewModels не содержат правил маршрутизации.
- Desktop, Android и Verify являются Composition Root: конкретные Infrastructure и Persistence реализации подключаются только регистрацией зависимостей.
- Не считайте OSM-признак моста разрешением на переход. Геометрически допустимые участки проходят предметную проверку Domain, а инженерная пригодность требует отдельного подтверждения.
- Храните ограничения и значимые технические пороги в одном месте; формируйте числовые диапазоны сообщений из этих значений. Именуйте алгоритмические допуски с единицами измерения.
- Проверяйте согласованность модели целиком. Ошибка валидации должна указывать конкретное поле, фактическое значение и нарушенное правило; противоречивые данные не должны давать правдоподобный результат.
- Обрабатывайте исключения и отмену фоновых операций, освобождайте их ресурсы. Не подавляйте ошибки пустыми обработчиками.
- В архитектурных проверках отличайте разрешённые зависимости от обязательных. Добавляйте абстракции только при практической необходимости.
- При изменении AXAML, compiled bindings, ReactiveUI, стилей, шаблонов или особенностей Avalonia сначала используйте официальный Avalonia Docs MCP, если он доступен. Это не требуется для каждого изменения C#.

## Навигация по Agent Skills

Содержимое skills находится в `.agents/skills/`; здесь указаны только условия выбора, а не дублируются их процедуры:

- `csharp-refactoring` — только поведенчески нейтральный рефакторинг C# (переименование, выделение, устранение дублирования); не для исправления поведения или обновления зависимостей.
- `code-testing` — создание или расширение xUnit-тестов; `test-value-evidence` — доказательство, что тест ловит конкретную регрессию; `test-gap-analysis` — общий поиск непроверенных сценариев.
- `routing-regression` — изменения A*, геометрии маршрута, проходимости, стоимости, мостов, отмены или устаревших результатов.
- `architecture-review` — изменения границ проектов, MVVM, DI или архитектурных абстракций.
- `avalonia-ui-acceptance` — AXAML, compiled bindings, ReactiveUI, шаблоны, стили и UI/жесты Avalonia.
- `pr-preflight` — перед PR для анализа diff, выбора существующих проверок, changelog и отчёта `PASS`/`NOT VERIFIED`.

Для простой документации, форматирования или локального C#-изменения без соответствующего риска skills не навязываются.

## C# Type Declaration and Readability Policy

- Prefer explicit local variable types.
- Avoid `var` when the type is not apparent from the right-hand side; prefer target-typed `new()` when the explicit declaration already gives the type.
- Use interfaces when they accurately express the required contract, but never replace concrete types mechanically.
- Keep `var` where the language requires it, especially for anonymous types, and where the right-hand side makes the type apparent.
- Do not use `dynamic`, `object`, casts, or diagnostic suppressions to bypass this policy.
- Preserve runtime behavior, overload resolution, and LINQ execution semantics when choosing a type.
- Treat `.editorconfig` as the authoritative source for enforceable style rules. Run the relevant code-style checks locally before pushing.
- Never disable analyzers or weaken CI quality gates to make a change pass.

Preferred:

```csharp
ExecutionResult result = await service.ExecuteAsync();
User user = new();
var anonymous = new { Name = "Test" };
IReadOnlyList<Waypoint> Points { get; }
```

Avoid:

```csharp
var result = await service.ExecuteAsync();
var user = new User();
```

For property accessors, keep trivial getter/setter declarations on one line
(`IReadOnlyList<Waypoint> Points { get; }`). This is a readability convention
for accessors only; it does not require unrelated blocks or statements to be
collapsed.
