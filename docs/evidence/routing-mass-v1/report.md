# Массовый baseline маршрутизации Беларуси

Зафиксировано и выполнено 88 пар в шести районах: 10 существующих пилотных и 78 новых. Каждая пара содержит две пользовательские точки. Целевая выборка проверяет барьеры и не оценивает случайную поездку по стране.

| Показатель | Значение |
| --- | ---: |
| Допустимый маршрут | 32/88 (36.4%) |
| NoRoute | 56/88 (63.6%) |
| Нарушение правил | 0/88 |
| Подтверждённый путь найден | 15/15 |
| Подтверждённый путь пропущен | 0 |
| Использование переходов среди допустимых маршрутов | 37.5% |
| Время p50 / p95 | 3818 / 5674 мс |

## Классификация

Типизированные причины: `{"NoRouteInSearchArea": 56}`. NoRoute без проверенного witness остаётся неопределённым, а не дефектом A*.
Успешно использованные переходы: `{"bridge": 12}`. Числа обозначают сценарии, где аудит нашёл соответствующий контакт.

| Группа | Результаты |
| --- | --- |
| area:brest | {"no_route": 9, "success": 4} |
| area:gomel | {"no_route": 7, "success": 6} |
| area:naliboki | {"no_route": 11, "success": 5} |
| area:pukhovichi | {"no_route": 10, "success": 6} |
| area:smolevichi | {"no_route": 9, "success": 8} |
| area:vitebsk | {"no_route": 10, "success": 3} |
| category:A | {"no_route": 22, "success": 10} |
| category:B | {"no_route": 17, "success": 6} |
| category:E | {"no_route": 17, "success": 2} |
| category:F | {"success": 14} |
| type:bridge | {"no_route": 21, "success": 9} |
| type:control | {"success": 12} |
| type:multi | {"no_route": 16, "success": 2} |
| type:no_candidate | {"no_route": 13, "success": 5} |
| type:pilot | {"no_route": 6, "success": 4} |

## Исходные OSM и подготовленная база

Сверены закреплённые PBF и SQLite по SHA-256 ([машинный инвентарь](source-inventory.json)). Категории OSM ways пересекаются и не означают разрешение кабельных работ.

| Тип | OSM ways | Найдено в подготовленных слоях |
| --- | ---: | --- |
| road_bridge | 10837 | 10837 BridgeCandidate |
| pedestrian_bridge | 4288 | 4286 BridgeCandidate |
| railway_bridge | 1263 | 8 BridgeCandidate |
| road_tunnel | 2508 | 1 BridgeCandidate, 2506 Road |
| pedestrian_tunnel | 2739 | 2 BridgeCandidate |
| railway_tunnel | 259 | 0 в сравниваемых слоях |
| gas_pipeline_overhead | 67 | 67 OverheadGasPipeline |
| gas_pipeline_other | 847 | 0 в сравниваемых слоях |
| bridge_structure | 1070 | 0 в сравниваемых слоях |
| covered_highway | 2791 | 20 BridgeCandidate, 2186 Road |
| nonzero_layer | 96983 | 13879 BridgeCandidate, 3 OverheadGasPipeline, 1478 Road |
| ford | 195 | 10 BridgeCandidate, 112 Road |

В SQLite: 15 123 BridgeCandidate, 67 OverheadGasPipeline и 12 порождённых GasPipelineCrossingCandidate. Числа в третьем столбце показывают присутствие исходного way в указанном слое, а не пригодное пересечение дороги. Два pedestrian_tunnel с BridgeCandidate имеют также мостовой тег: отдельной поддержки тоннелей нет. `bridge=*` на highway импортируется, включая пешеходные мосты; железнодорожные мосты, самостоятельные `man_made=bridge`, `layer=*` и иные газопроводы не становятся переходами автоматически. Часть дорожных тоннелей хранится как Road. Нужна отдельная проверка геометрии пересечения дорог и предметных правил перед расширением Domain.

## Доказательные случаи и следующие изменения

- [Пуховичи, газовый переход](cases/pukhovichi-01/diagnostic.json): кандидат `4605961` пересекает целевую дорогу и лежит в области поиска, но прямая даёт девять запрещённых дорожных контактов и пересекает воду; `NoRouteInSearchArea`, существование обхода не доказано. [Геометрия](cases/pukhovichi-01/geometry.geojson), [карта](cases/pukhovichi-01/map.svg).
- [Налибоки, мост](cases/mass-naliboki-bridge-01/diagnostic.json): кандидат `438476` пересекает целевую дорогу в области поиска, в 66 м от прямой. Прямая также встречает воду и болото; witness нет. [Геометрия](cases/mass-naliboki-bridge-01/geometry.geojson), [карта](cases/mass-naliboki-bridge-01/map.svg).
- [Брест, последовательные барьеры](cases/mass-brest-multi-06/diagnostic.json): прямая даёт 10 дорожных контактов и касается запрещённой местности; модельный кандидат для целевой дороги не выбран. Отказ поиска не доказывает дефект A*. [Геометрия](cases/mass-brest-multi-06/geometry.geojson), [карта](cases/mass-brest-multi-06/map.svg).

