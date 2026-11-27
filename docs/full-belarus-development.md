# Полная карта Беларуси в локальном приложении

Исходники по умолчанию используют небольшой реальный район для быстрых тестов.
Текущий механизм подготовки полной Беларуси — locked `country_data.py` и
`tools/GeodataPreparation/belarus.lock.json`; он общий для локального запуска и CI.

Из корня репозитория:

```sh
uv sync --locked
uv run --locked python tools/GeodataPreparation/country_data.py ensure --output artifacts/belarus-current
uv run --locked python tools/GeodataPreparation/country_data.py validate --output artifacts/belarus-current
```

`ensure` получает готовый Release package, если его recipe совпадает с текущими
исходниками. После изменения конвертера явно создаёт пакет из закреплённого PBF
через locked GIS pipeline. SHA-256 источника и файлов, runtime, зависимости,
лицензии и recipe проверяются отдельно. Недоступный готовый пакет не подменяется
скрытой конвертацией. Подробности: [геоданные в CI](ci-geodata.md).
Полные PBF, SQLite и APK остаются в `artifacts/` и не включаются в Git.

Проверенный кеш содержит `geodata.sqlite.gz`. Reader и Desktop читают
распакованную `geodata.sqlite`; не добавляйте её в кеш с `package.json`, иначе
повторный `validate` отклонит изменённый состав. Создайте отдельную runtime-копию
в новом каталоге (сохраняются лицензии и NOTICE), затем распакуйте её штатным
компонентом приложения:

```sh
uv run --locked python -c "import shutil; shutil.copytree('artifacts/belarus-current', 'artifacts/belarus-runtime')"
dotnet run --project tools/CableRoutePlanner.Verify -- --install-assets artifacts/belarus-current artifacts/belarus-runtime
```

При повторной подготовке используйте новый runtime-каталог. Для проверки данных
страны и шести областных центров через действующий C# reader:

```sh
dotnet run --project tools/CableRoutePlanner.Verify -- --inspect artifacts/belarus-runtime artifacts/belarus-package-check.json
```

PowerShell-запуск текущего Desktop:

```powershell
$env:CABLEROUTE_DATA = (Resolve-Path artifacts/belarus-runtime).Path
dotnet run --project src/CableRoutePlanner.Desktop
```

В shell Linux/macOS:

```sh
CABLEROUTE_DATA="$PWD/artifacts/belarus-runtime" dotnet run --project src/CableRoutePlanner.Desktop
```

Для собственной сборки укажите распакованную runtime-копию через
`-p:GeodataDirectory=<absolute-path-to-runtime-package>`
при publish. CI сохраняет маленький fixture для быстрых проверок; workflow
`Full Belarus offline installers` собирает установщики с полной страной.

Прежний установщик из APK 0.2.0 удалён: он закреплял старую SQLite v2 и не являлся
способом получения текущей SQLite v3. Ранее сохранённые отчёты и снимки относятся
к тому историческому пакету; их контрольные суммы не описывают новую подготовку.
Не переиспользуйте его каталог как кеш `country_data.py`.

Это OSM extract Беларуси, а не обещание полноты препятствий или инженерных
согласований. Атрибуция OpenStreetMap, ODbL/DbCL и источник Geofabrik сохраняются.
