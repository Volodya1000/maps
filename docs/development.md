# Локальная проверка перед отправкой

Нужны Git, Python 3.12, uv 0.12.23 и SDK из `global.json`. Для аудита автономности Linux нужен `strace`. Установите hooks один раз в каждом клоне:

```sh
uv sync --locked
uv run --locked pre-commit install
```

Pre-commit проверяет changelog-фрагмент, синтаксис Python/JSON/TOML/YAML, форматирование C#, Release-сборку с предупреждениями как ошибками, весь набор .NET-тестов, coverage gate и аудит автономности на Linux. C# анализируется встроенными [.NET SDK analyzers](https://learn.microsoft.com/dotnet/fundamentals/code-analysis/overview) уровня 8.0 `All`, а также `SonarAnalyzer.CSharp` и `Meziantou.Analyzer`. Избранные code-style диагностики повышены до warning и потому тоже блокируют Release-сборку. XML-документация для каждого публичного API не требуется.

Правила находятся в `Directory.Build.props` и `.editorconfig`. Исправляйте причину предупреждения в коде; не используйте `#pragma warning disable`, `#nullable disable`, `WarningsNotAsErrors`, `SuppressMessage` или массовое отключение правил. Единственное текущее исключение — точечный `AVLN3001` для DI-only Avalonia views: публичный конструктор для runtime loader нарушил бы фабричную архитектуру; причина зафиксирована рядом с настройкой проекта. Новые исключения допустимы только после такого же исследования и документирования.

CI вызывает тот же `tools/check.py`, а не копию его команд. Отсутствие инструмента завершает проверку ошибкой. Для первичной подготовки Playwright в Linux можно установить системные зависимости командой `npx --package playwright@1.55.1 playwright install-deps chromium`.

```sh
uv run --locked python tools/check.py lint
uv run --locked python tools/check.py desktop
uv run --locked python tools/check.py data
uv run --locked python tools/check.py terrain
uv run --locked python tools/check.py android
uv run --locked python tools/check.py all
uv run --locked pre-commit run --all-files
uv run --locked pre-commit run --all-files --hook-stage pre-push
```

`lint` дополнительно проверяет локальные AXAML includes, запускает штатный `dotnet sln list`, actionlint, расширенный Ruff и `ruff format --check`. Полная XML/project validation выполняется SDK restore/build в `desktop`; CSS проверяется Stylelint в существующем `terrain`-проходе. Надёжный кроссплатформенный форматтер AXAML с подтверждённой совместимостью Avalonia 11 не подключён: форматирование отдельно не проверяется, а компиляция Avalonia и локальные include-инварианты остаются проверками разметки. Для actionlint используется зафиксированный pre-commit hook `rhysd/actionlint` в manual-stage, который запускается ровно один раз из `tools/check.py lint`; требуется Go 1.23.4+ (официальный архив с go.dev/dl на рабочих станциях, `actions/setup-go` в CI).

`desktop` выполняет restore, `dotnet format` для whitespace/style/analyzers, Release build и единственный запуск xUnit-тестов с Coverlet. Покрытие собирается в Cobertura, проверяется `tools/check_coverage.py`, а HTML создаётся ReportGenerator в `artifacts/coverage-report`. Минимумы — 85% строк и 80% ветвей; проценты считаются по суммарному числу покрытых и всех строк/ветвей, а не как среднее проектов. В статистику входят Domain, Application, ViewModels, Infrastructure, Persistence и UI. Desktop/Android entry points и тестовая сборка не входят; Android анализируется отдельной Android-проверкой.

[Ресурсы и стили Avalonia](ui-styling.md) описывают палитру, подключение StyleInclude и раздельные обязанности разметки и оформления.

Windows publish записывает RID-зависимости в отдельный `obj/publish.win-x64.lock.json` каждого проекта. Обычные `packages.lock.json` сохраняются: повторный locked restore должен проходить после публикации. Структура тестовых каталогов описана в [README тестов](../tests/CableRoutePlanner.Tests/README.md).

Для просмотра отчёта локально откройте `artifacts/coverage-report/index.html`. Непокрытые строки и покрытие классов/методов видны в HTML. `line coverage` показывает выполненные исходные строки, `branch coverage` — выполненные варианты условных переходов. Сгенерированный компилятором/Avalonia/Fody код и сторонние библиотеки исключаются в `coverage.runsettings`; самостоятельно написанный прикладной код не исключается.

На Windows desktop-проверка также публикует самодостаточную win-x64 сборку, на Linux проверяет отсутствие внешних сетевых обращений Verify. Тесты получают 30 секунд до диагностики зависания, весь запуск — 180 секунд; CI дополнительно ограничивает длительность job. Вторая matrix-ОС, установка Windows-пакета и Android-эмулятор остаются платформенными проверками: Linux-hook не может доказать их прохождение. Эмулятор требует Android SDK/adb и аппаратной виртуализации, которой в некоторых средах разработки нет. В CI дорогая сборка APK начинается только после успешных quality/desktop jobs; новый коммит отменяет устаревший запуск.

Перед коммитом добавьте изменения в index: проверка фрагмента читает именно staged-содержимое. Один PR должен добавить собственный фрагмент; существующие фрагменты других PR не заменяют новый. Пример: `changes/5.refactor.md`. CI сравнивает с фактической базовой веткой PR, включая зависимые PR. [Правила и сборка релиза](../changes/README.md).

Для зависимого PR задайте `CHANGELOG_BASE=origin/<родительская-ветка>` перед запуском hooks, чтобы локальное сравнение использовало ту же базу, что и CI. Для обычных PR база по умолчанию — `origin/main`.

Не используйте `--no-verify` для обычной отправки. Не заявляйте успешную проверку Android/Windows только на основании сборки на другой ОС. Локальная проверка убирает воспроизводимые ошибки перед Actions, но не отменяет независимую проверку целевых платформ.

Полная карта в CI получается из версионированного Release; кеш служит ускорением. [Источники, SHA-256, обновление и восстановление геоданных](ci-geodata.md).
