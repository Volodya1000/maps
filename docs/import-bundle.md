# Перенос Git bundle в пустой репозиторий

GitHub отклонил запись через git и подключение API (403). Внешний репозиторий не изменён. Bundle содержит базовый main и отдельную ветку codex/offline-planner с реализацией.

В среде с вашими полномочиями записи:

```sh
git clone CableRoutePlanner.bundle maps
cd maps
git remote set-url origin https://github.com/Volodya1000/maps.git
git push origin main
git push origin codex/offline-planner
gh pr create --repo Volodya1000/maps --base main --head codex/offline-planner --draft --title 'Офлайн-планирование кабельных маршрутов: рабочая 2D-реализация и исследование платформ' --body-file docs/pull-request.md
```

Слияние PR не выполняется автоматически. Описание честно фиксирует незакрытые обязательные требования.
