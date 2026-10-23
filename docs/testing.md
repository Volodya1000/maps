# Проверка и воспроизведение

Локально 2026-10-23, Linux x64: **34/34 теста проходят**, включая реальный OSM-маршрут, запреты препятствий и переходов, late-result invalidation, compiled XAML, мышь, навигацию и Fody PropertyChanged после реальной компиляции. Тесты не подменяют алгоритм production-результатами. Synthetic-fixtures создаются явно в тестах; основной UI использует настоящий включённый пакет.

```sh
dotnet restore CableRoutePlanner.Desktop.slnf --locked-mode
dotnet test CableRoutePlanner.Desktop.slnf --no-restore --logger trx --results-directory artifacts/test-results
uv sync --project tools/GeodataPreparation --locked
uv run --project tools/GeodataPreparation --locked python tools/GeodataPreparation/rebuild_sample.py --output artifacts/rebuilt-minsk
```

GitHub Actions: Linux/Windows headless-тесты, Windows publish, Android APK и отдельная браузерная DEM-проверка. Это не доказательство работы на Windows 7/10 или Android-устройстве: windows-2022 — Windows Server, а hosted Linux — не телефон. Удалённый workflow запущен в PR #1 после предоставления доступа. Его статус нужно проверять на GitHub; локальное прохождение тестов не означает автоматически успешный удалённый CI.

## Аудит автономности Linux

Для отделения телеметрии SDK от приложения запускается уже собранная Verify DLL, без `dotnet run`, restore или build:

```sh
strace -f -e trace=connect,sendto -o artifacts/runtime.strace dotnet tools/CableRoutePlanner.Verify/bin/Debug/net8.0/CableRoutePlanner.Verify.dll samples/real-geodata-fixtures/minsk artifacts/route.geojson 53.9490,27.5180 53.9500,27.5190
python tools/check_network.py artifacts/runtime.strace --output artifacts/network-report.json
```

Обычный `dotnet test --no-build` в среде с workload проявил сетевые обращения SDK; поэтому его нельзя выдавать за доказательство обращений приложения. Прямой запуск vstest.console.dll с отключённой телеметрией показал только loopback IPC тест-раннера. Полная блокировка сети через unshare недоступна в этой среде (Operation not permitted). Стенд MapLibre дополнительно проверен Playwright с принудительным отказом внешним запросам.

## Windows 10 и Windows 7, отдельные чистые VM

Для каждой ОС зафиксировать редакцию/сборку, SP/KB, GPU/драйвер, SHA исходников и хеш архива. Скопировать самодостаточную win-x64 папку на чистую VM без SDK. Запретить исходящую сеть приложению и проверить журнал/сетевую трассу.

Проверить запуск, OSM-слои, pan/zoom, клавиатуру, добавление/перенос/перестановку/копирование/удаление точек, построение, отмену, устаревание результатов и 100 переходов между экранами. Повторить с отсутствующим/повреждённым пакетом и с точкой вне покрытия. Для примера реальные точки: `53.9490, 27.5180` → `53.9500, 27.5190`; трасса вычисляется, не подгружается.

Проверка Windows 7 должна начинаться с отдельного runtime/Avalonia/Mapsui smoke-test. Текущая .NET 8 сборка не считается совместимой. При невозможности запуска сохранить реальную ошибку, логи и prerequisites, затем исследовать адаптер. Нельзя записывать неподдерживаемую платформу как пройденную.

## Android-устройство

Сохранить API/модель/GPU/WebView-версию, SHA APK и исходников. Установить Signed.apk через adb, включить авиарежим, запустить приложение. Проверить первый запуск с копированием assets, касания, pinch-zoom, панорамирование, перенос точки одним пальцем, второй палец при переносе, отмену capture, перенос панели при повороте, clipboard и повторный запуск. `aapt dump permissions <apk>` не должен показывать INTERNET. Подпись разработчика не предназначена для production-обновлений.

## 3D

Браузерный сценарий описан отдельно в spikes/MapLibre. Для приёмки требуется подключить WebView C# на каждой ОС и повторить сценарий с настоящим DEM, высотными различиями, камерой, маршрутом поверх рельефа и сетевой блокировкой. Чистый hillshade или запуск браузерного стенда отдельно от приложения не закрывает этот пункт.

## Регрессия Android-темы (0.1.1)

Первый APK 0.1.0 падал в AndroidX AppCompatActivity до появления интерфейса: `You need to use a Theme.AppCompat theme (or descendant) with this activity`. Стартовой Activity не была назначена AppCompat-тема. В 0.1.1 задана `@style/CableRoutePlannerTheme` с родителем `Theme.AppCompat.Light.NoActionBar`; versionCode увеличен до 2 для установки поверх прежней версии тем же ключом.

Помимо сборки, CI теперь устанавливает полученный Release APK в Android 14/API 34 x86_64 эмулятор и трижды запускает Activity. Проверяются живой процесс, resumed Activity, отсутствие падения приложения в crash buffer; сохраняются logcat, screenshots и JSON. Запуск вручную на уже загруженном устройстве: `python tools/android_smoke.py --apk <Signed.apk> --output artifacts/android-smoke`. Это отдельная проверка запуска, не подтверждение всей функциональности и не замена повторной проверки Xiaomi пользователя.

Результат проверки 0.1.1: [GitHub Actions](https://github.com/Volodya1000/maps/actions/runs/39972389857) прошёл полностью, включая android-smoke. Три запуска Activity в Android 14/API 34 x86_64 успешны; screenshot показывает главный экран с настоящей картой. Отчёт: `docs/evidence/android-0.1.1.json`, screenshot: `docs/evidence/android-startup-0.1.1.png`. CI собирает APK своим тестовым ключом; скачиваемый локально собранный APK того же исходного коммита сохраняет предыдущий ключ для обновления. Его хеш проверен отдельным полным скачиванием. Это подтверждение запуска в эмуляторе, не утверждение о проверке исправления на физическом Xiaomi.
