# Первый эксперимент дорожных пересечений

Предпросмотр GeoJSON обрезан окном аудита; полные исходные геометрии и журналы каждого сценария находятся в artifact запуска GitHub Actions. Локальный прогон сделан на рабочем дереве до коммита PR; `appCommit` обозначает базовый commit, `sourceDirty=true` и SHA бинарника указаны в result.json артефакта. После PR канонический повтор запускается на commit ветки.

Реальные OSM-данные Беларуси; отсутствие маршрута не доказывает физическую невозможность.

| ID | Категория | Дистанция, м | Результат | Время, мс | Альтернативы | Аудит |
| --- | --- | ---: | --- | ---: | ---: | --- |
| [naliboki-01](naliboki-01/map.svg) ([GeoJSON](naliboki-01/input.geojson), [route](naliboki-01/routes.geojson)) | B | 800 | success | 4093 | 1 | valid |
| [naliboki-02](naliboki-02/map.svg) ([GeoJSON](naliboki-02/input.geojson), [route](naliboki-02/routes.geojson)) | B | 10800 | no_route | 3568 | 0 | unknown |
| [naliboki-03](naliboki-03/map.svg) ([GeoJSON](naliboki-03/input.geojson), [route](naliboki-03/routes.geojson)) | B | 800 | success | 6676 | 2 | valid |
| [naliboki-04](naliboki-04/map.svg) ([GeoJSON](naliboki-04/input.geojson), [route](naliboki-04/routes.geojson)) | F | 500 | success | 5815 | 1 | valid |
| [pukhovichi-01](pukhovichi-01/map.svg) ([GeoJSON](pukhovichi-01/input.geojson), [route](pukhovichi-01/routes.geojson)) | A | 1800 | no_route | 3763 | 0 | unknown |
| [pukhovichi-02](pukhovichi-02/map.svg) ([GeoJSON](pukhovichi-02/input.geojson), [route](pukhovichi-02/routes.geojson)) | B | 10800 | no_route | 4984 | 0 | unknown |
| [pukhovichi-03](pukhovichi-03/map.svg) ([GeoJSON](pukhovichi-03/input.geojson), [route](pukhovichi-03/routes.geojson)) | B | 10800 | no_route | 6071 | 0 | unknown |
| [smolevichi-01](smolevichi-01/map.svg) ([GeoJSON](smolevichi-01/input.geojson), [route](smolevichi-01/routes.geojson)) | B | 800 | no_route | 3625 | 0 | unknown |
| [smolevichi-02](smolevichi-02/map.svg) ([GeoJSON](smolevichi-02/input.geojson), [route](smolevichi-02/routes.geojson)) | B | 12400 | no_route | 3863 | 0 | unknown |
| [smolevichi-03](smolevichi-03/map.svg) ([GeoJSON](smolevichi-03/input.geojson), [route](smolevichi-03/routes.geojson)) | F | 500 | success | 3285 | 1 | valid |

Принято: 10; отказы генератора: {'building_near_road': 29, 'unknown_terrain': 73, 'blocked_point': 12, 'short_road': 12, 'control_without_direct_witness': 3, 'distance': 38}.

Исходный OSM/SQLite SHA-256: `2e304811624b88bcbd664f7a78d3ee915bb37c5260396b5d7d72ce2dd9f154e6`.
Хеш фиксированного corpus: `2aea3e441f1fb48994e74b1814e4637f96fafee97d30a0310dca0a66bdfa48f1`.
Выполнено 10 сценариев: success 4, no_route 6, timeout 0, resource_limit 0, error 0, invalid_input 0.
Независимый аудит: проверенных допустимых альтернатив 5; результатов с неустановленной допустимостью/существованием пути 6.
Отсутствие маршрута и наличие OSM-кандидата не доказывают пропущенный допустимый путь. Инженерные разрешения не проверялись.

## Случаи для последующего разбора

- **naliboki-01** (B, success): (53.99351993, 26.55995077) → (53.99723227, 26.57043403); target `way/494186486`; кандидаты []. [Карта](naliboki-01/map.svg), [исходные данные](naliboki-01/input.geojson).
- **naliboki-03** (B, success): (53.89481015, 26.61551386) → (53.88879755, 26.60880954); target `way/491585575`; кандидаты []. [Карта](naliboki-03/map.svg), [исходные данные](naliboki-03/input.geojson).
- **naliboki-04** (F, success): (53.82429206, 26.57859177) → (53.82785602, 26.58323726); target `way/377367162`; кандидаты [1413755]. [Карта](naliboki-04/map.svg), [исходные данные](naliboki-04/input.geojson).
- **pukhovichi-01** (A, no_route): (52.86459644, 27.49287320) → (52.84979826, 27.50374060); target `way/440525556`; кандидаты [4605961]. [Карта](pukhovichi-01/map.svg), [исходные данные](pukhovichi-01/input.geojson).
- **smolevichi-03** (F, success): (53.87248266, 28.40172142) → (53.87640603, 28.40544803); target `way/172050318`; кандидаты []. [Карта](smolevichi-03/map.svg), [исходные данные](smolevichi-03/input.geojson).

Для A нужен независимо проверенный witness перед утверждением о пропущенном пути. Контрольные F имеют отдельно сохранённый прямой witness; его вершины не передавались маршрутизатору как пользовательские точки.

© OpenStreetMap contributors, ODbL 1.0.
