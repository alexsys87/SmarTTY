using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VisualTTY.DeviceTerminal;

/// <summary>
/// The rules of a local console (a program of this computer in a Windows pseudo console) that need no window and no Windows call:
/// the consoles the launcher offers by itself, the command line and the start folder of a console, and the settings a new one gets.
/// </summary>
public static class LocalConsoleProfiles
{
    /// <summary>Where the programs of the built-in consoles are looked for.</summary>
    public sealed class Environment
    {
        /// <summary>The system folder (C:\Windows\System32).</summary>
        public string SystemDirectory;

        /// <summary>The program files folders, the 64-bit one first (C:\Program Files).</summary>
        public IReadOnlyList<string> ProgramFilesDirectories = new string[0];

        /// <summary>The folders of PATH.</summary>
        public IReadOnlyList<string> PathDirectories = new string[0];

        /// <summary>Whether a file exists.</summary>
        public Func<string, bool> FileExists = File.Exists;
    }

    /// <summary>The environment of this process (the system folder, the program files folders and PATH).</summary>
    public static Environment Current()
    {
        List<string> programFiles = new List<string>();
        foreach (string variable in new[] { "ProgramW6432", "ProgramFiles" })
        {
            string folder = System.Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrEmpty(folder) && !programFiles.Contains(folder))
            {
                programFiles.Add(folder);
            }
        }
        return new Environment
        {
            SystemDirectory = System.Environment.SystemDirectory,
            ProgramFilesDirectories = programFiles,
            PathDirectories = SplitPath(System.Environment.GetEnvironmentVariable("PATH"))
        };
    }

    /// <summary>The folders of a PATH value (empty items and quotes left out).</summary>
    public static IReadOnlyList<string> SplitPath(string path)
    {
        List<string> folders = new List<string>();
        foreach (string item in (path ?? "").Split(';'))
        {
            string folder = item.Trim().Trim('"');
            if (folder.Length != 0)
            {
                folders.Add(folder);
            }
        }
        return folders;
    }

    /// <summary>
    /// The consoles offered without being saved, for the programs that are installed: Command Prompt, Windows PowerShell,
    /// PowerShell 7 and WSL, in this order. Their names are fixed, so a console changed and saved by the user takes the
    /// place of the built-in one of the same name (<see cref="DeviceTerminalSettings.ConnectionSettings.LocalConsole.FileNameBase"/>).
    /// </summary>
    public static List<DeviceTerminalSettings.ConnectionSettings.LocalConsole> BuiltIn(Environment environment)
    {
        List<DeviceTerminalSettings.ConnectionSettings.LocalConsole> consoles = new List<DeviceTerminalSettings.ConnectionSettings.LocalConsole>();
        string system = environment.SystemDirectory;
        if (!string.IsNullOrEmpty(system))
        {
            Add(consoles, environment, "Command Prompt", Path.Combine(system, "cmd.exe"));
            Add(consoles, environment, "Windows PowerShell", Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"));
        }
        string pwsh = null;
        foreach (string programFiles in environment.ProgramFilesDirectories)
        {
            string candidate = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
            if (environment.FileExists(candidate))
            {
                pwsh = candidate;
                break;
            }
        }
        pwsh = pwsh ?? FindInPath(environment, "pwsh.exe");
        Add(consoles, environment, "PowerShell 7", pwsh);
        if (!string.IsNullOrEmpty(system))
        {
            // wsl.exe is there on every Windows 10 and 11, with or without a distribution; "~" starts in the home folder of Linux.
            Add(consoles, environment, "WSL", Path.Combine(system, "wsl.exe"), "~");
        }
        return consoles;
    }

    private static void Add(List<DeviceTerminalSettings.ConnectionSettings.LocalConsole> consoles, Environment environment, string name, string program, string arguments = null)
    {
        if (program != null && environment.FileExists(program))
        {
            consoles.Add(new DeviceTerminalSettings.ConnectionSettings.LocalConsole { Name = name, Program = program, Arguments = arguments });
        }
    }

    /// <summary>The first file of that name in the folders of PATH, or null.</summary>
    public static string FindInPath(Environment environment, string fileName)
    {
        foreach (string folder in environment.PathDirectories)
        {
            string candidate;
            try
            {
                candidate = Path.Combine(folder, fileName);
            }
            catch (ArgumentException)
            {
                continue;
            }
            if (environment.FileExists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// The command line a console is started with: the program, in quotes when it has a blank and none of its own, then the
    /// arguments as they were written. Null when there is no program.
    /// </summary>
    public static string BuildCommandLine(string program, string arguments)
    {
        string trimmed = (program ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }
        StringBuilder line = new StringBuilder();
        if (trimmed.IndexOfAny(new[] { ' ', '\t' }) >= 0 && !trimmed.StartsWith("\"", StringComparison.Ordinal))
        {
            line.Append('"').Append(trimmed).Append('"');
        }
        else
        {
            line.Append(trimmed);
        }
        string args = (arguments ?? "").Trim();
        if (args.Length != 0)
        {
            line.Append(' ').Append(args);
        }
        return line.ToString();
    }

    /// <summary>The folder a console starts in: the one configured, with its variables expanded, or else the profile folder of the user.</summary>
    public static string ResolveWorkingDirectory(string configured, Func<string, string> expand, string userProfile)
    {
        string folder = (configured ?? "").Trim().Trim('"');
        if (folder.Length != 0)
        {
            return expand(folder);
        }
        return string.IsNullOrEmpty(userProfile) ? null : userProfile;
    }

    /// <summary>
    /// The settings of a tab for a console: Enter is CR (what a console window sends), the text is UTF-8 (the pseudo console
    /// writes UTF-8), no prompt is followed (the program edits its own command line, every key goes to it at once), nothing is
    /// echoed here (the program does it), and the size is passed on by the pseudo console, not by a command.
    /// </summary>
    public static DeviceTerminalSettings NewSettings(DeviceTerminalSettings.ConnectionSettings.LocalConsole console)
    {
        return new DeviceTerminalSettings
        {
            Connection = console,
            LineEnding = DeviceTerminalSettings.LineEndingType.CR,
            TreatLFAsCRLF = false,
            AutoDetectPrompt = false,
            EchoTypedCharacters = false,
            SyncRemoteTerminalSize = false,
            ReconnectAutomatically = false,
            TextEncoding = TerminalEncodings.DefaultName
        };
    }
}
