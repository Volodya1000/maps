# Измерения CI — issue #61

## Методика и исходная точка

[Issue #61](https://github.com/Volodya1000/maps/issues/61) требует измерений до выбора настроек. Исходная ревизия этого PR — `7c4bec1b2fb5dbb5a51439260c93385a872d6ce2`.
[Машинные данные baseline](benchmarks/ci-61-baseline.json) содержат SHA, timestamps jobs и длительности успешных подшагов из публичного Actions API. Повторить получение: `GET /repos/Volodya1000/maps/actions/runs/<run-id>/jobs?per_page=100`; при большем числе jobs следует продолжить пагинацию.
Wall-clock здесь — от начала первого job до конца последнего; runner-minutes — сумма длительностей успешных jobs, без тарифных коэффициентов Windows, очереди и округления биллинга. Это исторические наблюдения на разных SHA/runner images, а не контролируемый A/B одного коммита. CPU/RAM старые логи не собирали; эти значения неизвестны. [Выдержки логов cache/download](benchmarks/ci-61-cache-evidence.json) подтверждают классификацию. Три наблюдения неизменного опубликованного рецепта подтверждают cold-cache Release download, три — полную конвертацию изменённого рецепта, два — exact-key hit последующих неизменных запусков. Для published recipe PR cache намеренно не сохраняется: его повторный cold Release download нельзя называть повторной конвертацией.

| Run | Карта/кэш | Wall-clock, с | Runner-minutes | Geodata, с | Полный Android, с |
| --- | --- | ---: | ---: | ---: | ---: |
| [44776950195](https://github.com/Volodya1000/maps/actions/runs/44776950195) | changed recipe; cold exact-key cache (cf38347c) | 2700 | 49.87 | 1397 | 717 |
| [45000446180](https://github.com/Volodya1000/maps/actions/runs/45000446180) | same cf38347c recipe; warm exact-key cache | 1345 | 26.90 | 34 | 663 |
| [45706672607](https://github.com/Volodya1000/maps/actions/runs/45706672607) | changed recipe; cold exact-key cache (7946b228) | 2976 | 52.50 | 1548 | 798 |
| [45796103324](https://github.com/Volodya1000/maps/actions/runs/45796103324) | cf38347c recipe; warm exact-key cache | 1396 | 27.03 | 36 | 750 |
| [44384044843](https://github.com/Volodya1000/maps/actions/runs/44384044843) | changed recipe; cold exact-key cache (000f0891) | 1922 | 35.08 | 696 | 700 |
| [43188541824](https://github.com/Volodya1000/maps/actions/runs/43188541824) | unchanged published f94aa250 recipe; cold Actions cache, Release download | 1166 | 23.65 | 35 | 567 |
| [43194933778](https://github.com/Volodya1000/maps/actions/runs/43194933778) | unchanged published f94aa250 recipe; cold Actions cache, Release download | 1203 | 23.65 | 26 | 628 |
| [43374709737](https://github.com/Volodya1000/maps/actions/runs/43374709737) | unchanged published f94aa250 recipe; cold Actions cache, Release download | 1315 | 24.87 | 35 | 755 |

Исходный run #295 завершился: полный Android job занял **717 с**, весь workflow **2700 с (45 мин)**, сумма jobs **49,87 runner-minutes**. Тем самым дополнена незавершённая в issue исходная таблица. Передача country artifact в этом запуске — около 1,2 ГБ; это не размер распакованной SQLite.

## Выбор графа зависимостей

Вариант A запускает geodata после quality, обеих desktop-платформ, data и terrain, одновременно с Android Minsk. Вариант B запускает сразу после quality. Внедрён **A**: он не тратит полную конвертацию на PR, упавшие на desktop/data/terrain. Оба варианта сохраняют ожидание всех обязательных gates перед потребителями полной карты.

Модель переносит измеренную длительность geodata на новое время старта, без изменения длительности остальных jobs и без предположений об очереди GitHub:

| Run | Расчётная экономия A, с | Расчётная экономия B, с |
| --- | ---: | ---: |
| 44776950195 | 301 | 534 |
| 45000446180 | 40 | 40 |
| 45706672607 | 316 | 576 |
| 45796103324 | 42 | 42 |
| 44384044843 | 256 | 478 |

Это прогноз критического пути, не новый измеренный CI run. На cache hit короткая подготовка скрывается под Android и B почти не даёт дополнительного выигрыша. На cache miss B перекрывает ещё desktop-время, но увеличивает стоимость падающих PR. Вариант A не добавляет jobs и не увеличивает полезные runner-minutes на успешном пути; при падении Minsk Android возможна лишняя конвертация. Отмена устаревшего workflow сохранена.

Geodata публикуется внутри того же run и берётся из точного `github.sha`. Reusable APK workflow по-прежнему проверяет переданный tested SHA и PR number. Полные APK и routing benchmark получают данные только после успешных исходных gates и geodata. Minsk APK и временный ключ переиспользуются из проверенного Android job. Обе проверки установки/запуска эмулятором, permissions, версия, сертификаты, checksums и optional routing label сохранены.

## Gzip: воспроизводимый эксперимент

`tools/benchmark_android_map.py` запускает каждый вариант в отдельном процессе, чередует порядок повторов, измеряет wall/CPU/peak RSS сжатия, размер gzip и время чтения/распаковки с SHA-256. SHA распакованных chunks обязан совпасть с исходным manifest. Исходные файлы не изменяются; временные результаты удаляются после каждого варианта. Время включает копирование сопутствующих assets. Оно не включает Android install/startup и создание APK; размер gzip нельзя выдавать за размер APK.

```powershell
uv run --locked python tools/benchmark_android_map.py artifacts/belarus artifacts/gzip-levels --repeats 3 --levels 1 3 6 9 --workers 1
uv run --locked python tools/benchmark_android_map.py artifacts/belarus artifacts/gzip-workers --repeats 3 --levels 6 --workers 2 4
```

Локальный вход: полная SQLite Беларуси из прежней проверенной подготовки, 2 577 719 296 байт, SHA-256 `f584e0c9963692b31b0f2b6322dc810641729bbabcc410626faf8ea70b703493`. Это не свежий выпуск текущего рецепта; он используется только как одинаковый большой вход для сравнения gzip. Исходная SHA проверена по его manifest. Для актуального рецепта предусмотрен opt-in CI benchmark ниже.

Два полных локальных прохода завершены. [Сырые результаты уровней](benchmarks/ci-61-gzip-levels-windows.json), [workers](benchmarks/ci-61-gzip-workers-windows.json). Windows Server 2022, Python 3.12.14, zlib 1.3.2, 28 logical CPUs. Есть фоновая нагрузка других задач; порядок второго прохода обратный. Время — диапазон двух повторов, CPU — медиана, RSS — максимум.

| Level / workers | Сжатие, с | CPU, с | Peak RSS, МиБ | Gzip, байт | Чтение + hash, с |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1 / 1 | 97.90–102.72 | 92.66 | 151.88 | 1,234,963,519 | 23.60–25.68 |
| 3 / 1 | 108.22–112.31 | 102.80 | 151.94 | 1,232,408,278 | 22.90–24.50 |
| 6 / 1 | 168.83–179.71 | 165.48 | 151.23 | 1,211,703,118 | 23.35–23.74 |
| 9 / 1 | 394.23–404.54 | 390.57 | 151.28 | 1,210,638,768 | 23.11–25.18 |
| 6 / 2 | 90.56–90.99 | 171.84 | 247.15 | 1,211,703,118 | 23.40–23.55 |
| 6 / 4 | 46.46–46.99 | 167.50 | 425.86 | 1,211,703,118 | 23.55–24.05 |

Level 6 / один worker сократил медиану сжатия с 399,38 до 174,27 с (56,4%) при росте gzip на 1 064 350 байт (0,088%). Четыре workers дополнительно снизили медиану до 46,72 с с peak RSS до 426 МиБ; gzip-байты одинаковы при любом числе workers. Каждый из 12 случаев восстановил исходный SHA-256 SQLite. Первое CI-измерение сохраняло level 9 / один worker; выбор 6 / 4 подтверждён отдельным Linux-экспериментом ниже, Android сравнивается отдельно. Локальные результаты не доказывают размер APK и время Android install/startup.

Gzip `mtime=0`, размеры распакованных chunks 64 МиБ и последовательные имена сохраняются. Число workers ограничено 1–4; очередь удерживает не больше workers chunks. zlib-компрессия выполняется в threads без копирования блоков между процессами. Тесты сравнивают gzip-байты при 1/2/4 workers, восстанавливают SQLite на всех уровнях и проверяют ошибки ввода/записи.

### Linux hosted runner, текущий рецепт

В [исследовательском run](https://github.com/Volodya1000/maps/actions/runs/46013430359) gzip-матрица прошла полностью: 3 повтора на комбинацию, 18 случаев. [Levels JSON](benchmarks/ci-61-gzip-levels-linux.json), [workers JSON](benchmarks/ci-61-gzip-workers-linux.json). Ubuntu 22.04 / 4 logical CPUs, Python 3.12.16, zlib 1.2.11. Текущая SQLite SHA-256 `1d055f4987932865150fbea2b4b1a3b9f7742127da7bdc7bcefeb7f064219439`; это отдельный вход от старого Windows-набора. Во всех 18 случаях она восстановлена точно. Время/CPU — медианы, RSS — максимум.

| Level / workers | Wall, с | CPU, с | Peak RSS, МиБ | Gzip, байт |
| --- | ---: | ---: | ---: | ---: |
| 1 / 1 | 60.13 | 60.12 | 205.95 | 1,239,759,980 |
| 3 / 1 | 68.25 | 68.25 | 205.89 | 1,237,260,134 |
| 6 / 1 | 113.15 | 113.14 | 205.84 | 1,216,334,617 |
| 9 / 1 | 279.75 | 279.73 | 205.81 | 1,215,251,178 |
| 6 / 2 | 58.24 | 112.81 | 310.27 | 1,216,334,617 |
| 6 / 4 | 37.75 | 138.57 | 558.94 | 1,216,334,617 |

По измерениям выбран режим **6 / 4**: split wall меньше на 86,5% относительно 9 / 1, CPU меньше примерно на 50%, gzip больше на 1 083 439 байт (0,089%). Против двух workers четыре экономят ещё 20,49 с wall (35,2%), но требуют примерно на 23% больше CPU и до 559 МиБ RSS вместо 310 МиБ. На измеренном фиксированном 4-core runner это сокращает занятые runner-minutes; пик памяти остаётся ограниченным. Для малых машин доступен `--workers 1`. Default — 6 / 4; он выбран после baseline 9 / 1 и воспроизводимой Linux-матрицы. Установка/первый запуск сравниваются отдельно; их единичные значения не доказывают ускорение Android runtime.


### Размеры реально собранных APK

[Сырые результаты](benchmarks/ci-61-apks-windows.json): один APK на уровень, тот же полный SQLite-вход, 4 workers, один application ID / version code, JDK 17 / build-tools 34.0.0. Использован код commit `62a72d8`. Это Windows-сборки без country DEM, не распространяемые preview APK текущего CI. Во всех APK проверены exact manifest, восстановленная SHA-256 SQLite и отсутствие INTERNET permission. Временные APK удалены после проверки.

| Level | Gzip assets, байт | Map entries внутри ZIP, байт | Полный APK, байт |
| --- | ---: | ---: | ---: |
| 1 | 1,234,963,519 | 1,233,569,813 | 1,303,532,006 |
| 3 | 1,232,408,278 | 1,230,683,558 | 1,300,644,326 |
| 6 | 1,211,703,118 | 1,210,012,612 | 1,279,971,814 |
| 9 | 1,210,638,768 | 1,208,946,965 | 1,278,906,854 |

Level 6 увеличил полный APK относительно 9 на 1 064 960 байт (0,0833%). Levels 1/3 экономят ещё CPU, но увеличивают размер примерно на 22–25 МБ; для установки APK с country DEM такой обмен ещё надо проверять. Первая сборка была холодной, следующие использовали тёплый build cache; publish_seconds сохранены только как наблюдения, не доказательство ускорения. Каждый вариант восстановил те же 2 577 719 296 байт SQLite.

Для воспроизведения размеров использовать новый пустой каталог на каждый уровень:

```powershell
$versionCode = ((python tools/release_version.py | Select-String '^ANDROID_VERSION_CODE=') -split '=', 2)[1]
$runId = [guid]::NewGuid().ToString('N')
foreach ($level in 1, 3, 6, 9) {
    $caseDir = "$PWD/artifacts/apk-benchmark/$runId/level-$level"
    python tools/split_android_map.py artifacts/belarus "$caseDir/chunks" --compresslevel $level --workers 4
    if ($LASTEXITCODE -ne 0) { throw "Split failed: $LASTEXITCODE" }
    dotnet publish src/CableRoutePlanner.Android -c Release -p:ApplicationId=by.cablerouteplanner.benchmark "-p:AndroidVersionCode=$versionCode" "-p:GeodataDirectory=$caseDir/chunks" "-p:AndroidSdkDirectory=$env:ANDROID_HOME" "-p:JavaSdkDirectory=$env:JAVA_HOME" -o "$caseDir/publish"
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $LASTEXITCODE" }
    $apk = Get-ChildItem "$caseDir/publish" -Filter '*-Signed.apk'
    if ($apk.Count -ne 1) { throw 'Expected one signed APK' }
    python tools/check_apk_map.py $apk.FullName --manifest artifacts/belarus/manifest.json --output "$caseDir/report.json"
    if ($LASTEXITCODE -ne 0) { throw "Map verification failed: $LASTEXITCODE" }
}
```

Новая проверка `check_apk_map.py` выполняется также для скачиваемого country preview перед публикацией: потоково читает именно тот набор direct assets, который выбирает Android, отвергает пропуски/дубли/лишние gzip assets, сверяет manifest с проверенным источником и SHA-256 всей SQLite. JSON с APK identity/размером добавлен к checksums artifact. Это отдельный барьер после репаковки; исходная country validation сохранена.

## Повторная работа .NET tooling

Сравниваются три исходных вызова whitespace/style/analyzers и один `dotnet format CableRoutePlanner.Desktop.slnf --no-restore --verify-no-changes --severity warn` на SDK из global.json. Дополнительно проверяется ненулевой exit code и набор диагностик на временном примере с нарушениями whitespace, IDE0008 и fixable analyzer. [Результаты на Windows](benchmarks/ci-61-format.json): три повтора отдельных вызовов — 77,39 / 76,57 / 77,18 с, общий — 47,87 / 49,69 / 55,58 с. Медиана сократилась с 77,18 до 49,69 с (35,6%, около 27,5 с). На чистом дереве все exit codes 0; на диагностическом примере все exit codes 2. Общий вызов обнаруживает ENDOFLINE, WHITESPACE, IDE0008 и MA0002. Отдельные style/analyzers также сообщают IDE0055 для тех же пробелов: общий вызов исправляет их в памяти до запуска следующих проходов, поэтому JSON-списки ID буквально не равны. Проверены одинаковые категории нарушений и ненулевой exit code, а не буквальная идентичность диагностического текста. Внедрён один общий вызов в `tools/check.py`: локальные hooks и обе CI-платформы используют одну команду. Полная Release-сборка с анализаторами и warnings-as-errors сохранена.

Замер выполнен на одной Windows-машине при фоновой нагрузке; это не Linux-замер и не обещание того же процента на hosted runners. Для воспроизведения использовать SDK из `global.json`, locked restore и три чередующихся повтора каждой группы; `--report` сохраняет JSON диагностик. Во время отрицательного эксперимента временный C#-пример пересёкся с одним запуском hooks, вызвав ожидаемое падение сборки. Он удалён в `finally`, итоговые hooks выполняются после завершения эксперимента.

## Конвертация, кэши и цена эксперимента

Рецепт OSM → SQLite, LOD, R-tree и логика классификации не изменены. Ключ карты остаётся прежним. Пакетные вставки, перестройка индексов и упрощение геометрии не внедрены без профиля.

Opt-in `ci:performance-benchmark` запускает gzip-матрицу на одной и той же полной карте текущего PR, публикуя JSON с Python/zlib/платформой и точной идентичностью входа. Это исследовательский расход runner-minutes, не часть стандартной оптимизации. Та же метка (или отдельная `ci:apk-startup-benchmark` без повторения gzip-матрицы) включает на API 35 дополнительное открытие полной карты после единственной установки точного APK на свежий эмулятор (8G data partition). В JSON сохраняются APK SHA-256/размер, install_seconds, activity_start_seconds и map_ready_seconds; затем выполняется restart в том же эмуляторе. Обязательные install и pm-path проверки сохраняются. Map-ready — момент наблюдения существующего feature-ready marker при polling, включая adb overhead; это не timestamp отрисованного кадра. Второй запуск — restart с уже распакованной картой, не второй холодный эмулятор. 600 итераций ожидания — observation budget, не точный wall-clock deadline; job и отдельные adb-вызовы ограничены timeout.

Opt-in `ci:profile-country` выполняет независимую полную конвертацию с `cProfile` и `/usr/bin/time -v`. Сохраняет hotspots, CPU, peak RSS и профиль; после seal сравнивает gzip SQLite и manifest с обычной конвертацией. Инструментирование находится вне recipe: не меняет ключ карты, входы конвертера или публикуемый prepared package. Профилированное время нельзя сравнивать с обычным временем как ускорение.

### Полная конвертация: профиль и целостность

Country-profile job прошёл: независимый `cProfile`-запуск, `seal`, побайтовые `cmp` gzip SQLite и manifest с обычной конвертацией. Рецепт и ключ не изменены. [GNU time](benchmarks/ci-61-country-resources.txt): wall 31:01,47; user CPU 1867,95 с; system CPU 16,42 с; peak RSS 1 236 628 kbytes (около 1208 МиБ), exit 0. [Таблица профиля](benchmarks/ci-61-country-hotspots.txt): 995 727 692 вызова / 1076,556 учтённых секунд; это другая метрика, чем внешний wall. Профилированное время нельзя выдавать за время обычной конвертации (1050,05 с у obtain/prepare/verify шага).

| Наблюдение в профиле | Вызовы | Self, с | Inclusive, с |
| --- | ---: | ---: | ---: |
| add: геометрия, LOD, вставки | 4 623 547 | 39,154 | 646,140 |
| sqlite3 execute: features / R-tree / indexes | 13 557 984 | 112,277 | 112,277 |
| geometry simplify / LOD | 1 880 872 | 105,793 | 116,564 с wrapper |
| to_wkb | 6 505 712 | 79,904 | 103,198 |
| итоговое zlib compress | 2470 | 90,090 | 90,090 |
| SQLite commit | 464 | 7,734 | 7,734 |
| hash update | 3478 | 2,598 | 2,598 |

Inclusive строки вложены друг в друга, складывать их нельзя. В этом профиле отсутствуют полноценные entry frames main/apply_file/way/area; обработка pyosmium callbacks видна через вызываемые helpers. Поэтому он не доказывает отдельное wall-время чтения/сборки OSM вне callbacks. Основная измеренная работа распределена между tags/geometry/LOD/WKB/SQL, а не только сетью. SQL не единственный hotspot; обещать крупный выигрыш от executemany или перестройки индексов без нового A/B нельзя. Эти изменения, повторное использование геометрий и изменение LOD сознательно не внедрены: нет подтверждённого выигрыша с сохранением SQLite/геосемантики. Дополнительные таймеры последовательных фаз остаются дальнейшей исследовательской работой; данные профиля и побайтовая идентичность не подменяются предположениями.


Prepared cache использует только точный content-addressed key. Нет restore-keys, latest PBF, старой SQLite или bin/obj/SDK cache. Release prepared package остаётся долговременным источником, PR cache ограничен ref. Android workload cache, NuGet cache, Playwright/npm cache и кэш chunks оставлены без изменений: нет контролируемого положительного ROI; npm около 2 с не определяет критический путь. Перенос country data через artifact сохраняет изоляцию и возможность повторного запуска; объединение runners не внедрено.

## Быстрая итерация и окончательная проверка

На промежуточных коммитах разрешена существующая метка `ci:skip-full-maps`. Перед окончательным принятием удалить её или добавить `ci:full-maps`, дождаться полного зелёного workflow, обеих установок APK и нужного `ci:routing-benchmark`. Автоматической политики по изменённым путям нет. Benchmark/profile labels при skip-full-maps также требуют `ci:full-maps` (иначе geodata не запускается).

Неизменённые manual offline installers / publish workflow и Windows 7 x86 .NET 6 self-contained остаются отдельными проверками; отсутствие изменения файла не считается проверенным запуском платформы.

## Состояние доказательств

Локально все обязательные commit hooks (lint, desktop/tests/coverage, data, terrain) и Android pre-push gate прошли без обхода. Desktop: 933 tests, line coverage 95,9%, branch coverage 88,1%; Release и self-contained win-x64 publish успешны. Tooling после интеграции main, проверки APK, UTF-8 regression и Windows SDK isolation: 99 tests. Bash syntax проверен.

[PR #69](https://github.com/Volodya1000/maps/pull/69), исходный commit `62a72d811de57626e4d677bdb437e1cb4a6b83c5`: [CI run 46013430359](https://github.com/Volodya1000/maps/actions/runs/46013430359). Quality/data/terrain и обе desktop-платформы прошли; geodata и Android выполнялись одновременно, Minsk emulator прошёл. Cold country conversion заняла 1050,05 с, country elevation — 177,11 с, проверены 840 shards; передан artifact 1 829 356 415 байт. Exact-key кэши OSM и DEM сохранены. Full APK job упал в check_apk_elevation.py на Python 3.10 (`hashlib.file_digest` отсутствует), поэтому full-install/map-ready не заявлены как PASS. За время измерений в main появился исправляющий commit `5a3676e`; он включён в ветку. Оба исследовательских job затем завершились успешно; их результаты приведены выше. [Windows 7 x86 packaging run](https://github.com/Volodya1000/maps/actions/runs/46013391379) прошёл; это .NET 6 self-contained build/package verification на Windows 2022, не запуск на Windows 7 VM. Итоговое сравнение полного PR CI приведено в PR; сырые измерения компонентов сохранены рядом с отчётом.

Пока **не доказаны** контролируемая матрица холодного/тёплого кеша одинаковых SHA/рецептов, Linux/Windows NuGet ROI и фактический выигрыш install/startup. Исторические runs и модель графа не подменяют эти критерии. Issue #61 не следует считать полностью закрытым только по расчётной экономии.

При объединении обновлённого main Windows hook выявил locale-зависимое декодирование `git show` в changelog checker. Исправлен явный UTF-8 для Git stdout; регрессионный тест проходит для index и HEAD при отключённом Python UTF-8 mode. Проверка содержимого/staged snapshots сохранена. Это исправление локального quality gate, не ускорение.

## Обнаружение SDK на Windows

Исправленный baseline run 46023452314 остановился до проверки формата: SDK 8 вернул exit code 4 (`Unable to locate dotnet CLI`) в Windows job. Единственный диагностический rerun той же Windows job воспроизвёл ошибку; Linux и локальный Windows gate проходили. Это не провал форматирования или .NET tests. Аналогичная проблема описана в [dotnet/sdk #44957](https://github.com/dotnet/sdk/issues/44957).

Для Windows matrix SDK из неизменённого global.json устанавливается отдельно в runner.temp/dotnet через официальный [DOTNET_INSTALL_DIR](https://github.com/actions/setup-dotnet#environment-variables). Linux provisioning сохранён. Это проверяемый workaround: он не меняет SDK, analyzers, диагностики или команды tools/check.py и не добавляет ретраи к проверкам. В [baseline run 46028041238](https://github.com/Volodya1000/maps/actions/runs/46028041238) Windows job прошёл с SDK 8.0.427 из D:/a/_temp/dotnet: formatter, Release, 933 tests и coverage gate. Установка SDK заняла 16 с против 27 с у первого обычного Windows setup; это разные runs, а не доказательство ускорения provisioning. Повторная проверка выполняется на tuned candidate.

## Текущий рецепт: холодный и тёплый путь

В первом исследовательском run 46013430359 country geodata job занял 1262 с: obtain/prepare/verify OSM 1050,05 с, DEM 177,11 с; это cold exact-key miss с полной конвертацией. В baseline run 46028041238 тот же рецепт восстановлен по точным ключам OSM `belarus-v1-1e611665e188ff4d90b2d9f4e02e48ccf0bf51feaf0c79a180bfffa936678341` и DEM `country-elevation-v1-bdd01f928eee39ff9d03828c8ce458f3ffc9f25f99914d0f7484800869a86c00`: весь geodata job — 39 с, повторно проверены 840 shards. В обоих случаях передан artifact 1 829 356 415 байт. Это два последовательных наблюдения одного рецепта; cold run содержит исследовательские jobs и известный, позднее исправленный Python failure, поэтому полный wall этих двух runs нельзя выдавать за контролируемый A/B оптимизации.

Передача country artifact заняла 90 с в первом run и 37 с в warm baseline; этот шум сопоставим с установкой Android workload (29 с в обоих). Без повторного положительного A/B не добавлены кеши NuGet, workload или split assets и не объединены runners. Linux/Windows NuGet cache ROI остаётся неизмеренным; отсутствие нового cache не выдаётся за подтверждение отрицательного ROI.
