using SmarTTY.IO;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace SmarTTY.Terminal;

/// <summary>
/// Where the relative file names in the output of a build command are relative to. A compiler prints the names as
/// they were given to it, from the directory the build tool works in, which is not always the directory the command was
/// typed in: "make -C sub", "ninja -C build", "cmake --build build", "cd sub &amp;&amp; make", and the recursive makes that
/// announce "Entering directory '...'".
///
/// The class is free of any UI dependency, so the test harness can check it without a display.
/// </summary>
internal static class BuildDirectories
{
    // GNU make: make[1]: Entering directory '/home/u/proj/sub' (older versions: `/home/u/proj/sub'); ninja: ninja: Entering
    // directory `build'. Only the English messages are recognized.
    private static readonly Regex DirectoryMessage = new Regex(
        @"(?<what>Entering|Leaving) directory\s+[`'‘""](?<dir>[^'’""`]+)['’]$",
        RegexOptions.CultureInvariant);

    private static readonly HashSet<string> MakePrograms = new HashSet<string>(StringComparer.Ordinal)
    {
        "make", "gmake", "gnumake", "bmake", "ninja", "samu"
    };

    // Words in front of the program that do not change where it works.
    private static readonly HashSet<string> Wrappers = new HashSet<string>(StringComparer.Ordinal)
    {
        "sudo", "time", "nohup", "env", "nice", "command", "exec", "stdbuf"
    };

    private const char SeparatorMark = '\0';

    /// <summary>
    /// Reads a "Entering directory" or "Leaving directory" message at the end of <paramref name="text"/> (the output so
    /// far). The directory is as printed: it may be relative.
    /// </summary>
    public static bool TryParseMessage(string text, out bool entering, out string directory)
    {
        entering = false;
        directory = null;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        Match match = DirectoryMessage.Match(text);
        if (!match.Success)
        {
            return false;
        }
        entering = match.Groups["what"].Value == "Entering";
        directory = match.Groups["dir"].Value;
        return directory.Length > 0;
    }

    /// <summary>
    /// The directory that a command line makes its build tool work in, or null when it does not say (or says it in a way that
    /// cannot be followed: a variable, "~"). <paramref name="workingDirectory"/> is where the command was typed.
    /// </summary>
    public static string FromCommand(string commandText, string workingDirectory)
    {
        return Analyze(commandText, workingDirectory, out _);
    }

    /// <summary>
    /// Like <see cref="FromCommand"/>, and also tells where the programs of the command line start: the working directory,
    /// or the one a "cd" in front of them went to. A directory that ninja announces is relative to that one.
    /// </summary>
    public static string Analyze(string commandText, string workingDirectory, out string startDirectory)
    {
        startDirectory = workingDirectory;
        if (string.IsNullOrWhiteSpace(commandText) || string.IsNullOrEmpty(workingDirectory))
        {
            return null;
        }
        string current = workingDirectory;
        string result = null;
        foreach (List<string> words in SplitCommands(Tokenize(commandText)))
        {
            int index = 0;
            while (index < words.Count && (Wrappers.Contains(words[index]) || IsAssignment(words[index])))
            {
                index++;
            }
            if (index >= words.Count)
            {
                continue;
            }
            string program = ProgramName(words[index]);
            if (program == "cd")
            {
                string target = index + 1 < words.Count ? words[index + 1] : null;
                if (!IsFollowable(target) || target == "-")
                {
                    return null;
                }
                current = Resolve(current, target);
                startDirectory = current;
                result = current;
            }
            else if (MakePrograms.Contains(program))
            {
                string directory = null;
                for (int i = index + 1; i < words.Count; i++)
                {
                    string word = words[i];
                    if (word == "-C" || word == "--directory")
                    {
                        directory = i + 1 < words.Count ? words[i + 1] : null;
                        if (!IsFollowable(directory))
                        {
                            return null;
                        }
                        i++;
                    }
                    else if (word.StartsWith("--directory=", StringComparison.Ordinal))
                    {
                        directory = word.Substring("--directory=".Length);
                        if (!IsFollowable(directory))
                        {
                            return null;
                        }
                    }
                    else if (word.Length > 2 && word.StartsWith("-C", StringComparison.Ordinal))
                    {
                        directory = word.Substring(2);
                        if (!IsFollowable(directory))
                        {
                            return null;
                        }
                    }
                }
                if (directory != null)
                {
                    result = Resolve(current, directory);
                }
            }
            else if (program == "cmake")
            {
                int build = words.IndexOf("--build");
                if (build >= 0 && build + 1 < words.Count && !words[build + 1].StartsWith("-", StringComparison.Ordinal))
                {
                    if (!IsFollowable(words[build + 1]))
                    {
                        return null;
                    }
                    result = Resolve(current, words[build + 1]);
                }
            }
        }
        return result;
    }

