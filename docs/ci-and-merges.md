# Запуски CI и слияние PR

Workflow [Build and verify](../.github/workflows/ci.yml) запускается по трём событиям:

| Событие | Когда запускается |
| --- | --- |
| `pull_request` | PR открыт, обновлён новым commit или переоткрыт; проверяется предлагаемое содержимое PR |
| `push` в `main` | Изменился commit основной ветки, в том числе после merge PR в `main` |
| `workflow_dispatch` | Запуск вручную из GitHub Actions |

Слияние PR в промежуточную ветку не является push в `main`. Workflow намеренно слушает push только основной ветки, поэтому такое слияние не создаёт отдельный post-merge запуск. При этом проверка PR обычно уже прошла на его head commit. GitHub не обязан повторять её только потому, что head был включён в другую feature-ветку.

В PR #5 целевой веткой была `fix/settings-and-map-interactions`, а в PR #6 — `fix/ci-local-checks`. Их проверки прошли как PR jobs, затем commits были влиты в эти промежуточные ветки. PR #4 был единственным из этой последовательности, который изменил `main`, поэтому после него появился единственный `push` run в `main`. PR #7 сводит эти изменения в PR с целевой `main`.

Для обычных независимых изменений указывайте `main` как base PR. Если PR намеренно основан на другом ещё не влитом PR, считайте его зависимым: влейте нижний PR в `main`, затем обновите base/head верхнего PR относительно `main`. Не вливайте цепочку PR в промежуточные source branches, считая, что это автоматически обновит основную ветку.

Это поведение исправляется порядком интеграции, а не повторным запуском всех jobs после каждого merge в любой ветке. Отдельный push-trigger для всех feature-веток дублировал бы работу при каждом обновлении head PR и тратил бы Actions без новой проверки пользовательского результата.

## APK превью для pull request

Обычный PR собирает, проверяет на Android emulator и публикует single-file Minsk APK. После обязательных jobs `quality`, `tests`, `data`, `terrain-spike` и `android` workflow `Build and verify` вызывает reusable workflow `Pull request Android APKs` по умолчанию для PR. Это исключает много минут подготовки полной страны из каждого PR. В вызов передаются номер PR из payload текущего `pull_request` и точный `github.sha`; reusable workflow сверяет событие и номер, затем checkout-ит этот SHA. Версия APK вычисляется в job подготовки геоданных и передаётся как output в Android job. Minsk и Belarus загружаются как отдельные single-file artifacts без ZIP, SHA-256 публикуется отдельно. Права ограничены `contents: read`, секреты в fork PR не используются.

Актуальная политика: два APK по умолчанию, `ci:skip-full-maps` для быстрого PR, `ci:full-maps` для принудительных двух карт. См. [метки PR](pull-request-labels.md). Minsk публикуется основным Android job сразу после проверки; поздний job публикует только Belarus, не дублируя Minsk.
