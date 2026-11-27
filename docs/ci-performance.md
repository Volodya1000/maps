# Измерения CI — issue #61

## Методика и исходная точка

[Issue #61](https://github.com/Volodya1000/maps/issues/61) требует измерений до выбора настроек. Исходная ревизия этого PR — `7c4bec1b2fb5dbb5a51439260c93385a872d6ce2`.
[Машинные данные baseline](benchmarks/ci-61-baseline.json) содержат SHA, timestamps jobs и длительности успешных подшагов из публичного Actions API. Повторить получение: `GET /repos/Volodya1000/maps/actions/runs/<run-id>/jobs?per_page=100`; при большем числе jobs следует продолжить пагинацию.
Wall-clock здесь — от начала первого job до конца последнего; runner-minutes — сумма длительностей успешных jobs, без тарифных коэффициентов Windows, очереди и округления биллинга. Это исторические наблюдения на разных SHA/runner images, а не контролируемый A/B одного коммита. CPU/RAM старые логи не собирали; эти значения неизвестны. Три наблюдения неизменного опубликованного рецепта подтверждают cold-cache Release download, три — полную конвертацию изменённого рецепта, два — exact-key hit последующих неизменных запусков. Для published recipe PR cache намеренно не сохраняется: его повторный cold Release download нельзя называть повторной конвертацией.

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

Первый полный локальный проход (повторный проход ещё выполняется):

| Level / workers | Сжатие, с | CPU, с | Peak RSS, МиБ | Gzip, байт | Чтение + hash, с |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1 / 1 | 102,72 | 94,03 | 151,84 | 1 234 963 519 | 25,68 |
| 3 / 1 | 112,31 | 105,56 | 151,94 | 1 232 408 278 | 24,50 |
| 6 / 1 | 179,71 | 169,36 | 151,16 | 1 211 703 118 | 23,74 |
| 9 / 1 | 404,54 | 395,91 | 151,26 | 1 210 638 768 | 25,18 |

Level 6 в первом проходе сократил сжатие на 224,83 с при росте gzip на 1 064 350 байт (0,088%). Это ещё не доказательство для APK/install на GitHub. Итоговые настройки будут выбраны после повторов и Android-проверки; первый PR CI сохраняет level 9 / один worker как исходную точку.

Gzip `mtime=0`, размеры распакованных chunks 64 МиБ и последовательные имена сохраняются. Число workers ограничено 1–4; очередь удерживает не больше workers chunks. zlib-компрессия выполняется в threads без копирования блоков между процессами. Тесты сравнивают gzip-байты при 1/2/4 workers, восстанавливают SQLite на всех уровнях и проверяют ошибки ввода/записи.

## Повторная работа .NET tooling

Сравниваются три исходных вызова whitespace/style/analyzers и один `dotnet format CableRoutePlanner.Desktop.slnf --no-restore --verify-no-changes --severity warn` на SDK из global.json. Дополнительно проверяется ненулевой exit code и набор диагностик на временном примере с нарушениями whitespace, IDE0008 и fixable analyzer. [Результаты на Windows](benchmarks/ci-61-format.json): три повтора отдельных вызовов — 77,39 / 76,57 / 77,18 с, общий — 47,87 / 49,69 / 55,58 с. Медиана сократилась с 77,18 до 49,69 с (35,6%, около 27,5 с). На чистом дереве все exit codes 0; на диагностическом примере все exit codes 2. Общий вызов обнаруживает ENDOFLINE, WHITESPACE, IDE0008 и MA0002. Отдельные style/analyzers также сообщают IDE0055 для тех же пробелов: общий вызов исправляет их в памяти до запуска следующих проходов, поэтому JSON-списки ID буквально не равны. Проверены одинаковые категории нарушений и ненулевой exit code, а не буквальная идентичность диагностического текста. Внедрён один общий вызов в `tools/check.py`: локальные hooks и обе CI-платформы используют одну команду. Полная Release-сборка с анализаторами и warnings-as-errors сохранена.

Замер выполнен на одной Windows-машине при фоновой нагрузке; это не Linux-замер и не обещание того же процента на hosted runners. Для воспроизведения использовать SDK из `global.json`, locked restore и три чередующихся повтора каждой группы; `--report` сохраняет JSON диагностик. Во время отрицательного эксперимента временный C#-пример пересёкся с одним запуском hooks, вызвав ожидаемое падение сборки. Он удалён в `finally`, итоговые hooks выполняются после завершения эксперимента.

## Конвертация, кэши и цена эксперимента

Рецепт OSM → SQLite, LOD, R-tree и логика классификации не изменены. Ключ карты остаётся прежним. Пакетные вставки, перестройка индексов и упрощение геометрии не внедрены без профиля.

Opt-in `ci:performance-benchmark` запускает gzip-матрицу на одной и той же полной карте текущего PR, публикуя JSON с Python/zlib/платформой и точной идентичностью входа. Это исследовательский расход runner-minutes, не часть стандартной оптимизации. Та же метка включает на API 35 дополнительное открытие полной карты после единственной установки точного APK на свежий эмулятор (8G data partition). В JSON сохраняются APK SHA-256/размер, install_seconds, activity_start_seconds и map_ready_seconds; затем выполняется restart в том же эмуляторе. Обязательные install и pm-path проверки сохраняются. Map-ready — момент наблюдения существующего feature-ready marker при polling, включая adb overhead; это не timestamp отрисованного кадра. Второй запуск — restart с уже распакованной картой, не второй холодный эмулятор. 600 итераций ожидания — observation budget, не точный wall-clock deadline; job и отдельные adb-вызовы ограничены timeout.

Opt-in `ci:profile-country` выполняет независимую полную конвертацию с `cProfile` и `/usr/bin/time -v`. Сохраняет hotspots, CPU, peak RSS и профиль; после seal сравнивает gzip SQLite и manifest с обычной конвертацией. Инструментирование находится вне recipe: не меняет ключ карты, входы конвертера или публикуемый prepared package. Профилированное время нельзя сравнивать с обычным временем как ускорение.

Prepared cache использует только точный content-addressed key. Нет restore-keys, latest PBF, старой SQLite или bin/obj/SDK cache. Release prepared package остаётся долговременным источником, PR cache ограничен ref. Android workload cache, NuGet cache, Playwright/npm cache и кэш chunks оставлены без изменений: нет контролируемого положительного ROI; npm около 2 с не определяет критический путь. Перенос country data через artifact сохраняет изоляцию и возможность повторного запуска; объединение runners не внедрено.

## Быстрая итерация и окончательная проверка

На промежуточных коммитах разрешена существующая метка `ci:skip-full-maps`. Перед окончательным принятием удалить её или добавить `ci:full-maps`, дождаться полного зелёного workflow, обеих установок APK и нужного `ci:routing-benchmark`. Автоматической политики по изменённым путям нет. Benchmark/profile labels при skip-full-maps также требуют `ci:full-maps` (иначе geodata не запускается).

Неизменённые manual offline installers / publish workflow и Windows 7 x86 .NET 6 self-contained остаются отдельными проверками; отсутствие изменения файла не считается проверенным запуском платформы.

## Состояние доказательств

Локальный исходный desktop gate: 933 tests, line coverage 95,9%, branch coverage 88,1%; Release и self-contained win-x64 publish успешны. Tooling после первых изменений: 83 tests. Финальная проверка и измеренный PR CI будут дополнены отдельно.

Пока **не доказаны** контролируемая матрица холодного/тёплого кеша одинаковых SHA/рецептов, Linux/Windows NuGet ROI и фактический выигрыш install/startup. Исторические runs и модель графа не подменяют эти критерии. Issue #61 не следует считать полностью закрытым только по расчётной экономии.
