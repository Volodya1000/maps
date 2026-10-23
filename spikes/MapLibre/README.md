# Исследование MapLibre GL JS

Это отдельный проверочный стенд, **не встроенный 3D-режим Avalonia**. Он использует MapLibre GL JS 4.7.1 и настоящие локальные DEM-тайлы Copernicus, без hillshade и без собственного 3D-движка.

```sh
python spikes/MapLibre/serve.py
# открыть http://127.0.0.1:8765
```

Сервер привязан только к 127.0.0.1; Ctrl-C закрывает его. JS, CSS, OSM и DEM включены локально. CSP запрещает внешние ресурсы и шрифты; внешних тайлов и CDN нет. NOTICE.txt и vendor/LICENSE.txt содержат уведомления. Хеши vendor-файлов и terrain-тайлов — в манифестах.

`window.cableMap.applySnapshot({ points, routes, selectedId })` принимает снимок географического состояния. Маркеры draggable, изменение передаётся событием pointMoved, нажатие — pointAdded; удаление выполняется следующим снимком. LineString рисуется поверх DEM. `set3D(bool)` меняет terrain и наклон. JS содержит hooks chrome.webview.postMessage / cableNative.postMessage, но **реальный C# host отсутствует**.

Автоматическая проверка, при работающем сервере:

```sh
cd spikes/MapLibre
npm ci
npx playwright install chromium
npm test
```

Отчёт и screenshot — в docs/evidence. Локально проверено на Linux headless Chromium 140 с программным WebGL. Включение terrain и различие высот проверяются через API, а не только картинкой. В MapLibre 4.7.1 queryTerrainElevation возвращает высоту **относительно центра карты**, поэтому нулевое значение в центре и отрицательные значения рядом не означают нулевой исходный DEM. Исходные COG-значения в трёх контрольных точках около 219.8 / 206.4 / 213.1 м. Скриншот содержит тестовый LineString для проверки обмена; это не объявляется рассчитанным инженерным маршрутом.

Матрица исследования:

| Проверка | Результат |
|---|---|
| Локальный raster-dem, реальное изменение высоты, наклон/поворот | подтверждено в Chromium Linux |
| GeoJSON LineString, добавление/удаление маркеров, JS-событие перемещения | подтверждено браузерным тестом |
| Запрет внешних HTTP-запросов | тест перехватывает и отклоняет все внешние URL; запросов не было |
| Перетаскивание пальцем / мышью | поддерживается MapLibre; нативные устройства ещё не проверены |
| Обмен с production C# ViewModel | **не реализован** |
| Встраивание в Avalonia, lifecycle loopback server | **не реализовано** |
| Windows 10 / Android GPU и WebView | **не проверено** |
| Windows 7 | блокер runtime/WebView2 109, экспериментов на ОС нет |
| Лицензия host WebView | **host не выбран** |

Для Avalonia 11 встроенный Accelerate WebView требует отдельной проверки лицензии. Avalonia объявила [открытие WebView в ветке 12](https://avaloniaui.net/blog/the-avalonia-webview-is-going-open-source), но переход потребует новой проверки всей комбинации Avalonia/ReactiveUI/Fody и платформ. Другой вариант — отдельные платформенные адаптеры WebView2 / Android WebView с общей JS-частью. Старые WebView2 109 / runtime для Windows 7 не приняты как решение. До C#-интеграции и целевых испытаний FR-007 остаётся незакрытым.
