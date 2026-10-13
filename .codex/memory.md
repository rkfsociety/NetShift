# NetShift: память проекта

Эта карта содержит долгосрочный контекст разработки и переносится вместе с репозиторием. Она не заменяет код, тесты или workflow: при расхождении проверяй их как источник истины и обновляй карту.

## Назначение и платформа

- NetShift — Windows-приложение для настройки DNS и управления zapret от Flowseal; пользовательское поведение описано в `README.md`.
- UI — WPF с Windows Forms для значка в трее; основной проект `src/NetShift.App/NetShift.App.csproj` на `net10.0-windows`. Служба DNS — отдельный проект `src/NetShift.DnsService/DnsService.csproj`.
- В репозитории нет `.sln`: приложение, DNS-служба и регрессионная программа собираются/запускаются отдельными проектами.

## Карта кода и потоки выполнения

- `src/NetShift.App/App.xaml.cs` задаёт порядок старта: сначала разбираются команды `--admin`, затем bootstrapper устанавливает/запускает пользовательскую копию, после чего открывается `MainWindow`. Успешный показ главного окна подтверждает установку обновления и удаляет резервную копию.
- `src/NetShift.App/MainWindow.xaml` содержит главное окно; оно фиксировано в размере 1120×760 и не имеет кнопки разворачивания. Обработчики, настройки и orchestration функций находятся в `MainWindow.xaml.cs`; модальные окна приложения использует общий стилизованный `ThemedDialog` в `ThemedMessageDialogWindow.xaml`. Разметка/обработчики статуса проверки zapret вынесены в `MainWindow.ZapretTestStatus.cs`.
- `src/NetShift.App/Services/AdminCommandRouter.cs` — единственная точка входа для повышенных операций `--admin <operation> ...`. `MainWindow.RunElevatedWorkerAsync` повторно запускает тот же exe с UAC `runas`; при новых административных действиях обновляй и вызов UI, и разбор/валидацию команды в router. Не обходи проверку аргументов и путей.
- `NetShiftBootstrapper.cs` устанавливает exe в профиль пользователя, создаёт ярлыки и переносит автозапуск. Самоустановка выполняется только для релизной сборки с metadata репозитория и встроенным exe DNS-службы; локальная сборка без этих publish-параметров не включает этот путь.
- `MainWindow.xaml.cs` координирует страницы DNS/zapret, чтение пользовательских настроек, доступные адаптеры и обращения к службам. Сетевые адаптеры получает `NetworkService.cs`; модели UI находятся в `Models/`.

## DNS

- Native DNS записывается повышенной командой `dns-apply` в Windows и в профиль `HKLM\SOFTWARE\NetShift\Dns`; команда `dns-reset` выключает сохранённый профиль и возвращает DNS адаптера к DHCP.
- В профиле службы используются `Enabled`, `Revision`, `InterfaceGuid`, `Addresses` и `DohTemplate`. Изменение увеличивает `Revision`.
- `NetShift.DnsService/Program.cs` регистрирует Windows-службу и `DnsProfileWorker`. Worker проверяет профиль раз в 5 секунд и при новой ревизии применяет DNS/DoH через Windows PowerShell; при ошибке повторяет попытку. Установка и обновление службы реализованы в `AdminCommandRouter.cs`; имя службы SCM — `NetShiftDns`, бинарник располагается в `%ProgramFiles%\NetShift\DnsService`.
- Legacy DNS реализован в `MainWindow.xaml.cs`, `Services/LegacyDohProxy.cs` и переиспользуемых PowerShell-фрагментах `Services/LegacyDnsScriptFragments.cs`. Прокси слушает loopback UDP/53 и пересылает DNS-over-HTTPS; резервные параметры хранятся в `HKLM\SOFTWARE\NetShift\LegacyDns`. При остановке UI сначала восстанавливает исходные DNS; если восстановление не удалось, приложение остаётся открытым.
- Версия установленной DNS-службы отдельно хранится в `HKLM\SOFTWARE\NetShift\DnsService` и берётся из версии проекта DNS-службы при публикации.

## zapret и сетевые сервисы

