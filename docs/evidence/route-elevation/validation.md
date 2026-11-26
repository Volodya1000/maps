# Проверка PR с высотными профилями

Область: профиль выбранной альтернативы, WGS84/поверхностная длина, запас кабеля, офлайн-DEM, сохранение расчёта, Desktop/Android. Поиск A*, стоимость, ограничения, прежняя LengthMetres и GPS-прогресс сохраняют прежние правила. База интеграции — main 68555c3 (включая сохранённые расчёты из PR #63).

## Наблюдаемые требования

| Требование | Проверка |
|---|---|
| Ровный участок, склон, внутренний холм и однократный запас | TerrainLengthUsesWgs84HorizontalAndAppliesReserveOnce; InteriorHillIncreasesLengthBetweenEqualEndpoints; LengthCorrectionUsesOneHorizontalBasisAndReserveUsesTheApplicableLengthOnce |
| Все вершины и обратное направление | IntermediateVerticesRemainInProfileAndWgs84LengthFollowsTheBend; ReverseTraversalKeepsLengthAndSwapsAscentAndDescent |
| Пропуски остаются неизвестными, конструктивные переходы используют подписанную 2D-основу | NoDataDoesNotBridgeGapOrProduceWholeRouteTotals; StructuralCrossingKeepsSurfaceProfileButUsesLegacyCableBase |
| Изменение точек и отмена не публикуют старый анализ | PointEditWhileElevationIsPendingRejectsStaleRoutes; CancellationWhileElevationIsPendingRejectsCompletedAnalysis |
| Выбор альтернативы, очистка, курсор карты, клавиатура и прокрутка телефона | AlternativeChangeAndPointEditReplaceProfileAndRemoveOldMapCursor; PhoneWidthFitsTheChartAndVerticalSwipeOverItScrollsWithoutMovingTheCursor |
| Настоящий DEM и независимый географический эталон | DzerzhinskayaNativeDemMatchesIndependentWgs84SurfaceReference; [числа, источник и воспроизведение](README.md) |
| Родные пиксели и швы блоков/широтных зон | InterpolationReadsAcrossBlockAndGridSeamsOnTheNativeAffine; LatitudeBandSeamUsesEachNativeRowAtTheRequestedLongitude |
| Проверяются ровно читаемые байты; отмена освобождает временный снимок | HeightsComeFromTheSameOpenedPackageBytesThatPassedChecksum; CancellationDuringChecksumReadingAllowsVerificationToRetry; ConfiguredTemporaryDirectoryIsUsedAndCleared |
| Android обновляет DEM независимо от OSM и сохраняет внешний пакет | ChangedElevationIsInstalledEvenWhenMapIdentityDoesNotChange; CorruptedInstalledBytesAreRepairedWhenTheIdentityMarkerStillMatches; RemovingBundledElevationPreservesAnUnmanagedPackage |
| Подготовка сохраняет пиксели, отвергает явные неметрические единицы и восстанавливает старый пакет при ошибке публикации | test_crop_preserves_native_affine_float_samples_and_nodata; test_rejects_explicit_feet_without_replacing_existing_package; test_failed_checksum_publication_preserves_existing_package; test_failed_rollback_reports_recovery_copy_of_previous_database |
| Сохранение/открытие всех профилей и прежних настроек без нового анализа, чтение v1 | RoundTripPreservesEveryAlternativeAndOrderedUserPoints; SaveAndOpenCommandsRestoreAllVariantsPointsSelectionAndFrozenMetricsAfterRestart; VersionOneRecordStillOpensAndSurvivesAddingAProfile |
| Противоречивая геометрия/статистика не заменяет текущий маршрут или файл | SavingChangedGeometryRejectsSamplesFromThePreviousPath; SavedStationsMustFollowEachSegmentAndIncludeVerticesInOrder; InvalidElevationSnapshotIsReportedAndCannotReplaceStorage; MissingSnapshotMembersAreRejectedWithoutReplacingWorkingRoute |

Для исправлений review наблюдались отдельные RED→GREEN, включая семь случаев подмены геометрии перед окончательным GREEN 130 тестов сохранения/высот. Итоговый независимый review не оставил открытых существенных замечаний. Все 18 исторических routing golden-файлов сохранены: тестовый JSON-проектор оставляет прежние десять полей метрик, новые физические величины проверяются отдельными тестами.

## Дополнительная регрессия карты

Повторный полный gate обнаружил существующую гонку кеша Mapsui: перекрывающиеся завершения фоновых запросов могли оставить две копии газового символа. RepeatedGasMarkersFromOverlappingFetchesKeepDistinctObjects воспроизводит обе ситуации (разные и совпадающие координаты): до исправления RED 2/2, после GREEN в наборе 35 тестов газовых знаков и аннотаций. Символы дедуплицируются по ID объекта, отдельные входы подземных переходов сохраняются; исходный Assert.Single не ослаблен. Узкий независимый review подтвердил исправление.

RED: задержанный вариант выявил отсутствие HasRoute; GREEN: 16 связанных тестов, без пропусков. Перестановка точек во время отмены также проверяется с принудительно задержанными уведомлениями ReactiveUI: ReorderingDuringCalculationCancelsAndBuildsLatestOrder сохраняет проверки порядка, наличия маршрута и координат концов. Автоматическое перестроение использует состояние сессии и сервиса, а не запаздывающую проекцию доступности кнопки.

## Локальные проверки

Окончательные результаты записываются после штатных hooks; команды и критерии совпадают с docs/development.md. Анализаторы, coverage gate и hooks не отключались.

| Команда / сценарий | Результат |
|---|---|
| uv run --locked pre-commit run --all-files | PASS: все пять применимых hooks |
| tools/check.py lint / desktop / data / terrain | PASS: 890 .NET-тестов, без пропусков; покрытие 95,8% строк / 88,1% ветвей; win-x64 publish; 84 Python data-теста; браузер без внешних запросов |
| uv run --locked pre-commit run --all-files --hook-stage pre-push | PASS: Android Release APK, версия, офлайн-карта, отсутствие INTERNET |
| tools/Windows7Compatibility/build-win7-x86.ps1 | PASS: locked restore и self-contained net6.0/win-x86 publish; запуск на Windows 7 не проверен |
| Настоящие MapView/Styles/Mapsui, Skia 1100×760 и 314×850 | PASS: [снимки](README.md#отрисовка-интерфейса) |
| DEM внутри подписанной APK | PASS: assets/Data/elevation.sqlite и sidecar, SHA-256 совпадает с исходным fixture |
| Большой DEM: сравнительное время и память | NOT VERIFIED: полноценный пакет страны не измерялся; потоковая копия и ограниченный кеш описаны в руководстве |
| Физический Android / жесты и установка на устройстве | NOT VERIFIED: устройство/рабочий эмулятор не использовались |
| Linux runtime audit и вторая ОС | NOT VERIFIED локально: среда Windows; независимая проверка остаётся в CI |

Domain использует только BCL. Семантика высот и применимость длины находятся в Domain; анализ окончательной геометрии — в Infrastructure, чтение DEM — в Persistence, регистрация конкретных реализаций — в Composition Root. Новых зависимостей и скопированного кода OsmAnd нет. Добавлен один фрагмент changes/65.feature.md; CHANGELOG.md не изменён.

В Git входят только два ограниченных DEM-fixture по 77 824 байта и снимки интерфейса; полный TIFF, карта, SDK и APK остаются вне Git. Стандартное покрытие DEM — маленький район Минска. Для всей Беларуси требуется отдельно подготовленный пакет. Проверка целостности создаёт временный дисковый снимок пакета на каждую непустую выборку; затраты и ограничения описаны в [руководстве](../../route-elevation.md).
