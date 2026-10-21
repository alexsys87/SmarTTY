using System;
using System.Collections.Generic;
using System.Linq;

namespace SmarTTY.Terminal;

/// <summary>
/// Remote commands and output parsers for the Smart Terminal's completion of aliases, shell functions, variables and
/// host names.
/// </summary>
internal static class CompletionSources
{
    public const string FunctionsMarker = "___SMARTTY_FUNCTIONS___";

    public const string VariablesMarker = "___SMARTTY_VARIABLES___";

    public const string FileMarker = "___SMARTTY_FILE___ ";

    public sealed class ShellDefinitions
    {
        /// <summary>Alias name and what it stands for.</summary>
        public List<KeyValuePair<string, string>> Aliases = new List<KeyValuePair<string, string>>();

        public List<string> Functions = new List<string>();

        public List<string> Variables = new List<string>();
    }

    public sealed class HostEntry
    {
        public string Name;

        /// <summary>Where it was found: "ssh config", "known host" or "/etc/hosts".</summary>
        public string Source;
    }

    /// <summary>
    /// A script for the interactive shell (bash -i -c, which reads .bashrc) that prints its aliases, then its functions
    /// and variables after the markers. zsh is handled as well; other shells only report aliases and the environment.
    /// </summary>
    public static string BuildShellDefinitionsScript()
    {
        return "alias; echo " + FunctionsMarker + "; " +
               "if [ -n \"$BASH_VERSION\" ]; then compgen -A function; elif [ -n \"$ZSH_VERSION\" ]; then print -l ${(k)functions}; fi; " +
               "echo " + VariablesMarker + "; " +
               "if [ -n \"$BASH_VERSION\" ]; then compgen -v; elif [ -n \"$ZSH_VERSION\" ]; then print -l ${(k)parameters}; else env; fi";
    }

    /// <summary>Prints the readable ones of ~/.ssh/config, ~/.ssh/known_hosts and /etc/hosts, each after a marker line.</summary>
    public static string BuildHostFilesCommand()
    {
        return "for f in ~/.ssh/config ~/.ssh/known_hosts /etc/hosts; do if [ -r \"$f\" ]; then echo \"" + FileMarker + "$f\"; cat \"$f\"; fi; done";
    }

    public static ShellDefinitions ParseShellDefinitions(IEnumerable<string> lines)
    {
        ShellDefinitions definitions = new ShellDefinitions();
        int section = 0;
        HashSet<string> aliasNames = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> functions = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> variables = new HashSet<string>(StringComparer.Ordinal);
        foreach (string rawLine in lines ?? Enumerable.Empty<string>())
        {
            string line = (rawLine ?? "").TrimEnd('\r');
            if (line == FunctionsMarker)
            {
                section = 1;
                continue;
            }
            if (line == VariablesMarker)
            {
                section = 2;
                continue;
            }
            switch (section)
            {
                case 0:
                    if (TryParseAlias(line, out string name, out string value) && aliasNames.Add(name))
                    {
                        definitions.Aliases.Add(new KeyValuePair<string, string>(name, value));
                    }
                    break;
                case 1:
                    {
                        string function = line.Trim();
                        // "declare -f name" (bash's declare -F) as well as a bare name.
                        if (function.StartsWith("declare -f ", StringComparison.Ordinal))
                        {
                            function = function.Substring("declare -f ".Length).Trim();
                        }
                        if (IsCommandName(function) && functions.Add(function))
                        {
                            definitions.Functions.Add(function);
                        }
                        break;
                    }
                default:
                    {
                        // A name per line (compgen -v), or NAME=value lines of env; continuation lines of multi-line
                        // values are not names.
                        int equals = line.IndexOf('=');
                        string variable = equals == -1 ? line.Trim() : line.Substring(0, equals);
                        if (ShellCommandLine.IsValidName(variable) && variables.Add(variable))
                        {
                            definitions.Variables.Add(variable);
                        }
                        break;
                    }
            }
        }
        definitions.Variables.Sort(StringComparer.Ordinal);
        return definitions;
    }

