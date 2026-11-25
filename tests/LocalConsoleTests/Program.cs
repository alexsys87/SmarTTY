using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using VisualTTY;
using VisualTTY.DeviceTerminal;

using LocalConsole = VisualTTY.DeviceTerminal.DeviceTerminalSettings.ConnectionSettings.LocalConsole;

// Checks of a local console (cmd, PowerShell, WSL in a Windows pseudo console) that need no Windows: the consoles the launcher
// offers for the programs that are installed, the command line and the start folder of a console, the settings a new one gets,
// and the XML of the saved profile. Run with: dotnet run --project tests/LocalConsoleTests
internal static class Program
{
    private static int _failures;

    private static void Check(bool condition, string name)
    {
        if (condition)
        {
            Console.WriteLine("PASS: " + name);
            return;
        }
        _failures++;
        Console.WriteLine("FAIL: " + name);
    }

    private static void Equal<T>(T expected, T actual, string name)
    {
        Check(EqualityComparer<T>.Default.Equals(expected, actual), name + (EqualityComparer<T>.Default.Equals(expected, actual) ? "" : " (expected '" + expected + "', got '" + actual + "')"));
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine("FAIL: " + name + " -> " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static LocalConsoleProfiles.Environment Machine(params string[] files)
    {
        HashSet<string> existing = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        return new LocalConsoleProfiles.Environment
        {
            SystemDirectory = Path.Combine("C:", "Windows", "System32"),
            ProgramFilesDirectories = new[] { Path.Combine("C:", "Program Files") },
            PathDirectories = new[] { Path.Combine("C:", "Tools"), Path.Combine("C:", "Scoop") },
            FileExists = existing.Contains
        };
    }

    private static string Sys(params string[] parts)
    {
        return Path.Combine(new[] { "C:", "Windows", "System32" }.Concat(parts).ToArray());
    }

    private static void BuiltInChecks()
    {
        string cmd = Sys("cmd.exe");
        string powershell = Sys("WindowsPowerShell", "v1.0", "powershell.exe");
        string wsl = Sys("wsl.exe");
        string pwsh = Path.Combine("C:", "Program Files", "PowerShell", "7", "pwsh.exe");
        List<LocalConsole> all = LocalConsoleProfiles.BuiltIn(Machine(cmd, powershell, wsl, pwsh));
        Equal("Command Prompt|Windows PowerShell|PowerShell 7|WSL", string.Join("|", all.Select(c => c.Name)), "every installed console, in order");
        Equal(cmd, all[0].Program, "Command Prompt runs cmd.exe of the system folder");
        Equal(null, all[0].Arguments, "Command Prompt has no arguments");
        Equal(powershell, all[1].Program, "Windows PowerShell runs the one of the system folder");
        Equal(pwsh, all[2].Program, "PowerShell 7 of Program Files");
        Equal(wsl, all[3].Program, "WSL runs wsl.exe");
        Equal("~", all[3].Arguments, "WSL starts in the Linux home folder");
        Check(all.All(c => c.IsValid()), "every built-in console is valid");
        Equal(4, all.Select(c => c.FileNameBase).Distinct(StringComparer.OrdinalIgnoreCase).Count(), "the built-in consoles are saved under different names");

        List<LocalConsole> onlyCmd = LocalConsoleProfiles.BuiltIn(Machine(cmd));
        Equal("Command Prompt", string.Join("|", onlyCmd.Select(c => c.Name)), "a program that is not installed is not offered");

        string scoopPwsh = Path.Combine("C:", "Scoop", "pwsh.exe");
        List<LocalConsole> fromPath = LocalConsoleProfiles.BuiltIn(Machine(cmd, scoopPwsh, Path.Combine("C:", "Tools", "other.exe")));
        Equal(scoopPwsh, fromPath.Single(c => c.Name == "PowerShell 7").Program, "PowerShell 7 found in PATH when it is not in Program Files");
        List<LocalConsole> both = LocalConsoleProfiles.BuiltIn(Machine(pwsh, scoopPwsh));
        Equal(pwsh, both.Single().Program, "Program Files comes before PATH");

        LocalConsoleProfiles.Environment noSystem = Machine(cmd);
        noSystem.SystemDirectory = null;
        Equal(0, LocalConsoleProfiles.BuiltIn(noSystem).Count, "no system folder: nothing of it is offered");

        Equal("C:\\a|C:\\b c|D:\\x", string.Join("|", LocalConsoleProfiles.SplitPath(" C:\\a;;\"C:\\b c\"; D:\\x ;")), "PATH split, blanks, quotes and empty items left out");
        Equal(0, LocalConsoleProfiles.SplitPath(null).Count, "no PATH");
        Equal(null, LocalConsoleProfiles.FindInPath(Machine(), "pwsh.exe"), "a program in no folder of PATH");
    }

    private static void CommandLineChecks()
    {
        Equal("cmd.exe", LocalConsoleProfiles.BuildCommandLine("cmd.exe", null), "a program alone");
        Equal("cmd.exe /k ver", LocalConsoleProfiles.BuildCommandLine(" cmd.exe ", " /k ver "), "the arguments after the program, trimmed");
        Equal("\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -NoLogo", LocalConsoleProfiles.BuildCommandLine("C:\\Program Files\\PowerShell\\7\\pwsh.exe", "-NoLogo"), "a path with a blank in quotes");
        Equal("\"C:\\My Tools\\a.exe\" x", LocalConsoleProfiles.BuildCommandLine("\"C:\\My Tools\\a.exe\"", "x"), "a path in quotes of its own is kept");
        Equal("wsl.exe -d \"Ubuntu 22.04\"", LocalConsoleProfiles.BuildCommandLine("wsl.exe", "-d \"Ubuntu 22.04\""), "the arguments as they were written");
        Equal(null, LocalConsoleProfiles.BuildCommandLine("  ", "x"), "no program: no command line");
        Equal(null, LocalConsoleProfiles.BuildCommandLine(null, null), "nothing");
    }

    private static void FolderChecks()
    {
        Func<string, string> expand = text => text.Replace("%PROJECTS%", "D:\\src");
        Equal("D:\\src\\app", LocalConsoleProfiles.ResolveWorkingDirectory("%PROJECTS%\\app", expand, "C:\\Users\\me"), "a folder with a variable");
        Equal("C:\\a b", LocalConsoleProfiles.ResolveWorkingDirectory(" \"C:\\a b\" ", expand, "C:\\Users\\me"), "quotes and blanks around a folder taken off");
        Equal("C:\\Users\\me", LocalConsoleProfiles.ResolveWorkingDirectory("", expand, "C:\\Users\\me"), "no folder: the profile folder");
        Equal("C:\\Users\\me", LocalConsoleProfiles.ResolveWorkingDirectory(null, expand, "C:\\Users\\me"), "null: the profile folder");
        Equal(null, LocalConsoleProfiles.ResolveWorkingDirectory(null, expand, ""), "no profile folder either: the folder of this process");
    }

    private static void NameChecks()
    {
        Equal("Dev shell", new LocalConsole { Name = " Dev shell ", Program = "cmd.exe" }.DisplayName, "the name given");
        Equal("pwsh", new LocalConsole { Program = "C:\\Program Files\\PowerShell\\7\\pwsh.exe" }.DisplayName, "no name: the file of the program without .exe");
        Equal("bash", new LocalConsole { Program = "\"C:/Git/bin/bash.EXE\"" }.DisplayName, "forward slashes, quotes, another case of .exe");
        Equal("python3", new LocalConsole { Program = "python3" }.DisplayName, "a name without .exe");
        Equal("Console", new LocalConsole().DisplayName, "nothing set");
        Equal("console_WSL", new LocalConsole { Name = "WSL", Program = "x" }.FileNameBase, "saved under its name");
        Equal("WSL", new LocalConsole { Name = "WSL" }.ToString(), "the text of a console is its name");
        Check(!new LocalConsole { Name = "x", Program = " " }.IsValid(), "a console without a program is not valid");
        LocalConsole original = new LocalConsole { Name = "a", Program = "b", Arguments = "c", WorkingDirectory = "d" };
        LocalConsole copy = original.Clone();
        copy.Arguments = "changed";
        Check(!ReferenceEquals(original, copy) && original.Arguments == "c" && copy.Program == "b" && copy.WorkingDirectory == "d", "a clone is a copy");
    }

    private static void SettingsChecks()
    {
        LocalConsole console = new LocalConsole { Name = "Command Prompt", Program = "cmd.exe" };
        DeviceTerminalSettings settings = LocalConsoleProfiles.NewSettings(console);
        Check(ReferenceEquals(console, settings.Connection), "the settings hold the console");
        Equal(DeviceTerminalSettings.LineEndingType.CR, settings.LineEnding, "Enter is CR, as a console window sends it");
        Check(!settings.TreatLFAsCRLF, "LF is not taken as CR LF (the pseudo console sends CR LF itself)");
        Check(!settings.AutoDetectPrompt, "no prompt is followed");
        Check(!settings.EchoTypedCharacters, "nothing is echoed here");
        Check(!settings.SyncRemoteTerminalSize, "no command sets the size");
        Check(!settings.ReconnectAutomatically, "an ended program is not started again by itself");
        Equal("utf-8", settings.TextEncoding, "the text is UTF-8");
    }

    private static void XmlChecks()
    {
        RawConnectionSettings saved = new RawConnectionSettings
        {
            Settings = LocalConsoleProfiles.NewSettings(new LocalConsole { Name = "Dev", Program = "%ComSpec%", Arguments = "/k \"vcvars64.bat\"", WorkingDirectory = "D:\\src" }),
            Icon = "terminal.png"
        };
        XmlSerializer serializer = new XmlSerializer(typeof(RawConnectionSettings));
        StringWriter writer = new StringWriter();
        serializer.Serialize(writer, saved);
        string xml = writer.ToString();
        Check(xml.Contains("com.sysprogs.terminal.connection.localconsole"), "the console is written with a type name of its own");
        Check(!xml.Contains("DisplayName") && !xml.Contains("FileNameBase"), "the names worked out are not written");
        RawConnectionSettings read = (RawConnectionSettings)serializer.Deserialize(new StringReader(xml));
        LocalConsole console = read.Settings.Connection as LocalConsole;
        Check(console != null, "read back as a local console");
        Equal("Dev", console?.Name, "the name read back");
        Equal("%ComSpec%", console?.Program, "the program read back as it was written (expanded when it starts)");
        Equal("/k \"vcvars64.bat\"", console?.Arguments, "the arguments read back");
        Equal("D:\\src", console?.WorkingDirectory, "the start folder read back");
        Equal(DeviceTerminalSettings.LineEndingType.CR, read.Settings.LineEnding, "the line ending read back");
        Check(!read.Settings.AutoDetectPrompt, "the prompt detection read back");

        // A profile of another kind is still read (the include of the new type changes nothing for them).
        RawConnectionSettings tcp = new RawConnectionSettings { Settings = new DeviceTerminalSettings { Connection = new DeviceTerminalSettings.ConnectionSettings.Tcp { Host = "h", Port = 23 } } };
        StringWriter tcpWriter = new StringWriter();
        serializer.Serialize(tcpWriter, tcp);
        DeviceTerminalSettings.ConnectionSettings.Tcp tcpRead = ((RawConnectionSettings)serializer.Deserialize(new StringReader(tcpWriter.ToString()))).Settings.Connection as DeviceTerminalSettings.ConnectionSettings.Tcp;
        Check(tcpRead != null && tcpRead.Host == "h" && tcpRead.Port == 23, "a TCP profile still goes through XML");
    }

    private static void ShownTypesChecks()
    {
        XmlSerializer serializer = new XmlSerializer(typeof(ShownConnectionTypes));
        ShownConnectionTypes old = (ShownConnectionTypes)serializer.Deserialize(new StringReader("<ShownConnectionTypes><SSH>true</SSH><RawTcp>true</RawTcp><Serial>true</Serial><HideNetconsole>false</HideNetconsole></ShownConnectionTypes>"));
        Check(!old.HideLocalConsoles, "an older preferences file shows the local consoles");
        StringWriter writer = new StringWriter();
        serializer.Serialize(writer, new ShownConnectionTypes { HideLocalConsoles = true });
        Check(((ShownConnectionTypes)serializer.Deserialize(new StringReader(writer.ToString()))).HideLocalConsoles, "hiding them is kept");
    }

    private static int Main()
    {
        Run("built-in consoles", BuiltInChecks);
        Run("command line", CommandLineChecks);
        Run("start folder", FolderChecks);
        Run("names", NameChecks);
        Run("settings", SettingsChecks);
        Run("xml", XmlChecks);
        Run("shown types", ShownTypesChecks);
        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "All local console checks passed." : _failures + " local console check(s) FAILED.");
        return _failures == 0 ? 0 : 1;
    }
}
