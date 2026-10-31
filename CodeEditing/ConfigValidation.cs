using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace SmarTTY.CodeEditing;

/// <summary>One problem that a check found: the line is counted from 1, the column from 1 (0 when it is not known).</summary>
public sealed class ValidationIssue
{
    public int Line;

    public int Column;

    public string Message;
}

public enum ValidationOutcome
{
    /// <summary>The program of the check is not on the server, did not run, or timed out.</summary>
    Unavailable,

    /// <summary>The check ran and accepted the file.</summary>
    Passed,

    /// <summary>The check ran and named at least one line that it rejects.</summary>
    Failed,

    /// <summary>The check failed, but said nothing that points to a line (a missing permission, a missing host key, ...).</summary>
    Inconclusive
}

public sealed class ValidationResult
{
    public ValidationOutcome Outcome;

    public List<ValidationIssue> Issues = new List<ValidationIssue>();
}

/// <summary>A command that makes a service read its configuration again after the file was saved.</summary>
public sealed class ServiceReload
{
    /// <summary>Shown in the questions, e.g. "sshd".</summary>
    public string Name;

    /// <summary>A command for a POSIX shell. It checks the configuration itself first where the service can do that.</summary>
    public string Command;
}

/// <summary>
/// A syntax or configuration check that the editor runs on the server before it replaces the file: the text is
/// uploaded to a temporary file first, the check is run on that file, and the real file is only written when the
/// check does not reject it.
///
/// A check blocks the save only when it exits with an error and names a line it does not accept. Anything else (the
/// program is missing, it needs more rights than the user has, it complains about the host rather than the file)
/// lets the save go on, so a check that cannot run properly on some server never stands in the way.
///
/// The class is free of any UI dependency, so the test harness can check it without a display.
/// </summary>
public sealed class ConfigValidator
{
    // What the temporary file's path is replaced with in the output of a check.
    private const string FileToken = "<file>";

    private const int MaxIssues = 50;

    private sealed class Candidate
    {
        public string Program;

        public string Arguments;
    }

    private readonly List<Candidate> _candidates = new List<Candidate>();

    /// <summary>What the messages call the check, e.g. "bash -n".</summary>
    public string Name { get; }

    public ConfigValidator(string name)
    {
        Name = name;
    }

    /// <summary>
    /// Adds a way to run the check. The checks are tried in the order added, and the first whose program exists on
    /// the server is used. <paramref name="arguments"/> holds {FILE} where the path of the file goes.
    /// </summary>
    public ConfigValidator Add(string program, string arguments)
    {
        _candidates.Add(new Candidate { Program = program, Arguments = arguments });
        return this;
    }

    /// <summary>
    /// The command line for a POSIX shell. <paramref name="quotedFile"/> is the path of the file, already quoted for the
    /// shell. The command exits with 127 when none of the programs exists, otherwise with the status of the first
    /// program found; the output of the program is on the standard output (stderr merged in).
    /// </summary>
    public string BuildCommand(string quotedFile)
    {
        StringBuilder command = new StringBuilder();
        foreach (Candidate candidate in _candidates)
        {
            command.Append("if command -v ").Append(candidate.Program).Append(" >/dev/null 2>&1; then ")
                .Append(candidate.Program).Append(' ').Append(candidate.Arguments.Replace("{FILE}", quotedFile))
                .Append(" 2>&1; exit $?; fi; ");
        }
        command.Append("exit 127");
        return command.ToString();
    }

    /// <summary>Reads the result of <see cref="BuildCommand"/>: its output and its exit status (null: it did not run).</summary>
    public ValidationResult Interpret(IEnumerable<string> output, int? exitStatus, string temporaryPath)
    {
        ValidationResult result = new ValidationResult();
        if (output == null || exitStatus == null || exitStatus.Value == 127)
        {
            result.Outcome = ValidationOutcome.Unavailable;
            return result;
        }
        if (exitStatus.Value == 0)
        {
            result.Outcome = ValidationOutcome.Passed;
            return result;
        }
        result.Issues = ConfigValidators.ParseIssues(output, temporaryPath);
        result.Outcome = result.Issues.Count > 0 ? ValidationOutcome.Failed : ValidationOutcome.Inconclusive;
        return result;
    }

