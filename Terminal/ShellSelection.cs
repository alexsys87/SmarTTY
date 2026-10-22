using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SmarTTY.IO;

namespace SmarTTY.Terminal;

/// <summary>
/// The shell that runs Smart Terminal commands in the interactive wrapper ("Wrap launched commands in an interactive shell",
/// which makes the aliases and functions of the shell available) and the history file that belongs to it.
/// In the automatic mode it is chosen once per connection from the answer to <see cref="ProbeCommand"/>: the login shell
/// if it is bash or ash, otherwise bash, then ash (BusyBox: OpenWrt, Alpine), then sh. In the manual mode the shell of
/// the Smart Terminal settings is used as typed.
/// Pure logic without SSH, tested by tests/SmartTerminalCommandLineTests.
/// </summary>
public static class ShellSelection
{
    /// <summary>The history file of the settings before it followed the shell; a different value is the user's own choice.</summary>
    public const string LegacyHistoryFile = "~/.bash_history";

    public const string LoginShellMarker = "SMARTTY_LOGIN_SHELL=";

    public const string ShellMarker = "SMARTTY_SHELL=";

    public const string BusyBoxMarker = "SMARTTY_BUSYBOX=";

    /// <summary>The shells looked for in $PATH, in the order of preference.</summary>
    public static readonly string[] Candidates = { "bash", "ash", "sh" };

    /// <summary>
    /// Reports the login shell ($SHELL, set by OpenSSH and Dropbear), which of bash, ash and sh are installed and which of
    /// them (and the login shell) are BusyBox applets. Only POSIX features, so that it runs in BusyBox ash, dash and bash.
    /// </summary>
    public const string ProbeCommand =
        "echo " + LoginShellMarker + "$SHELL; " +
        "case \"$(readlink -f \"$SHELL\" 2>/dev/null)\" in */busybox*) echo " + BusyBoxMarker + "$SHELL;; esac; " +
        "for s in bash ash sh; do p=$(command -v $s 2>/dev/null) || continue; echo " + ShellMarker + "$s; " +
        "case \"$(readlink -f \"$p\" 2>/dev/null)\" in */busybox*) echo " + BusyBoxMarker + "$s;; esac; done";

    // A path from $SHELL is put into command lines unquoted, so only plain characters are accepted.
    private static readonly Regex SafePath = new Regex("^/[A-Za-z0-9._+/-]+$");

    /// <summary>What <see cref="ProbeCommand"/> reported.</summary>
    public sealed class ProbeResult
    {
        /// <summary>The path of the login shell, or null when it was not reported or is not a plain path.</summary>
        public string LoginShell;

        /// <summary>The shells of <see cref="Candidates"/> found in $PATH.</summary>
        public readonly List<string> Installed = new List<string>();

        /// <summary>Shell names and paths that are BusyBox applets.</summary>
        public readonly HashSet<string> BusyBox = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>A shell to run commands in.</summary>
    public sealed class Choice
    {
        /// <summary>The command that starts the shell: a name ("bash"), a path ("/bin/ash") or, in the manual mode, whatever was typed.</summary>
        public string Command;

        /// <summary>
        /// BusyBox ash (or BusyBox sh): started with -i it prints its banner ("BusyBox v1.36.1 ... built-in shell (ash)") before
        /// every command, so it is started without -i and reads the file of $ENV itself (see <see cref="WrapInteractive"/>).
        /// </summary>
        public bool IsAsh;

        /// <summary>The name of the shell without the directory, e.g. "bash".</summary>
        public string Name => GetName(Command);
    }

    public static ProbeResult ParseProbe(IEnumerable<string> lines)
    {
        if (lines == null)
        {
            return null;
        }
        ProbeResult result = new ProbeResult();
        bool answered = false;
        foreach (string rawLine in lines)
        {
            string line = rawLine?.Trim();
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }
            if (line.StartsWith(LoginShellMarker, StringComparison.Ordinal))
            {
                answered = true;
                string path = line.Substring(LoginShellMarker.Length);
                result.LoginShell = SafePath.IsMatch(path) ? path : null;
            }
            else if (line.StartsWith(ShellMarker, StringComparison.Ordinal))
            {
                string name = line.Substring(ShellMarker.Length);
                if (Candidates.Contains(name) && !result.Installed.Contains(name))
                {
                    result.Installed.Add(name);
                }
            }
            else if (line.StartsWith(BusyBoxMarker, StringComparison.Ordinal))
            {
                result.BusyBox.Add(line.Substring(BusyBoxMarker.Length));
            }
        }
        return answered ? result : null;
    }

