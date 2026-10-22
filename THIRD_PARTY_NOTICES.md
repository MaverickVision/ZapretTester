# Сторонние компоненты и материалы

- `tests/ZapretTester.Tests/Fixtures` и `Fixtures1103` содержат BAT-примеры Flowseal 1.10.2 и 1.10.3. Источник: https://github.com/Flowseal/zapret-discord-youtube. Используются как данные для тестирования парсера; тесты не выполняют BAT. Сохранённые тексты лицензий находятся в `licenses/Flowseal-1.10.2.txt` и `licenses/Flowseal-1.10.3.txt`.
- Сетевой движок zapret и WinDivert берутся из выбранной пользователем отдельной установки Flowseal. Их бинарники не поставляются с Zapret Tester. Проекты: https://github.com/bol-van/zapret и https://github.com/basil00/WinDivert.
- Переносимый выпуск содержит .NET Runtime и Windows Desktop Runtime Microsoft. Лицензии пакетов `Microsoft.NETCore.App.Runtime.win-x64` и `Microsoft.WindowsDesktop.App.Runtime.win-x64` версии 10.0.9 и уведомления Runtime сохранены в `licenses/dotnet-*`. При обновлении runtime нужно обновить эти файлы из соответствующих NuGet-пакетов.
- Зависимости автоматических тестов перечислены в проекте `ZapretTester.Tests`; они не входят в переносимое приложение.
- Discord, League of Legends, Riot Games, YouTube, Steam, Twitch и Cloudflare — названия соответствующих сторонних сервисов. Их обозначения в интерфейсе не означают одобрения или связи с авторами Zapret Tester.