    internal static string Token => FileToken;

    internal static int IssueLimit => MaxIssues;
}

/// <summary>Chooses the check for a file and reads what the checks print.</summary>
public static class ConfigValidators
{
    // nginx: [emerg] unknown directive "x" in <file>:5
    private static readonly Regex TrailingLocation = new Regex(@"^(?<msg>.*?)\s+in\s+<file>:(?<line>\d+)\s*$", RegexOptions.CultureInvariant);

    // The forms of "<file>" with a line number that the common checks print, from the most to the least specific.
    private static readonly Regex[] LinePatterns =
    {
        // 5:3: message (what the checks written in Python print)
        new Regex(@"^(?<line>\d+):(?<col>\d+): (?<msg>.*)$", RegexOptions.CultureInvariant),
        // <file>:5:3: message (visudo, gcc style)
        new Regex(@"<file>:(?<line>\d+):(?<col>\d+):?\s*(?<msg>.*)$", RegexOptions.CultureInvariant),
        // <file>:5: message (xmllint, jq)
        new Regex(@"<file>:(?<line>\d+):?\s*(?<msg>.*)$", RegexOptions.CultureInvariant),
        // <file>: line 5: message (bash), <file> line 5: message (sshd)
        new Regex(@"<file>:?\s+line\s+(?<line>\d+):\s*(?<msg>.*)$", RegexOptions.CultureInvariant),
        // <file>: 5: Syntax error (dash)
        new Regex(@"<file>:\s+(?<line>\d+):\s*(?<msg>.*)$", RegexOptions.CultureInvariant),
        // syntax error near line 5 (old visudo)
        new Regex(@"near line (?<line>\d+)", RegexOptions.CultureInvariant),
        // ... at line 3, column 5 (jq)
        new Regex(@"\bline (?<line>\d+)(?:,? column (?<col>\d+))?", RegexOptions.CultureInvariant)
    };

    // The checks written in Python print one "line:column: message" line for a problem of the file. Anything else that
    // goes wrong in them (the file cannot be read, the module is missing) ends with the status 127, "not available",
    // so that a traceback is never read as a problem of the file.
    private const string PythonSyntaxCheck =
        "import ast,sys\n" +
        "try:\n" +
        "    ast.parse(open(sys.argv[1],\"rb\").read(),sys.argv[1])\n" +
        "except SyntaxError as e:\n" +
        "    print(\"%s:%s: %s\" % (e.lineno,e.offset or 0,e.msg))\n" +
        "    sys.exit(1)\n" +
        "except Exception:\n" +
        "    sys.exit(127)";

    private const string PythonJsonCheck =
        "import json,sys\n" +
        "try:\n" +
        "    json.load(open(sys.argv[1],\"rb\"))\n" +
        "except ValueError as e:\n" +
        "    print(\"%s:%s: %s\" % (getattr(e,\"lineno\",1),getattr(e,\"colno\",0),getattr(e,\"msg\",str(e))))\n" +
        "    sys.exit(1)\n" +
        "except Exception:\n" +
        "    sys.exit(127)";

    // compose_all only parses: tags that need a constructor (!Ref, !vault) are not an error.
    private const string PythonYamlCheck =
        "import sys\n" +
        "try:\n" +
        "    import yaml\n" +
        "except Exception:\n" +
        "    sys.exit(127)\n" +
        "try:\n" +
        "    list(yaml.compose_all(open(sys.argv[1],\"rb\")))\n" +
        "except yaml.YAMLError as e:\n" +
        "    m = getattr(e,\"problem_mark\",None)\n" +
        "    if m is None:\n" +
        "        sys.exit(127)\n" +
        "    print(\"%d:%d: %s\" % (m.line+1,m.column+1,getattr(e,\"problem\",None) or \"invalid YAML\"))\n" +
        "    sys.exit(1)\n" +
        "except Exception:\n" +
        "    sys.exit(127)";

