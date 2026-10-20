using System;
using System.Collections.Generic;
using System.Linq;
using SmarTTY.Terminal;

// Regression harness for the command line handling of the Smart Terminal (Terminal/ShellCommandLine.cs):
//  * tokenizing with quotes, backslashes and operators,
//  * telling a command name from a path or a cd argument (completion target),
//  * quoting completed paths and reading back what was typed,
//  * joining pasted lines, export/unset parsing, history file lines, "~" in the prompt.
// Run with: dotnet run --project tests/SmartTerminalCommandLineTests
internal static class Program
{
    private static int _passed;

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static void Equal<T>(T actual, T expected, string name)
    {
        bool equal = EqualityComparer<T>.Default.Equals(actual, expected);
        Check(equal, name + (equal ? "" : $" (expected '{expected}', got '{actual}')"));
    }

    private static string Words(string text)
    {
        return string.Join("|", ShellCommandLine.Tokenize(text).Select(token => (token.IsOperator ? "op:" : "") + token.Text));
    }

    private static void TokenizeChecks()
    {
        Equal(Words(""), "", "empty line has no tokens");
        Equal(Words("ls  -l   /tmp"), "ls|-l|/tmp", "spaces separate words");
        Equal(Words("cat \"my file\" next"), "cat|\"my file\"|next", "double quotes keep spaces");
        Equal(Words("cat 'my file' next"), "cat|'my file'|next", "single quotes keep spaces");
        Equal(Words("cat my\\ file next"), "cat|my\\ file|next", "an escaped space stays in the word");
        Equal(Words("echo 'a\"b' \"c'd\""), "echo|'a\"b'|\"c'd\"", "the other quote is literal inside quotes");
        Equal(Words("ls|grep x"), "ls|op:||grep|x", "a pipe without spaces is an operator");
        Equal(Words("make && ./run; echo done &"), "make|op:&&|./run|op:;|echo|done|op:&", "&& ; and & are operators");
        Equal(Words("ls 2>/dev/null"), "ls|2|op:>|/dev/null", "redirection");
        Equal(Words("echo \"a|b\" 'c;d' e\\&f"), "echo|\"a|b\"|'c;d'|e\\&f", "quoted and escaped operators are text");
        Equal(Words("cat \"unterminated quote"), "cat|\"unterminated quote", "an unterminated quote runs to the end");
        List<ShellCommandLine.Token> tokens = ShellCommandLine.Tokenize("a  bc");
        Check(tokens[1].Start == 3 && tokens[1].End == 5, "token positions");
        Check(ShellCommandLine.HasSeparators(ShellCommandLine.Tokenize("cd /tmp && ls")), "cd && ls is compound");
        Check(!ShellCommandLine.HasSeparators(ShellCommandLine.Tokenize("cd '/tmp/a;b'")), "a quoted ; is not a separator");
        Check(!ShellCommandLine.HasSeparators(ShellCommandLine.Tokenize("cd /tmp 2>/dev/null")), "a redirection is not a separator");
    }

    private static void Target(string text, int caret, CompletionTarget target, int start, int end, string command, string name)
    {
        ShellCommandLine.CompletionContext context = ShellCommandLine.Analyze(text, caret);
        bool ok = context.Target == target && context.TokenStart == start && context.TokenEnd == end && context.CommandName == command;
        Check(ok, name + (ok ? "" : $" (got {context.Target} {context.TokenStart}..{context.TokenEnd} command '{context.CommandName}')"));
    }

