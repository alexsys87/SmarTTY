[← К оглавлению документации](../README.md#документация)

# 1. Требования и сборка

**Для запуска:** Windows 10 или 11 **x64** и [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (x64) для обычной сборки; без него Windows покажет системное сообщение с предложением установить среду. Единый exe-файл (см. [ниже](#сборка-единого-exe-файла)) не требует ничего. Приложение использует WPF и Windows Forms; 32-разрядная Windows, Windows 7 и 8.1 не поддерживаются (.NET 10 их не поддерживает), нативный запуск в Linux/macOS не предусмотрен.

**Для сборки:** [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0); Visual Studio 2026 с компонентом **.NET desktop development** по желанию, достаточно одного SDK. Нужен доступ к NuGet для восстановления зависимостей (`SSH.NET` версии `2026.0.0`, `System.IO.Ports`, `System.Security.Cryptography.ProtectedData`).

```powershell
git clone https://github.com/alexsys87/SmarTTY.git
cd SmarTTY
dotnet build SmarTTY.csproj -c Release
.\bin\Release\net10.0-windows\SmarTTY.exe
```

Восстановление пакетов выполняется самой командой `dotnet build`. Проект также собирается на Linux и macOS (`EnableWindowsTargeting`), но только для проверки компиляции: запустить приложение там нельзя.

Альтернатива: откройте `SmarTTY.sln` в Visual Studio, выберите `Release` и соберите решение. При переносе сборки копируйте всю выходную папку, а не только `SmarTTY.exe`.

## Сборка единого exe-файла

Обычная сборка зависит от установленного .NET 10 Desktop Runtime и кладёт рядом с `SmarTTY.exe` зависимые сборки (`Renci.SshNet.dll`, `BouncyCastle.Cryptography.dll` и другие). Чтобы получить **один самодостаточный `SmarTTY.exe`**, опубликуйте проект со свойством `SingleExe=true`: это штатная публикация .NET в один файл (single-file, self-contained, со сжатием и ReadyToRun), в которую входят среда выполнения, WPF/Windows Forms и все зависимости.

```powershell
dotnet publish SmarTTY.csproj -c Release -p:SingleExe=true
.\bin\Release\net10.0-windows\publish\SmarTTY.exe
```

В `bin\Release\net10.0-windows\publish\` лежит только `SmarTTY.exe` (отладочные символы встроены в него). Файл заметно больше обычной сборки, потому что содержит всю среду выполнения (около 95 МБ). Свойство `SingleExe` действует только на время публикации и не меняет обычный вывод. Файлы рядом с exe (`ApiSymbols.xml`, `CommandCompletions.xml`, `portable.txt`, `PortableSettings\`, `vcxsrv\`) работают так же, как и в обычной сборке: их ищут в папке, где лежит `SmarTTY.exe`.

CI собирает оба варианта: job `build` публикует обычную сборку как `SmarTTY-Release`, а job `single-exe` — единый файл как `SmarTTY-SingleExe`.

## Отладочная трассировка терминала (только Debug)

В конфигурации **Debug** терминал пишет диагностику в вывод отладчика (`Debug.WriteLine`): управляющие символы
(`<CR>`, `<LF>`, `<TAB>`, `<BEL>`, `<SO>`, `<SI>`, `<BACK>`) и, для каждой escape-последовательности, её смещение
и смысл (`[ESC @12] Save cursor`, `[ESC @40] WARNING: Unknown CSI command: …`). Строки видны в окне **Output**
Visual Studio (категория **Debug**), в [DebugView](https://learn.microsoft.com/sysinternals/downloads/debugview)
или в любом другом отладчике.

В конфигурации **Release** этой диагностики нет вообще: `TerminalEmulatorBase.Trace` помечен `[Conditional("DEBUG")]`,
и компилятор убирает и его вызовы, и строки описаний, поэтому обычная сборка работает без накладных расходов и
ничего не выводит. Включать и выключать нечего — достаточно собрать нужную конфигурацию; CI и релизные сборки всегда
Release, так что на них это не влияет.

Для SSH нужен доступный SSH-сервер и учётная запись на нём. Файловые функции и Smart Terminal рассчитаны на Unix/Linux-окружение с shell и стандартными командами. Для SCP необходима удалённая команда `scp`, для потоковой передачи каталогов — `tar`; для Smart Terminal по умолчанию используется `bash`. Ограниченная shell или SFTP-only доступ могут не поддерживать эти функции.
