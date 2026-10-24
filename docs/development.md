# Локальная проверка перед отправкой

Нужны Git, Python 3.12, uv 0.12.23 и SDK из `global.json`. Для аудита автономности Linux нужен `strace`. Установите hooks один раз в каждом клоне:

```sh
uv sync --locked
uv run --locked pre-commit install
```

Pre-commit проверяет changelog-фрагмент, синтаксис Python/JSON/TOML/YAML, форматирование C#, Release-сборку с предупреждениями как ошибками, весь набор .NET-тестов и аудит автономности на Linux. C# анализируется встроенными [.NET SDK analyzers](https://learn.microsoft.com/dotnet/fundamentals/code-analysis/overview) уровня 8.0 Recommended; избранные code-style диагностики повышены до warning и потому тоже блокируют Release-сборку. Изменения подготовки данных и браузерного стенда дополнительно запускают соответствующие проверки. Pre-push собирает Android при изменениях общего C#-кода или платформенного проекта. Нужны JDK 17, Android workload, SDK API/build-tools 34.0.0 и `ANDROID_HOME`.

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

На Windows desktop-проверка также публикует самодостаточную win-x64 сборку, на Linux проверяет отсутствие внешних сетевых обращений Verify. Тесты получают 30 секунд до диагностики зависания, весь запуск — 180 секунд; CI дополнительно ограничивает длительность job. Вторая matrix-ОС, установка Windows-пакета и Android-эмулятор остаются платформенными проверками: Linux-hook не может доказать их прохождение. Эмулятор требует Android SDK/adb и аппаратной виртуализации, которой в некоторых средах разработки нет. В CI дорогая сборка APK начинается только после успешных quality/desktop jobs; новый коммит отменяет устаревший запуск.

Перед коммитом добавьте изменения в index: проверка фрагмента читает именно staged-содержимое. Один PR должен добавить собственный фрагмент; существующие фрагменты других PR не заменяют новый. Пример: `changes/5.refactor.md`. CI сравнивает с фактической базовой веткой PR, включая зависимые PR. [Правила и сборка релиза](../changes/README.md).

Для зависимого PR задайте `CHANGELOG_BASE=origin/<родительская-ветка>` перед запуском hooks, чтобы локальное сравнение использовало ту же базу, что и CI. Для обычных PR база по умолчанию — `origin/main`.

Не используйте `--no-verify` для обычной отправки. Не заявляйте успешную проверку Android/Windows только на основании сборки на другой ОС. Локальная проверка убирает воспроизводимые ошибки перед Actions, но не отменяет независимую проверку целевых платформ.
