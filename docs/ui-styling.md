# Оформление Avalonia

UI приложения находится в `src/CableRoutePlanner.UI`. Разметка экранов (`Views/*.axaml`, `Controls/*.axaml`) определяет структуру, привязки, команды, автоматизацию и обработку взаимодействий. Общие визуальные свойства вынесены в отдельные файлы.

| Файл | Ответственность |
| --- | --- |
| `Resources/Brushes.axaml` | Кисти текущей светлой палитры |
| `Styles/Common.axaml` | Базовые кнопки, текст и повторяемая типографика |
| `Styles/Shell.axaml` | Шапка и предупреждение об изменениях |
| `Styles/Map.axaml` | Панели карты, компас, выбор маршрута, статус |
| `Styles/WaypointList.axaml` | Карточки точек и состояния выбора/перетаскивания |
| `PlannerApplication.axaml` | Единая точка подключения ресурсов и стилей |

`PlannerApplication.axaml` подключает кисти через `Application.Resources` → `ResourceDictionary.MergedDictionaries` → `ResourceInclude`, стили через `Application.Styles` → `StyleInclude`. Адрес `avares://CableRoutePlanner.UI/...` указывает на ресурс в UI-сборке.

В разметке элементу задаётся класс, например `Classes="primary"`. Селектор `Button.primary` в `Styles/Common.axaml` назначает визуальные свойства всем соответствующим кнопкам. Состояния `selected`, `dragging`, `drop-target` обрабатываются отдельными селекторами, а их изменение остаётся в биндингах и обработчиках контрола.

Кисти в стилях получают через `{DynamicResource AppAccentBrush}`: ресурс разрешается после подключения словарей и может обновляться в рантайме. Это также устраняет зависимость от порядка загрузки соседних `StyleInclude`. При добавлении новых вариантов темы предпочтительнее `ResourceDictionary.ThemeDictionaries` с теми же ключами.

Не переносите в стили свойства с `Binding`, имена элементов `x:Name`, команды и события, структуру `Grid`, управление размерами на мобильных устройствах из code-behind. Чистый HTML/CSS пример находится отдельно в `spikes/MapLibre`; он не является частью Avalonia UI. Стили картографических объектов Mapsui заданы в коде отдельного адаптера.

**Проверки:** `uv run --locked python tools/check.py lint`, `uv run --locked python tools/check.py desktop`, `uv run --locked python tools/check.py android` (последняя требует Android SDK). UI headless-тест проверяет применённые размеры и цвета; проверяйте также жесты списка точек и компас на Android после изменений визуального оформления.
