# Тестовая Android-сборка 0.1.1

[Скачать CableRoutePlanner-0.1.1-test.apk](https://github.com/Volodya1000/maps/raw/refs/heads/downloads/android-preview/CableRoutePlanner-0.1.1-test.apk)

Исправлено падение при запуске: стартовой Activity назначена тема Theme.AppCompat.Light.NoActionBar. Исходники: https://github.com/Volodya1000/maps/pull/1, commit 342d7a728b743a0520aa945739d28efc96f559fa.

APK подписан тем же тестовым ключом, versionCode увеличен до 2: можно устанавливать поверх 0.1.0. Разрешение INTERNET отсутствует. Реальный офлайн-пакет OSM района Дрозды — Новинки (Минск) включён.

[Проверка запуска в Android 14 эмуляторе прошла](https://github.com/Volodya1000/maps/actions/runs/39972389857): Activity успешно запущена три раза, сохранены logcat и screenshots. На Xiaomi пользователя исправленная версия ещё требует повторной проверки.

Реализованы 2D-карта, точки и расчёт маршрутов. Встроенный 3D, ESA WorldCover и полный пакет Беларуси пока отсутствуют. Это не полное MVP по ТЗ.

© OpenStreetMap contributors, ODbL 1.0 / DbCL 1.0. Лицензии и уведомления включены в APK вместе с данными.
