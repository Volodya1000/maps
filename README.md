# Планировщик кабельных маршрутов Беларуси

C# / Avalonia / ReactiveUI. **Рабочая реализация 2D и расчёта маршрутов; полное MVP по ТЗ пока не готово.** Нет подтверждения Windows 7, встроенного 3D и полного набора Беларуси. Это ограничения приёмки, а не снятые требования.

Реализованы:
- Настоящая офлайн-карта OSM района Дрозды — Новинки в Минске: SQLite, R-tree, векторное отображение Mapsui. Никаких запросов к тайловым серверам.
- Добавление, перетаскивание, удаление, очистка, перестановка точек и копирование координат. Русский адаптивный интерфейс.
- A* по сетке местности с произвольными конечными точками и последовательными промежуточными точками. До трёх различных трасс по профилям стоимости; одинаковые трассы не дублируются.
- Запрет болот, озёр, зданий; реки только через явно разрешённые переходы. Штрафы за лес, поле и пересечение дорог. Мост из OSM сам по себе не разрешает переход.
- Длина и характеристики вычисляются по полученной геометрии и реальным слоям. При изменении точек маршруты удаляются; запоздавшие результаты не публикуются.
- Отдельные Domain, Application, Infrastructure, ViewModels, UI, Bootstrap, Desktop и Android. DI, ReactiveCommand, RoutingState, типизированный ViewLocator, compiled bindings, ReactiveUI.Fody.
- xUnit, Avalonia.Headless, GitHub Actions, самостоятельный инструмент проверки реального маршрута.

## Скачать тестовый APK

[Прямая ссылка на подписанный APK (69 МБ)](https://github.com/Volodya1000/maps/raw/refs/heads/downloads/android-preview/CableRoutePlanner-0.1.1-test.apk). [PR с исходниками](https://github.com/Volodya1000/maps/pull/1). Версия 0.1.1 исправляет падение Android Activity из-за отсутствующей AppCompat-темы. Проверены скачивание, подпись и три запуска в эмуляторе Android 14; повторная проверка на Xiaomi пользователя ещё требуется.

## Запуск Desktop

Нужен .NET SDK из `global.json`. Данные небольшого реального района уже включены в репозиторий; интернет нужен для первоначального восстановления NuGet, не для работы приложения.

```sh
dotnet restore CableRoutePlanner.Desktop.slnf --locked-mode
dotnet build CableRoutePlanner.Desktop.slnf --no-restore
dotnet test CableRoutePlanner.Desktop.slnf --no-restore
dotnet run --project src/CableRoutePlanner.Desktop
```

Единое решение — `CableRoutePlanner.sln`, фильтр `CableRoutePlanner.Desktop.slnf` позволяет работать без Android workload. На Linux для графического запуска нужны X11/Wayland и системные библиотеки Avalonia; headless-тесты не требуют дисплея.

Панорамирование — перетаскивание свободного места; масштабирование — колесо или `+ / −`; точки ставятся коротким нажатием при включённом «Добавлять точки», маркеры перетаскиваются. На узком экране панель переносится вниз. Фактическая проверка жестов Android ещё требуется.

Пакет данных выбирается переменной `CABLEROUTE_DATA` (каталог с `manifest.json` и `geodata.sqlite`). При отсутствии или повреждении пакета отображается ошибка. Синтетические данные не подставляются.

Самодостаточная Windows-сборка:

```sh
dotnet publish src/CableRoutePlanner.Desktop -c Release -r win-x64 --self-contained true -o artifacts/win-x64
```

Эта команда не доказывает поддержку Windows 7 и не заменяет запуск на Windows 10.

## Android APK

Требуются JDK 17, Android SDK Platform 34 / Build Tools 34.0.0 и workload:

```sh
dotnet workload install android --skip-manifest-update
dotnet publish src/CableRoutePlanner.Android -c Release -p:AndroidSdkDirectory=/path/to/android-sdk -p:JavaSdkDirectory=/path/to/jdk17
```

APK: `src/CableRoutePlanner.Android/bin/Release/net8.0-android/publish/by.cablerouteplanner.app-Signed.apk`. Подпись — локальный ключ разработчика, для тестирования. Проект запрещает `INTERNET` в конечном манифесте. Карта копируется из assets в приватный каталог приложения. Установка: `adb install -r <apk>`; запуск и касания на устройстве пока не подтверждены.

## Проверка настоящего расчёта без интерфейса

```sh
dotnet run --project tools/CableRoutePlanner.Verify -- samples/real-geodata-fixtures/minsk artifacts/route.geojson 53.9490,27.5180 53.9500,27.5190
```

Координаты задаются пользователем. Выходной GeoJSON содержит рассчитанную трассу и метрики. Маршруты из этого файла не используются приложением как заготовки.

## Исследование 3D

Отдельный [MapLibre-стенд](spikes/MapLibre/README.md) содержит локальные OSM-векторы и настоящие Terrarium-тайлы Copernicus DEM. В Linux Chromium проверены terrain, маркеры, обмен JS-событиями, LineString и отсутствие внешних запросов. Он **не встроен в Avalonia** и не закрывает FR-007. Кнопка 3D в приложении явно сообщает о недоступности.

## Ограничения приёмки

- Windows 7: современный runtime не поддерживается поставщиком; испытаний на чистой ОС нет. Требование остаётся обязательным.
- 3D: C# WebView, лицензия выбранного адаптера и Windows/Android GPU не проверены.
- Основной пакет содержит только OSM. DEM есть только в отдельном стенде; ESA WorldCover отсутствует. Уклоны и высотные метрики в основном приложении не рассчитываются.
- Полный пакет Беларуси и потоковая подготовка PBF / тайлов не реализованы. Пространственная выборка есть; полная карта страны ещё не поставляется.
- Расчёт ограничен областью 20 км, 100 000 узлов и 100 точками. Сетка может не обнаружить слишком узкий допустимый коридор; уменьшите ячейку. Недостающие препятствия OSM остаются неизвестными.
- Возвращается от одного до трёх разных маршрутов, без обещания трёх независимых коридоров. Целевая функция — инженерная эвристика, не смета.

[Архитектура](docs/architecture.md) · [Источники](docs/data-sources.md) · [Алгоритм](docs/routing-algorithm.md) · [Тестирование](docs/testing.md) · [Платформы](docs/platform-compatibility.md) · [Матрица требований](docs/acceptance.md).

Результаты предназначены для предварительного планирования. Они не заменяют обследование местности, согласование переходов и рабочий проект строительства.
