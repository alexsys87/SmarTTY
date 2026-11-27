# SmarTTY

**SmarTTY — графический SSH-клиент и терминал для Windows:** несколько сеансов и вкладок, Smart Terminal с автодополнением, передача файлов, удалённый редактор и работа с COM-портами и TCP-устройствами.

## Содержание

- [Возможности](#возможности)
- [Быстрый старт](#быстрый-старт)
- [Документация](#документация)
- [Ограничения](#ограничения)
- [Для разработчиков](#для-разработчиков)

## Возможности

Кратко; полное описание — в [docs/features.md](docs/features.md): [подключения и SSH](docs/features-ssh.md), [терминал](docs/features-terminal.md), [передача файлов](docs/features-transfer.md), редактор ([текст](docs/features-editor-edit.md), [подсветка](docs/features-editor-syntax.md), [hex](docs/features-editor-hex.md), [сохранение](docs/features-editor-save.md)), [обслуживание сервера](docs/features-maintenance.md), [COM и TCP](docs/features-com-tcp.md), [настройка и запуск](docs/features-settings.md).

- **Подключения:** несколько сеансов в одном окне; профили SSH, COM, Raw TCP, Telnet (в том числе поверх TLS), netconsole (UDP) и локальные консоли (cmd, PowerShell, WSL); вход по паролю или ключу, проверка ключа сервера, прокси (HTTP CONNECT, SOCKS5), X11 forwarding, проброс портов и SOCKS5 через SSH, поиск компьютеров в сети.
- **Терминал:** обычный терминал (xterm-совместимый) и **Smart Terminal** с историей, `Ctrl+R`, подсказками серым текстом, умным автодополнением команд и путей, кодом завершения и подсветкой ошибок.
- **Файлы:** передача файлов и каталогов по SCP/SFTP с очередью, файловая панель, **удалённый редактор** с подсветкой синтаксиса, поиском, несколькими курсорами, hex-режимом, просмотром Markdown, проверкой файла и защитой от потери данных.
- **Обслуживание сервера:** резервные копии и восстановление (tar.gz, flash/MTD), память и регистры (devmem), менеджер пакетов, локальный репозиторий OpenWrt, диспетчер процессов, быстрые команды и скрипты.
- **COM и TCP:** текстовый и HEX-режимы, XMODEM/YMODEM/ZMODEM/Kermit, gzip + base64, логи, линии COM-порта, автопереподключение.
- **Оформление и настройки:** 15 тем и свои темы, цветовые схемы терминала, прозрачный фон, масштабирование под DPI, vector-значки, portable-режим, запуск из командной строки.

## Быстрый старт

Нужны Windows 10/11 x64 и [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
git clone https://github.com/alexsys87/SmarTTY.git
cd SmarTTY
dotnet build SmarTTY.csproj -c Release
.\bin\Release\net10.0-windows\SmarTTY.exe
```

Один самодостаточный exe: `dotnet publish SmarTTY.csproj -c Release -p:SingleExe=true`. Подробности — в [требованиях и сборке](docs/build.md). Первое подключение: [docs/ssh-connection.md](docs/ssh-connection.md).

Названия кнопок и меню в документации приведены на английском, как в интерфейсе программы.

## Документация

| № | Раздел | Файл |
| --- | --- | --- |
| — | Все возможности подробно: [SSH](docs/features-ssh.md), [терминал](docs/features-terminal.md), [передача файлов](docs/features-transfer.md), редактор ([текст](docs/features-editor-edit.md), [подсветка](docs/features-editor-syntax.md), [hex](docs/features-editor-hex.md), [сохранение](docs/features-editor-save.md)), [обслуживание сервера](docs/features-maintenance.md), [COM и TCP](docs/features-com-tcp.md), [настройка](docs/features-settings.md) | [docs/features.md](docs/features.md) |
| 1 | Требования и сборка: [единый exe](docs/build.md#сборка-единого-exe-файла), [отладочная трассировка](docs/build.md#отладочная-трассировка-терминала-только-debug) | [docs/build.md](docs/build.md) |
| 2–3 | [Первое SSH-подключение](docs/ssh-connection.md#2-первое-ssh-подключение), [ключи и безопасность](docs/ssh-connection.md#3-ключи-и-безопасность) | [docs/ssh-connection.md](docs/ssh-connection.md) |
| 4 | Сеансы, вкладки и Smart Terminal: [Tools](docs/sessions-tools.md#какие-пункты-tools-показываются), [диспетчер процессов](docs/sessions-tools.md#диспетчер-процессов), [резервная копия tar.gz](docs/sessions-tools.md#резервная-копия-в-targz), [восстановление](docs/sessions-tools.md#восстановление-из-targz), [devmem](docs/sessions-tools.md#память-и-регистры-devmem), [flash (MTD)](docs/sessions-tools.md#резервная-копия-и-восстановление-flash-mtd), [быстрые команды](docs/sessions-tools.md#быстрые-команды-и-скрипты), [запуск скрипта](docs/sessions-tools.md#запуск-скрипта), [свободное место](docs/sessions-tools.md#свободное-место-на-диске) | [docs/sessions-tools.md](docs/sessions-tools.md) |
| 5 | Передача файлов и каталогов | [docs/file-transfer.md](docs/file-transfer.md) |
| 6 | Редактирование удалённых файлов | [docs/remote-editor.md](docs/remote-editor.md) |
| 7 | Управление Linux-пакетами, [локальный репозиторий OpenWrt](docs/packages.md#локальный-репозиторий-openwrt) | [docs/packages.md](docs/packages.md) |
| 8 | COM и TCP: [COM / UART](docs/com-tcp.md#com--uart), [TCP](docs/com-tcp.md#tcp), [Telnet](docs/com-tcp.md#telnet), [Netconsole](docs/com-tcp.md#netconsole-udp), [локальная консоль](docs/com-tcp.md#локальная-консоль), [ввод, HEX и файлы](docs/com-tcp.md#ввод-hex-и-файлы) | [docs/com-tcp.md](docs/com-tcp.md) |
| 9–10 | [Настройки и горячие клавиши](docs/settings.md#9-настройки-и-горячие-клавиши), [хранение настроек и portable-режим](docs/settings.md#10-хранение-настроек-и-portable-режим) | [docs/settings.md](docs/settings.md) |
| 11 | Командная строка | [docs/command-line.md](docs/command-line.md) |
| 12 | Решение типичных проблем | [docs/troubleshooting.md](docs/troubleshooting.md) |
| — | Ограничения текущей реализации | [docs/limitations.md](docs/limitations.md) |

## Ограничения

Ограничения X11, Telnet-протокола, протоколов передачи, DPI и отдельных настроек перечислены в [docs/limitations.md](docs/limitations.md). Описание относится к реализации в этом репозитории.

## Для разработчиков

Архитектура, карта папок, тесты и статус миграции на WPF — в [CLAUDE.md](CLAUDE.md).