    /// <summary>
    /// The automatic choice: the login shell when it is bash or ash (so that OpenWrt with bash installed still uses the ash
    /// the user logs in to), otherwise the first of bash, ash and sh that is installed. Null when nothing is known.
    /// </summary>
    public static Choice ChooseAutomatic(ProbeResult probe)
    {
        if (probe == null)
        {
            return null;
        }
        if (probe.LoginShell != null)
        {
            string loginName = GetName(probe.LoginShell);
            if (loginName == "bash" || loginName == "ash")
            {
                return new Choice
                {
                    Command = probe.LoginShell,
                    IsAsh = loginName == "ash" || probe.BusyBox.Contains(probe.LoginShell)
                };
            }
        }
        foreach (string name in Candidates)
        {
            if (probe.Installed.Contains(name))
            {
                return new Choice
                {
                    Command = name,
                    IsAsh = name == "ash" || probe.BusyBox.Contains(name)
                };
            }
        }
        return null;
    }

    /// <summary>
    /// The shell typed in the settings (e.g. "bash", "/bin/ash" or "bash --norc"); the probe, when known, tells whether it is
    /// BusyBox (on OpenWrt "sh" is). Null for an empty text.
    /// </summary>
    public static Choice ChooseManual(string shell, ProbeResult probe)
    {
        string command = shell?.Trim();
        if (string.IsNullOrEmpty(command))
        {
            return null;
        }
        string program = command.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0];
        string name = GetName(program);
        return new Choice
        {
            Command = command,
            IsAsh = name == "ash" || (probe != null && (probe.BusyBox.Contains(program) || probe.BusyBox.Contains(name)))
        };
    }

    public static string GetName(string program)
    {
        if (string.IsNullOrEmpty(program))
        {
            return "";
        }
        string first = program.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        int slash = first.LastIndexOf('/');
        return slash >= 0 ? first.Substring(slash + 1) : first;
    }

    /// <summary>
    /// The command line that runs <paramref name="command"/> the way an interactive shell would, with its aliases and functions.
    /// bash and the other shells get "-i -c". BusyBox ash is not started with -i, as it would print its banner before the output
    /// of every command; it reads the file of $ENV instead, which is all -i adds to ash. A shell started by ssh for a command
    /// is no login shell, so $ENV is usually not set: OpenWrt sets it to /etc/shinit (with "ll" and the other aliases) only in
    /// /etc/profile, hence that file by default. The command comes on a line of its own, since ash expands an alias only in
    /// the lines read after the alias was defined.
    /// </summary>
    public static string WrapInteractive(Choice shell, string command)
    {
        if (shell.IsAsh)
        {
            string script = "[ -r \"${ENV:-/etc/shinit}\" ] && . \"${ENV:-/etc/shinit}\"\n" + command;
            return shell.Command + " -c \"" + RemotePathUtils.EscapeForDoubleQuotes(script) + "\"";
        }
        return shell.Command + " -i -c \"" + RemotePathUtils.EscapeForDoubleQuotes(command) + "\"";
    }

    /// <summary>
    /// The history file to read and append to: the one of the settings if the user changed it, otherwise the file the shell
    /// itself keeps (~/.ash_history for BusyBox ash, as on OpenWrt; ~/.zsh_history; ~/.bash_history for bash and the rest).
    /// </summary>
    public static string GetHistoryFile(string configured, Choice shell)
    {
        if (!string.IsNullOrWhiteSpace(configured) && configured.Trim() != LegacyHistoryFile)
        {
            return configured.Trim();
        }
        if (shell != null)
        {
            if (shell.IsAsh)
            {
                return "~/.ash_history";
            }
            if (shell.Name == "zsh")
            {
                return "~/.zsh_history";
            }
        }
        return LegacyHistoryFile;
    }
}
