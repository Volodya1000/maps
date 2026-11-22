# Железные дороги, статистика и надписи ГАЗ

Основа: `feat/railway-rendering-and-routing`, c07dc81. В `main` на момент
начала работы железнодорожные типы отсутствовали. Правила проходимости,
штрафы, поиск пути и геометрическая постобработка не изменены.

## Причины визуальных дефектов

В прежнем стиле экранная ширина железной дороги составляла 1,4 пикселя при
разрешении >40 м/пиксель, 2,8 при 5–40 и 4,2 при <=5. Для сравнения, дорога
на обзорном масштабе имела ширину 0,65 пикселя. Детальный чёрно-белый стиль
включался уже при 40 м/пиксель. Это объясняет доминирование железных дорог.

Проверены исходники Mapsui 4.1.9 `LineStringRenderer`: `Pen.Width` напрямую
передаётся в Skia `StrokeWidth`, дополнительного умножения на разрешение нет.
У пера по умолчанию круглые стыки; причиной выбросов miter они не являются.
Круглые/квадратные окончания добавляли выступы к коротким сегментам.
Повторная отрисовка пересекающихся/дублированных сегментов могла закрывать
светлые вставки следующим тёмным основанием.

Теперь обзорный стиль — серый RGB(190,190,190), 0,55 пикселя при >20 м/пиксель
и 0,7 при 2–20. Детальный стиль включается только при <=2 м/пиксель:
тёмное основание 1,8 пикселя, светлые вставки 0,8. Окончания butt, стыки round.
Для отображения линии объединяются и сшиваются: дубликаты удаляются,
основания рисуются общим проходом перед вставками. Разные параллельные пути
сохраняются. Существующий порядок слоёв и тайловый механизм LOD сохранены.

Это подтверждено синтетическими рендер-тестами. Наличие конкретных дубликатов
в исходных данных Минска/Юратишек не подтверждено: полная карта и упомянутые
скриншоты в окружении задачи отсутствовали.

## Расчёт и подписи

`RouteMetrics.RailwayCrossings` рассчитывается в `RouteAlternativeBuilder`
по окончательной геометрии каждого принятого варианта и выводится существующим
`RouteMetricsFormatter`. Анализатор использует исходную проекцию и пространственные
индексы подготовленной области. Совпадающие контакты объединяются по станции
маршрута, повторные проходы сохраняются; продолжения исходных путей отличают
касания от пересечений. В узлах каждый физический луч используется однократно,
включая общие участки стрелок. Концы маршрута и движение вдоль путей исключаются.

ГАЗ вынесен из растровой подложки в существующий слой аннотаций. Общий стиль
надписи один раз превращается в bitmap-символ; `SymbolStyle.RotateWithMap=true`
передаёт поворот и позиционирование самому Mapsui. Мосты и подземные переходы
сохраняют прежние экранные LabelStyle; платформенная логика не дублируется.

## Автоматические сценарии

| Requirement | Evidence |
| --- | --- |
| Тонкие серые линии, граница LOD | `OverviewTracksAreGreyAndNoWiderThanRoads`, `DetailHasBoundedDarkBedAndWhiteInserts` |
| Плотные пути, стыки, дубли | `DenseOverviewYardAndAcuteJoinsNeverProduceDarkPatches`, `DuplicateAndSplitWaysProduceSamePixelsWithoutJoinBlobs` |
| Несколько путей, повторные проходы | `ParallelTracksCountSeparatelyIncludingBridgeAndTunnel`, `RepeatVisitsCountEvenAtSameCoordinate` |
| Касания, движение вдоль, концы | `TouchSharedSectionAndEndpointsAreNotCrossings`, `RailwayEndpointTouchDoesNotCount`, `BentTrackTangencyDoesNotCountButPassageDoes` |
| OSM-сегменты и дубликаты | `SplitAndReversedOverlappingDuplicatesCountOnceAtRouteVertex`, `DuplicateMultiLineAndSplitWaysCountOnce` |
| Стрелки и узлы | `SharedIncomingTrackAtForkCountsOnceRegardlessOfOsmSegmentation`, `MixedWholeAndSplitSwitchWaysPreserveCrossingCount`, `OpposingTangenciesAtOneNodeDoNotBecomeCrossings`, `SharedSpurDoesNotHideSeparateThroughTrack` |
| Замкнутые пути и малые изломы | `ClosedTrackSeamCountsAsInteriorCrossing`, `ShortBentTrackTouchAndNearbyCrossingCountOnlyTheCrossing` |
| Перестроение и альтернативы | `PlannerRecalculationPublishesNewCount`, `AlternativesHaveIndependentCounts` |
| Отмена | `CancelledAnalysisDoesNotReturnPlausibleStatistics` |
| ГАЗ: поворот/привязка, 0/45/90/180/270/317° | `GasSymbolRotatesWithMapAndKeepsWorldAnchor`, `OfflineProviderKeepsGasInRotatingOverlayAndOutOfRasterSource` |
| Прежние подписи мостов/переходов | `BothAnnotationTypesRenderHorizontallyAndFollowTheirWorldAnchor`, `RealOfflineTilesKeepBothLabelsReadableOnProletarskayaMap` |

## Итог проверки

Полный прогон: **744 passed, 0 failed, 0 skipped**, Release, анализаторы без
предупреждений. Cobertura: строки **95,8%** (порог 85%), ветви **88,1%** (порог 80%).
Покрытие по проектам: Domain 99,4%, Application 96,5%, ViewModels 92,3%,
Infrastructure 98,7%, Persistence 98,0%, UI 95,3%.

- `uv run --locked pre-commit run --all-files`: changelog, desktop, data и terrain
  прошли; source-lint сначала остановился на требовании теста установщика иметь
  4 ГиБ свободного диска после установки инструментов проверки.
- После удаления созданных этой сессией кешей/архивов и дублирующей установки Go
  `uv run --locked pre-commit run source-lint --all-files` прошёл. Код и правила
  проверок для обхода ограничения не менялись; свободно 4,1 ГиБ.
- Desktop hook вызывает штатный `tools/check.py desktop`: locked restore,
  форматирование, анализаторы, Release build с предупреждениями как ошибками,
  полный набор xUnit, coverage gate и Linux-аудит автономности — PASS.
- `git diff --check`, проверка changelog-фрагмента — PASS.
- Результат последнего полного прогона: `artifacts/tests/_0067c12c8b20_2026-11-22_06_54_56.trx`.

GitHub Actions из этой сессии не запускались. Изменения подготовлены локально.
Android/Windows на устройствах здесь не проверены. Android gate останавливается
из-за отсутствующих Android SDK и .NET Android workload.