    private static void AnalyzeChecks()
    {
        Target("", 0, CompletionTarget.Command, 0, 0, null, "empty line: command");
        Target("gi", 2, CompletionTarget.Command, 0, 2, null, "first word: command");
        Target("ls /us", 6, CompletionTarget.Path, 3, 6, "ls", "argument: path");
        Target("ls ", 3, CompletionTarget.Path, 3, 3, "ls", "after a space: empty path word at the caret");
        Target("cd /us", 6, CompletionTarget.Directory, 3, 6, "cd", "cd argument: directory");
        Target("ls | gr", 7, CompletionTarget.Command, 5, 7, null, "after a pipe: command");
        Target("ls | ", 5, CompletionTarget.Command, 5, 5, null, "empty word after a pipe is a command at the caret, not at the line start");
        Target("ls|gr", 5, CompletionTarget.Command, 3, 5, null, "pipe without spaces");
        Target("make && ./ru", 12, CompletionTarget.Command, 8, 12, null, "after &&: command");
        Target("sudo apt inst", 13, CompletionTarget.Path, 9, 13, "apt", "sudo is skipped when looking for the command");
        Target("sudo ap", 7, CompletionTarget.Command, 5, 7, null, "word after sudo: command");
        Target("LANG=C so", 9, CompletionTarget.Command, 7, 9, null, "word after an assignment: command");
        Target("sort > out", 10, CompletionTarget.Path, 7, 10, "sort", "redirection target: path");
        Target("cat 'my fi", 10, CompletionTarget.Path, 4, 10, "cat", "quoted word with a space");
        Target("ls   | grep", 4, CompletionTarget.Path, 4, 4, "ls", "caret between words in the middle of the line");
        Target("echo $(whi", 10, CompletionTarget.Command, 7, 10, null, "inside $(: command");
        Check(ShellCommandLine.Analyze("sudo apt inst", 13).CommandStart == 5, "command start skips sudo");
    }

    private static void QuotingChecks()
    {
        Equal(ShellCommandLine.Unquote("my\\ file"), "my file", "unquote: escaped space");
        Equal(ShellCommandLine.Unquote("\"my file"), "my file", "unquote: unterminated double quote");
        Equal(ShellCommandLine.Unquote("'it''s'"), "its", "unquote: adjacent single quotes");
        Equal(ShellCommandLine.Unquote("\"a\\\"b\\x\""), "a\"b\\x", "unquote: backslash in double quotes escapes only backslash, quote, dollar, backtick");
        Equal(ShellCommandLine.Unquote("dir/"), "dir/", "unquote: plain word");
        Equal(ShellCommandLine.Unquote("abc\\"), "abc", "unquote: trailing backslash");
        Equal(ShellCommandLine.EscapeWord("my file (1).txt"), "my\\ file\\ \\(1\\).txt", "escape: spaces and parentheses");
        Equal(ShellCommandLine.EscapeWord("it's$x&y;z"), "it\\'s\\$x\\&y\\;z", "escape: quote, $, & and ;");
        Equal(ShellCommandLine.EscapeWord("~/my dir/"), "~/my\\ dir/", "escape: a leading ~/ stays expandable");
        Equal(ShellCommandLine.EscapeWord("~user"), "\\~user", "escape: ~name is a literal file name");
        Equal(ShellCommandLine.EscapeWord("plain-name_1.txt"), "plain-name_1.txt", "escape: nothing to escape");
        Equal(ShellCommandLine.EscapeInQuotes("a\"b$c", '"'), "a\\\"b\\$c", "escape inside double quotes");
        Equal(ShellCommandLine.EscapeInQuotes("it's", '\''), "it'\\''s", "escape inside single quotes");
        foreach (string literal in new[] { "a b", "x'y\"z", "$HOME", "semi;colon", "back\\slash", "~/x y" })
        {
            Equal(ShellCommandLine.Unquote(ShellCommandLine.EscapeWord(literal)), literal, "escape/unquote round trip: " + literal);
        }
    }

    private static void HomeChecks()
    {
        Equal(ShellCommandLine.AbbreviateHome("/home/user", "/home/user"), "~", "home itself");
        Equal(ShellCommandLine.AbbreviateHome("/home/user/src", "/home/user"), "~/src", "below home");
        Equal(ShellCommandLine.AbbreviateHome("/home/user2", "/home/user"), "/home/user2", "a sibling with the same prefix is not home");
        Equal(ShellCommandLine.AbbreviateHome("/etc", "/"), "/etc", "home = / is not abbreviated");
        Equal(ShellCommandLine.AbbreviateHome("/root/x", "/root/"), "~/x", "home with a trailing slash");
    }