    /// <summary>
    /// The check for a file, or null when there is none. The file name and its directory pick the checks of the services
    /// (sshd_config, nginx.conf, sudoers); the language picks the syntax check of the code.
    /// </summary>
    public static ConfigValidator Find(string remotePath, SyntaxHighlighting.Language language, string firstLine)
    {
        if (string.IsNullOrEmpty(remotePath))
        {
            return null;
        }
        string path = remotePath.Replace('\\', '/').ToLowerInvariant();
        string name = path.Substring(path.LastIndexOf('/') + 1);

        if (name == "sshd_config")
        {
            return new ConfigValidator("sshd -t")
                .Add("sshd", "-t -f {FILE}")
                .Add("/usr/sbin/sshd", "-t -f {FILE}");
        }
        if (name == "nginx.conf")
        {
            return new ConfigValidator("nginx -t")
                .Add("nginx", "-t -c {FILE}")
                .Add("/usr/sbin/nginx", "-t -c {FILE}");
        }
        if (name == "sudoers" || path.Contains("/sudoers.d/"))
        {
            return new ConfigValidator("visudo -c")
                .Add("visudo", "-c -f {FILE}")
                .Add("/usr/sbin/visudo", "-c -f {FILE}");
        }

        switch (language)
        {
            case SyntaxHighlighting.Language.Bash:
                return ShellCheck(name, firstLine);
            case SyntaxHighlighting.Language.Python:
                return new ConfigValidator("python3 syntax check")
                    .Add("python3", "-c '" + PythonSyntaxCheck + "' {FILE}");
            case SyntaxHighlighting.Language.Json:
                // A .jsonc file (or any other that is not plain JSON) may hold comments, which these checks reject.
                if (name.EndsWith(".json", StringComparison.Ordinal))
                {
                    return new ConfigValidator("JSON check")
                        .Add("jq", "empty {FILE}")
                        .Add("python3", "-c '" + PythonJsonCheck + "' {FILE}");
                }
                return null;
            case SyntaxHighlighting.Language.Yaml:
                return new ConfigValidator("YAML check")
                    .Add("python3", "-c '" + PythonYamlCheck + "' {FILE}");
            case SyntaxHighlighting.Language.Markup:
                if (name.EndsWith(".xml", StringComparison.Ordinal))
                {
                    return new ConfigValidator("xmllint --noout")
                        .Add("xmllint", "--noout {FILE}");
                }
                return null;
            default:
                return null;
        }
    }

    private static ConfigValidator ShellCheck(string lowerCaseName, string firstLine)
    {
        // zsh syntax is not what bash or sh accept: no check is better than a wrong one.
        if (lowerCaseName.StartsWith(".zsh", StringComparison.Ordinal) || lowerCaseName == ".zprofile")
        {
            return null;
        }
        ConfigValidator validator = new ConfigValidator("shell -n");
        foreach (string program in ShellsFor(firstLine))
        {
            validator.Add(program, "-n {FILE}");
        }
        return validator;
    }

    /// <summary>
    /// The shells that can check a script, from the best to the last resort: the one the "#!" line names (a script for
    /// sh is checked with sh, which does not know bash's extensions), otherwise bash and then sh.
    /// </summary>
    public static IReadOnlyList<string> ShellsFor(string firstLine)
    {
        if (!string.IsNullOrEmpty(firstLine) && firstLine.StartsWith("#!", StringComparison.Ordinal))
        {
            string[] words = firstLine.Substring(2).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            string program = words.Length > 0 ? ProgramName(words[0]) : "";
            if (program == "env")
            {
                program = "";
                for (int i = 1; i < words.Length; i++)
                {
                    if (!words[i].StartsWith("-", StringComparison.Ordinal))
                    {
                        program = ProgramName(words[i]);
                        break;
                    }
                }
            }
            if (program == "busybox" && words.Length > 1)
            {
                program = ProgramName(words[1]);
            }
            switch (program)
            {
                case "bash":
                    return new[] { "bash", "sh" };
                case "ksh":
                case "mksh":
                    return new[] { program, "sh" };
                case "sh":
                case "ash":
                case "dash":
                    return new[] { "sh" };
            }
        }
        return new[] { "bash", "sh" };
    }