Новые сценарии A отобраны по пересечению геометрии кандидата и Road; пригодность контакта и подключение к графу не доказаны. Число реальных пересечений дорог для неподдерживаемых OSM-типов пока unknown; приведённые числа ways задают верхнюю границу для дальнейшей проверки.

Первый приоритет: независимо построить и проверить witness для одного из 21 неуспеха в новой мостовой страте, затем проверить фактический шаг сетки, портал и его соединители. Это отделит дефект поиска от отсутствия допустимого пути. Следом проверить 16 неуспехов с последовательными барьерами. Для расширения покрытия данных отдельно сопоставить геометрию пешеходных тоннелей, железнодорожных мостов и иных сооружений с дорогами и получить предметное разрешение; OSM-теги сами по себе разрешения не дают. Подтверждённого локального дефекта в этом baseline нет, поэтому production-код не менялся и сравнение «до/после» неприменимо.

План составлял 100 пар; фактически 88. Все 12 недобранных слотов приходятся на газовую страту: в шести выбранных районах не удалось подобрать дополнительные пары с известным типом местности и допустимыми конечными точками. Ни один неуспех не исключён после заморозки.

## Воспроизводимость и пределы

Корпус SHA-256 `025262ab54548df41a43b71d230d003151b6e03b2541fec72a9b963e45e0634d`; SQLite `2e304811624b88bcbd664f7a78d3ee915bb37c5260396b5d7d72ce2dd9f154e6`; PBF `4463a38a3b68819c46fd9f89fc286a8f98afec480aff48d5240222254e45ec01`; приложение `03ea54850df3ce0f16f38151769196373f427cdf`.
Воспроизведение из корня репозитория после установки `uv` и .NET SDK из `global.json`:
```sh
uv sync --project tools/GeodataPreparation --locked
uv run --project tools/GeodataPreparation --locked python tools/GeodataPreparation/country_data.py ensure --output artifacts/belarus
uv run --project tools/GeodataPreparation --locked python tools/ci/routing_pilot_ci.py extract-db --data artifacts/belarus
dotnet build tools/CableRoutePlanner.Verify -c Release -warnaserror
uv run --project tools/GeodataPreparation --locked python tools/routing_mass_experiment.py freeze --data artifacts/belarus --output artifacts/routing-mass
uv run --project tools/GeodataPreparation --locked python tools/routing_mass_experiment.py run --data artifacts/belarus --verify-dll tools/CableRoutePlanner.Verify/bin/Release/net8.0/CableRoutePlanner.Verify.dll --output artifacts/routing-mass
uv run --project tools/GeodataPreparation --locked python tools/GeodataPreparation/country_data.py source --output artifacts/osm/belarus-261030.osm.pbf
uv run --project tools/GeodataPreparation --locked python tools/routing_source_inventory.py --pbf artifacts/osm/belarus-261030.osm.pbf --data artifacts/belarus --output artifacts/routing-source-inventory.json
mkdir -p artifacts/routing-mass-report && cp artifacts/routing-source-inventory.json artifacts/routing-mass-report/source-inventory.json
uv run --project tools/GeodataPreparation --locked python tools/routing_mass_diagnostics.py --run artifacts/routing-mass --output artifacts/routing-mass-report/cases mass-naliboki-bridge-01 pukhovichi-01 mass-brest-multi-06
uv run --project tools/GeodataPreparation --locked python tools/routing_mass_report.py --run artifacts/routing-mass --inventory artifacts/routing-source-inventory.json --output artifacts/routing-mass-report
```
Один и тот же Verify DLL SHA-256 `f466f79ed5b9d9f073896b537af2d9d1ce3adcea6089fee26bb4be65e09bcc58` использован во всех 88 запусках. `sourceDirty=true` записано в 33 результатах после изменения workflow во время запуска; бинарник и commit приложения не менялись. Поле сохранено для каждого случая в JSON.
[Все случаи, координаты, аудит и причины](results.json) сохранены в машинном виде. Полные GeoJSON, SVG, stdout/stderr и результаты каждого процесса остаются в artifacts запуска.
Пиковый RSS и фактический шаг сетки не инструментированы и записаны как null. Инженерная пригодность переходов не подтверждалась. © OpenStreetMap contributors, ODbL 1.0.