    private static void PasteChecks()
    {
        Equal(ShellCommandLine.JoinPastedLines("ls -l\n"), "ls -l", "trailing line break is dropped");
        Equal(ShellCommandLine.JoinPastedLines("cd /tmp\r\nls\r\n"), "cd /tmp; ls", "lines run one after another");
        Equal(ShellCommandLine.JoinPastedLines("make \\\n  all"), "make   all", "backslash continuation");
        Equal(ShellCommandLine.JoinPastedLines("cat x |\n  grep y"), "cat x | grep y", "a line ending with a pipe");
        Equal(ShellCommandLine.JoinPastedLines("make &&\nrun"), "make && run", "a line ending with &&");
        Equal(ShellCommandLine.JoinPastedLines("sleep 1 &\necho"), "sleep 1 & echo", "a line ending with &");
        Equal(ShellCommandLine.JoinPastedLines("for f in *; do\necho $f\ndone"), "for f in *; do echo $f; done", "a loop");
        Equal(ShellCommandLine.JoinPastedLines("\n\na\n\n\nb\n"), "a; b", "blank lines are skipped");
        Equal(ShellCommandLine.JoinPastedLines("a\tb"), "a b", "tabs become spaces");
        Equal(ShellCommandLine.JoinPastedLines("\\\n\nx"), "x", "an empty continued line does not break joining");
    }

    private static void VariableChecks()
    {
        Check(ShellCommandLine.TryParseVariableCommand("export A=1 B=\"x y\"", out List<KeyValuePair<string, string>> assignments), "export with two assignments");
        Check(assignments.Count == 2 && assignments[0].Key == "A" && assignments[0].Value == "1" && assignments[1].Key == "B" && assignments[1].Value == "\"x y\"", "values are kept as typed");
        Check(ShellCommandLine.TryParseVariableCommand("export PATH=$PATH:/opt/bin", out assignments) && assignments[0].Value == "$PATH:/opt/bin", "a value that refers to a variable");
        Check(ShellCommandLine.TryParseVariableCommand("export EMPTY=", out assignments) && assignments[0].Value == "", "an empty value is a value, not unset");
        Check(ShellCommandLine.TryParseVariableCommand("unset A B", out assignments) && assignments.Count == 2 && assignments[1].Value == null, "unset");
        Check(!ShellCommandLine.TryParseVariableCommand("export -p", out _), "export -p runs remotely");
        Check(!ShellCommandLine.TryParseVariableCommand("export", out _), "export without arguments runs remotely");
        Check(!ShellCommandLine.TryParseVariableCommand("export 1A=x", out _), "an invalid name runs remotely");
        Check(!ShellCommandLine.TryParseVariableCommand("export A=1 && ls", out _), "a compound command runs remotely");
        Check(!ShellCommandLine.TryParseVariableCommand("unset -f func", out _), "unset -f runs remotely");
        Check(ShellCommandLine.IsAssignment("LANG=C") && !ShellCommandLine.IsAssignment("=x") && !ShellCommandLine.IsAssignment("a-b=1"), "assignment detection");
    }

    private static void HistoryChecks()
    {
        Equal(ShellCommandLine.ParseHistoryLine("ls -l"), "ls -l", "a plain line");
        Equal(ShellCommandLine.ParseHistoryLine("#1700000000"), null, "a bash time stamp is not a command");
        Equal(ShellCommandLine.ParseHistoryLine("#comment"), "#comment", "a comment line is kept");
        Equal(ShellCommandLine.ParseHistoryLine(": 1700000000:0;git status"), "git status", "zsh extended history");
        Equal(ShellCommandLine.ParseHistoryLine(": not zsh"), ": not zsh", "a ':' command");
        Equal(ShellCommandLine.ParseHistoryLine("   "), null, "a blank line");
    }

    private static int Main()
    {
        try
        {
            TokenizeChecks();
            AnalyzeChecks();
            QuotingChecks();
            HomeChecks();
            PasteChecks();
            VariableChecks();
            HistoryChecks();
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }
        Console.WriteLine($"All {_passed} checks passed.");
        return 0;
    }
}