    private static string ProgramName(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash >= 0 ? path.Substring(slash + 1) : path;
    }

    /// <summary>
    /// What to offer after a file was saved so that the service reads it again, or null when the file is not the
    /// configuration of a service that is known.
    /// </summary>
    public static ServiceReload FindReload(string remotePath)
    {
        if (string.IsNullOrEmpty(remotePath))
        {
            return null;
        }
        string path = remotePath.Replace('\\', '/').ToLowerInvariant();
        string name = path.Substring(path.LastIndexOf('/') + 1);
        if (name == "sshd_config")
        {
            return new ServiceReload
            {
                Name = "sshd",
                // sshd -t first: a reload does not drop the sessions that are open, but a broken file would lock out the next one.
                Command = "S=$(command -v sshd || echo /usr/sbin/sshd); \"$S\" -t && { systemctl reload sshd || systemctl reload ssh || service sshd reload || service ssh reload; }"
            };
        }
        if (path.Contains("/etc/nginx/") && name.EndsWith(".conf", StringComparison.Ordinal))
        {
            return new ServiceReload
            {
                Name = "nginx",
                Command = "N=$(command -v nginx || echo /usr/sbin/nginx); \"$N\" -t && { systemctl reload nginx || \"$N\" -s reload || service nginx reload; }"
            };
        }
        return null;
    }

    /// <summary>
    /// The problems named in the output of a check. The path of the temporary file is replaced by a token first, so
    /// that the forms "file:5:", "file: line 5:" and "line 5, column 3" can be told apart whatever the directory is.
    /// Only one problem is kept per line (the first one, as a check also prints the offending text), in line order.
    /// </summary>
    public static List<ValidationIssue> ParseIssues(IEnumerable<string> output, string temporaryPath)
    {
        SortedDictionary<int, ValidationIssue> byLine = new SortedDictionary<int, ValidationIssue>();
        if (output != null)
        {
            foreach (string raw in output)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }
                string line = string.IsNullOrEmpty(temporaryPath) ? raw : raw.Replace(temporaryPath, ConfigValidator.Token);
                ValidationIssue issue = ParseLine(line);
                if (issue != null && issue.Line > 0 && !byLine.ContainsKey(issue.Line))
                {
                    byLine[issue.Line] = issue;
                    if (byLine.Count >= ConfigValidator.IssueLimit)
                    {
                        break;
                    }
                }
            }
        }
        return new List<ValidationIssue>(byLine.Values);
    }

    private static ValidationIssue ParseLine(string line)
    {
        line = line.Trim();
        Match match = TrailingLocation.Match(line);
        if (match.Success)
        {
            return MakeIssue(match, line, wholeLineIfNoMessage: true);
        }
        foreach (Regex pattern in LinePatterns)
        {
            match = pattern.Match(line);
            if (match.Success)
            {
                return MakeIssue(match, line, wholeLineIfNoMessage: true);
            }
        }
        return null;
    }

    private static ValidationIssue MakeIssue(Match match, string line, bool wholeLineIfNoMessage)
    {
        if (!int.TryParse(match.Groups["line"].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int number))
        {
            return null;
        }
        int column = 0;
        if (match.Groups["col"].Success)
        {
            int.TryParse(match.Groups["col"].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out column);
        }
        string message = match.Groups["msg"].Success ? match.Groups["msg"].Value.Trim(' ', ':', ')', '-') : "";
        if (message.Length == 0 && wholeLineIfNoMessage)
        {
            message = line.Replace(ConfigValidator.Token, "file").Trim();
        }
        if (message.Length > 300)
        {
            message = message.Substring(0, 300) + "...";
        }
        return new ValidationIssue { Line = number, Column = column, Message = message };
    }
}