- `ZapretReleaseService.cs` получает релизы и списки из официального репозитория Flowseal, проверяет источник, размер и SHA-256; установка и опасные операции проходят через `AdminCommandRouter.cs`.
- Перебор стратегий и пакетные операции выполняет `AdminCommandRouter`; порядок стратегий естественный, каждая запускается официальным `general*.bat` строго по одной и останавливается после проверки. `ZapretConnectivityTester.cs` читает `utils/targets.txt` (при пустом/отсутствующем файле использует набор из `test zapret.ps1`), для каждой URL-цели последовательно вызывает `curl.exe -I` в режимах HTTP/1.1, TLS 1.2 и TLS 1.3 с таймаутом 4 секунды и параллельно проверяет ping всех URL и PING-only целей с таймаутом 1 секунда. Все стратегии проверяются; лучшая выбирается по числу `curl` с кодом выхода 0, при равенстве — по числу успешных ping, как в upstream. HTTP status code не влияет на успех, поэтому HTTP 4xx/5xx с кодом curl 0 считаются успешной пробой. Оригинальные `.bat` выполняют свою подготовку, включая возможное включение TCP timestamps; UI предупреждает об этом. Активная служба проверяется той же сетевой схемой без остановки. UI получает прогресс из JSON-файла во временном каталоге; формат задан в `Models/ZapretTestProgress.cs`.
- `NetShiftUpdateService.cs` находит `NetShift.exe` в последнем GitHub Release, проверяет URL, размер и SHA-256 digest из GitHub API, скачивает файл и запускает PowerShell-обновлятор с резервированием и подтверждением старта. Проверка SHA-256 внутри скрипта использует .NET API для совместимости с Windows PowerShell 5.1; сбой передаётся установленному приложению через `--update-failed` и показывается общим стилизованным диалогом. Release workflow загружает только asset `NetShift.exe`; GitHub отдельно показывает автоматически созданные Source code ZIP и tar.gz, не входящие в assets релиза. `DnsAiProviderService.cs` загружает DNS-AI провайдера; `ZapretDiagnosticsService.cs` собирает диагностику.
- Остальные системные команды и их проверки аргументов сосредоточены в `AdminCommandRouter.cs`; перед добавлением новой операции ищи существующий маршрут и вспомогательную логику в этом файле.

## Локальные данные и конфигурация

- UI-настройки пользователя: `%LocalAppData%\NetShift\settings.json`; структура `AppSettings` объявлена в конце `MainWindow.xaml.cs`.
- Установленный UI: `%LocalAppData%\Programs\NetShift\NetShift.exe`. Загруженные пакеты zapret устанавливаются в `NetShift\zapret-<version>` в корне системного диска.
- Кэш скачанных списков и архивов: `%LocalAppData%\NetShift\Downloads`; staging и резервные данные обновления приложения: `%LocalAppData%\NetShift\Updates`.
- Системное состояние DNS хранится в указанных выше ветках HKLM; изменение системного состояния выполняется только с повышенными правами. Не переносить пользовательские настройки, временные файлы, секреты или данные машины в `.codex/`.

## Сборка, проверки и выпуск

- Требуется .NET 10 SDK. Поскольку проекты используют WPF/Windows Forms и системные службы Windows, полноценная сборка и проверка рассчитаны на Windows; GitHub workflow запускается на `windows-latest`.
- Сборка UI из корня репозитория: `dotnet build src/NetShift.App/NetShift.App.csproj`.
- Сборка DNS-службы: `dotnet build src/NetShift.DnsService/DnsService.csproj`.
- Регрессионные проверки: `dotnet run --project tests/NetShift.RegressionChecks/NetShift.RegressionChecks.csproj`. Это консольная программа, а не тестовый framework: она проверяет Legacy DNS backup/restore, timeout процесса, синтаксис скрипта обновления и распознавание/удаление резервной копии при подтверждении обновления. Для неё нужен Windows PowerShell 5.1; release workflow запускается на `windows-latest`.
- Release workflow — `.github/workflows/release.yml`: push в `main` сравнивает app `<Version>` с Git-тегами `vMAJOR.MINOR.PATCH`, создаёт и отправляет тег при более новой версии, запускает регрессионные проверки, публикует self-contained `win-x64` DNS-службу и приложение, затем создаёт GitHub Release с единственным загружаемым asset `NetShift.exe` (при повторном запуске удаляет другие assets этого релиза). GitHub отдельно показывает автоматически созданные Source code ZIP и tar.gz. Последний шаг workflow удаляет все более старые GitHub Releases и version-теги, оставляя только самый новый. Публикация приложения передаёт version и встраивает exe службы.
- Отдельный `Version` приложения в `src/NetShift.App/NetShift.App.csproj` определяет версию релиза; версия DNS payload задаётся `src/NetShift.DnsService/DnsService.csproj`. Постоянное правило обновления версий находится в `.codex/rules.md`.

## Ориентир для будущих изменений

Сначала смотри фактический код и конфигурацию указанных точек входа, затем соответствующую часть этой карты. При изменении архитектуры, хранения данных, команд, сборки или релиза обновляй этот файл в том же task-only коммите, что и код. Не фиксируй текущую версию, временные статусы, выводы из чата или локальные пути машины.