    /// <summary>"alias ll='ls -alF'" (bash) or "ll='ls -alF'" (dash, ash, zsh).</summary>
    public static bool TryParseAlias(string line, out string name, out string value)
    {
        name = value = null;
        line = (line ?? "").Trim();
        if (line.StartsWith("alias ", StringComparison.Ordinal))
        {
            line = line.Substring("alias ".Length).TrimStart();
        }
        int equals = line.IndexOf('=');
        if (equals <= 0)
        {
            return false;
        }
        name = line.Substring(0, equals);
        if (!IsCommandName(name))
        {
            name = null;
            return false;
        }
        value = ShellCommandLine.Unquote(line.Substring(equals + 1));
        return true;
    }

    // Alias and function names may contain '-', '.', ':' and the like, but no blanks, quotes or shell operators.
    private static bool IsCommandName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }
        foreach (char ch in name)
        {
            if (char.IsWhiteSpace(ch) || "'\"`$=|&;<>()\\".IndexOf(ch) != -1)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Host names from the output of <see cref="BuildHostFilesCommand"/>, without duplicates.</summary>
    public static List<HostEntry> ParseHostFiles(IEnumerable<string> lines)
    {
        List<HostEntry> hosts = new List<HostEntry>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string file = null;
        foreach (string rawLine in lines ?? Enumerable.Empty<string>())
        {
            string line = (rawLine ?? "").TrimEnd('\r');
            if (line.StartsWith(FileMarker, StringComparison.Ordinal))
            {
                file = line.Substring(FileMarker.Length);
                continue;
            }
            if (file == null)
            {
                continue;
            }
            IEnumerable<string> names;
            string source;
            if (file.EndsWith("/config", StringComparison.Ordinal))
            {
                names = ParseSshConfigLine(line);
                source = "ssh config";
            }
            else if (file.EndsWith("/known_hosts", StringComparison.Ordinal))
            {
                names = ParseKnownHostsLine(line);
                source = "known host";
            }
            else
            {
                names = ParseEtcHostsLine(line);
                source = "/etc/hosts";
            }
            foreach (string name in names)
            {
                if (seen.Add(name))
                {
                    hosts.Add(new HostEntry
                    {
                        Name = name,
                        Source = source
                    });
                }
            }
        }
        return hosts;
    }

    /// <summary>The names of a "Host a b c" line; patterns (*, ?, !) are not hosts that can be typed.</summary>
    public static IEnumerable<string> ParseSshConfigLine(string line)
    {
        string trimmed = (line ?? "").Trim();
        if (trimmed.Length < 5 || !trimmed.StartsWith("host", StringComparison.OrdinalIgnoreCase) || !(char.IsWhiteSpace(trimmed[4]) || trimmed[4] == '='))
        {
            yield break;
        }
        foreach (string name in trimmed.Substring(5).Split(new[] { ' ', '\t', '=', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (name.IndexOfAny(new[] { '*', '?', '!' }) == -1)
            {
                yield return name.Trim('"');
            }
        }
    }

    /// <summary>
    /// The host names of a known_hosts line: "host1,host2 key-type key", "[host]:2222 ..." and "@marker host ...";
    /// hashed entries (|1|...) cannot be read back.
    /// </summary>
    public static IEnumerable<string> ParseKnownHostsLine(string line)
    {
        string trimmed = (line ?? "").Trim();
        if (trimmed.Length == 0 || trimmed[0] == '#')
        {
            yield break;
        }
        string[] fields = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        int hostField = (fields.Length > 0 && fields[0].StartsWith("@", StringComparison.Ordinal)) ? 1 : 0;
        if (fields.Length <= hostField)
        {
            yield break;
        }
        foreach (string entry in fields[hostField].Split(','))
        {
            string name = entry;
            if (name.Length == 0 || name.StartsWith("|", StringComparison.Ordinal) || name.IndexOfAny(new[] { '*', '?', '!' }) != -1)
            {
                continue;
            }
            if (name.StartsWith("[", StringComparison.Ordinal))
            {
                int close = name.IndexOf(']');
                if (close <= 1)
                {
                    continue;
                }
                name = name.Substring(1, close - 1);
            }
            yield return name;
        }
    }

    /// <summary>The names (not the address) of an /etc/hosts line.</summary>
    public static IEnumerable<string> ParseEtcHostsLine(string line)
    {
        string content = line ?? "";
        int comment = content.IndexOf('#');
        if (comment != -1)
        {
            content = content.Substring(0, comment);
        }
        string[] fields = content.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i < fields.Length; i++)
        {
            yield return fields[i];
        }
    }
}