    /// <summary>
    /// The path that a file name printed by a build tool stands for: "./" in front of it and "/./" inside it are dropped
    /// (a "." never changes where a path leads, unlike ".."), then it is put after <paramref name="directory"/> when it is
    /// relative.
    /// </summary>
    public static string Resolve(string directory, string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return directory;
        }
        string cleaned = path;
        while (cleaned.StartsWith("./", StringComparison.Ordinal))
        {
            cleaned = cleaned.Substring(2);
        }
        cleaned = cleaned.Replace("/./", "/");
        if (cleaned.Length == 0 || cleaned == ".")
        {
            return directory;
        }
        return RemotePathUtils.Combine(directory, cleaned);
    }

    private static bool IsFollowable(string directory)
    {
        return !string.IsNullOrEmpty(directory) && directory.IndexOfAny(new[] { '$', '`', '~', '*', '?' }) < 0;
    }

    private static bool IsAssignment(string word)
    {
        int equals = word.IndexOf('=');
        if (equals <= 0)
        {
            return false;
        }
        for (int i = 0; i < equals; i++)
        {
            char c = word[i];
            if (!(char.IsLetterOrDigit(c) || c == '_') || (i == 0 && char.IsDigit(c)))
            {
                return false;
            }
        }
        return true;
    }

    private static string ProgramName(string word)
    {
        int slash = word.LastIndexOf('/');
        return slash >= 0 ? word.Substring(slash + 1) : word;
    }

    private static List<List<string>> SplitCommands(List<string> tokens)
    {
        List<List<string>> commands = new List<List<string>>();
        List<string> current = new List<string>();
        foreach (string token in tokens)
        {
            if (token.Length > 0 && token[0] == SeparatorMark)
            {
                commands.Add(current);
                current = new List<string>();
            }
            else
            {
                current.Add(token);
            }
        }
        commands.Add(current);
        return commands;
    }

    /// <summary>
    /// Splits a command line into words (quotes and backslashes are followed) and the separators &amp;&amp;, ||, ;, | and &amp;, which
    /// are marked so that they cannot be taken for a word.
    /// </summary>
    private static List<string> Tokenize(string text)
    {
        List<string> tokens = new List<string>();
        StringBuilder word = new StringBuilder();
        bool hasWord = false;
        char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else if (c == '\\' && quote == '"' && i + 1 < text.Length)
                {
                    word.Append(text[++i]);
                }
                else
                {
                    word.Append(c);
                }
                continue;
            }
            if (c == '\'' || c == '"')
            {
                quote = c;
                hasWord = true;
                continue;
            }
            if (c == '\\' && i + 1 < text.Length)
            {
                word.Append(text[++i]);
                hasWord = true;
                continue;
            }
            if (char.IsWhiteSpace(c) || c == '&' || c == '|' || c == ';')
            {
                if (hasWord)
                {
                    tokens.Add(word.ToString());
                    word.Clear();
                    hasWord = false;
                }
                if (c == '&' || c == '|' || c == ';')
                {
                    if ((c == '&' || c == '|') && i + 1 < text.Length && text[i + 1] == c)
                    {
                        i++;
                    }
                    tokens.Add(SeparatorMark.ToString());
                }
                continue;
            }
            word.Append(c);
            hasWord = true;
        }
        if (hasWord)
        {
            tokens.Add(word.ToString());
        }
        return tokens;
    }
}
