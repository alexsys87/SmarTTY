using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SmarTTY.Terminal;

// Regression harness for the command line handling of the Smart Terminal (Terminal/ShellCommandLine.cs):
//  * tokenizing with quotes, backslashes and operators,
//  * telling a command name from a path or a cd argument (completion target),
//  * quoting completed paths and reading back what was typed,
//  * joining pasted lines, export/unset parsing, history file lines, "~" in the prompt,
//  * the bash-like history search (Ctrl+R), the grey suggestion from the history, $variables and host names,
//  * parsing aliases, functions, variables and host files for completion,
//  * choosing the shell of the interactive wrapper (bash, or BusyBox ash as on OpenWrt) and its history file; the probe
//    and the wrapper are run by the system shells and by BusyBox when it is installed.
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
        Equal(ShellCommandLine.EscapeWord("a\nb"), "$'a\\nb'", "escape: a line break in a name is spelled out, not typed");
        Equal(ShellCommandLine.EscapeWord("it's\tx\u0001"), "$'it\\'s\\tx\\x01'", "escape: other control characters and a quote in the $'...' form");
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
        Equal(ShellCommandLine.JoinPastedLines("opkg install foo bar\n    baz qux\n"), "opkg install foo bar baz qux", "a wrapped line indented deeper is the same command");
        Equal(ShellCommandLine.JoinPastedLines("opkg install a\n    b\n    c\n"), "opkg install a b c", "several wrapped lines");
        Equal(ShellCommandLine.JoinPastedLines("    cd /tmp\n    ls\n"), "cd /tmp; ls", "lines with the same indent are separate commands");
        Equal(ShellCommandLine.JoinPastedLines("tar -c dir\n--exclude x\n"), "tar -c dir --exclude x", "a line that starts with an option");
        Equal(ShellCommandLine.JoinPastedLines("cat a\n| grep b\n"), "cat a | grep b", "a line that starts with a pipe");
        Equal(ShellCommandLine.JoinPastedLines("make\n&& run\n"), "make && run", "a line that starts with &&");
        Equal(ShellCommandLine.JoinPastedLines("echo \"a b\nc d\"\nls"), "echo \"a b c d\"; ls", "a quote left open continues on the next line");
        Equal(ShellCommandLine.JoinPastedLines("echo \\\"x\ny"), "echo \\\"x; y", "an escaped quote is not an open quote");
        Equal(ShellCommandLine.JoinPastedLines("echo 'it''s'\nls"), "echo 'it''s'; ls", "closed quotes do not join");
        Equal(ShellCommandLine.JoinWrappedLines("a\nb\n", false), "a\nb\n", "wrapped: separate lines and the final line break are kept");
        Equal(ShellCommandLine.JoinWrappedLines("cat a\n| grep b\nls", false), "cat a | grep b\nls", "wrapped: a pipe at the start joins");
        Equal(ShellCommandLine.JoinWrappedLines("echo \"a\nb\"\nls", false), "echo \"a b\"\nls", "wrapped: an open quote joins");
        Equal(ShellCommandLine.JoinWrappedLines("interface x\n  ip address 1\n", false), "interface x\n  ip address 1\n", "wrapped: an indented block stays when indent is off");
        Equal(ShellCommandLine.JoinWrappedLines("run a\n   b\n", true), "run a b\n", "wrapped: indented rest joins when indent is on");
        Equal(ShellCommandLine.JoinWrappedLines("a\n\n| b", false), "a\n\n| b", "wrapped: a blank line is not joined to");
        Equal(ShellCommandLine.JoinAsOneLine("a\nb\nc", " "), "a b c", "one line: spaces");
        Equal(ShellCommandLine.JoinAsOneLine("a\nb\nc", " && "), "a && b && c", "one line: &&");
        Equal(ShellCommandLine.JoinAsOneLine("make &&\nrun", " && "), "make && run", "one line: after an operator only a space");
        Equal(ShellCommandLine.JoinAsOneLine("cat a\n| grep b", " && "), "cat a | grep b", "one line: before an operator only a space");
        Equal(ShellCommandLine.JoinAsOneLine("a;\nb", " && "), "a; b", "one line: after ; only a space");
        Equal(ShellCommandLine.JoinAsOneLine("# note\n\n  ls  \n\n  pwd\n", " && "), "ls && pwd", "one line: comments and blank lines are dropped, lines trimmed");
        Equal(ShellCommandLine.JoinAsOneLine("tar \\\n  -x", " && "), "tar -x", "one line: backslash continuation");
        Equal(ShellCommandLine.JoinAsOneLine("for f in *; do\necho $f\ndone", " && "), "for f in *; do echo $f; done", "one line: a loop is not broken by &&");
        Equal(ShellCommandLine.JoinAsOneLine("if a; then\nb\nelse\nc\nfi", " && "), "if a; then b; else c; fi", "one line: if/else");
        Equal(ShellCommandLine.JoinAsOneLine("", " "), "", "one line: empty");
        // A comment at the end of a line would swallow the commands joined after it.
        Equal(ShellCommandLine.JoinAsOneLine("make # build\nmake install", " && "), "make && make install", "one line: a trailing comment is cut");
        Equal(ShellCommandLine.JoinAsOneLine("echo a#b\necho '# not a comment'\necho \"x # y\"", " && "), "echo a#b && echo '# not a comment' && echo \"x # y\"", "one line: a # inside a word or a quote stays");
        Equal(ShellCommandLine.JoinAsOneLine("echo $#\necho ${#PATH}", " && "), "echo $# && echo ${#PATH}", "one line: $# and ${#x} stay");
        Equal(ShellCommandLine.JoinAsOneLine("cut -d'\t' -f1 a\nls\t-l", " && "), "cut -d'\t' -f1 a && ls -l", "one line: a tab in quotes stays, other tabs become spaces");
        // What cannot be joined without changing its meaning is left as the lines it is.
        Equal(ShellCommandLine.JoinAsOneLine("cat <<EOF > f\nline1\nEOF", " && "), "cat <<EOF > f\nline1\nEOF", "one line: a here-document is not joined");
        Equal(ShellCommandLine.JoinAsOneLine("echo \"a\nb\"", " && "), "echo \"a\nb\"", "one line: a string over two lines is not joined");
        Equal(ShellCommandLine.JoinAsOneLine("echo 'it'\"'\"'s'\nls", " && "), "echo 'it'\"'\"'s' && ls", "one line: quotes that close on their line are joined");
        Equal(ShellCommandLine.JoinAsOneLine("echo hi # don't\nls", " && "), "echo hi && ls", "one line: an apostrophe in the comment is no open quote");
        Equal(ShellCommandLine.JoinAsOneLine("cat <<< \"x\"\nls", " && "), "cat <<< \"x\" && ls", "one line: a here-string is joined");
        Check(ShellCommandLine.JoinPastedLines("cut -d'\t' -f1\n").Contains("'\t'"), "pasted lines: a tab in quotes stays");
        Equal(string.Join("|", ShellCommandLine.SplitPastedCommands("ls\npwd\n")), "ls|pwd", "commands: one per line");
        Equal(string.Join("|", ShellCommandLine.SplitPastedCommands("for f in *; do\necho $f\ndone\nls")), "for f in *; do echo $f; done|ls", "commands: a loop is one command");
        Equal(string.Join("|", ShellCommandLine.SplitPastedCommands("if true; then\necho 1\nelse\necho 2\nfi")), "if true; then echo 1; else echo 2; fi", "commands: if/else is one command");
        Equal(string.Join("|", ShellCommandLine.SplitPastedCommands("echo 'a\nb'\nls")), "echo 'a b'|ls", "commands: an open quote goes on");
        Equal(string.Join("|", ShellCommandLine.SplitPastedCommands("make \\\n  all\nls")), "make   all|ls", "commands: backslash continuation");
        Equal(string.Join("|", ShellCommandLine.SplitPastedCommands("cat a\n| grep b\nls")), "cat a | grep b|ls", "commands: a line that starts with a pipe goes on");
        Equal(string.Join("|", ShellCommandLine.SplitPastedCommands("a &&\nb\nc")), "a && b|c", "commands: a trailing && goes on");
        Equal(string.Join("|", ShellCommandLine.SplitPastedCommands("echo a;\necho b")), "echo a;|echo b", "commands: a trailing ; ends the command");
        Equal(string.Join("|", ShellCommandLine.SplitPastedCommands("sleep 1 &\necho b")), "sleep 1 &|echo b", "commands: a trailing & ends the command");
        Equal(string.Join("|", ShellCommandLine.SplitPastedCommands("# c\nls\n\n\npwd")), "ls|pwd", "commands: comments and blank lines are dropped");
        Equal(string.Join("|", ShellCommandLine.SplitPastedCommands("foo() {\n echo hi\n}\nfoo")), "foo() { echo hi; }|foo", "commands: a function is one command");
        Equal(string.Join("|", ShellCommandLine.SplitPastedCommands("echo for\nls")), "echo for|ls", "commands: 'for' as an argument opens nothing");
        Equal(string.Join("|", ShellCommandLine.SplitPastedCommands("x=$(\nls\n)\npwd")), "x=$( ls )|pwd", "commands: an open parenthesis goes on");
        Equal(string.Join("|", ShellCommandLine.SplitPastedCommands("  cd /tmp\n  ls\n")), "cd /tmp|ls", "commands: lines with the same indent are separate");
        Equal(string.Join("|", ShellCommandLine.SplitPastedCommands("")), "", "commands: empty");
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

    private static void Context(string text, int caret, CompletionTarget target, int start, int end, string prefix, string name)
    {
        ShellCommandLine.CompletionContext context = ShellCommandLine.Analyze(text, caret);
        bool ok = context.Target == target && context.TokenStart == start && context.TokenEnd == end && (context.InsertPrefix ?? "") == prefix;
        Check(ok, name + (ok ? "" : $" (got {context.Target} {context.TokenStart}..{context.TokenEnd} prefix '{context.InsertPrefix}')"));
    }

    private static void VariableAndHostChecks()
    {
        Context("echo $HO", 8, CompletionTarget.Variable, 5, 8, "$", "a $NAME prefix is a variable");
        Context("echo ${HO", 9, CompletionTarget.Variable, 5, 9, "${", "a ${NAME prefix is a braced variable");
        Context("echo ${HO}x", 9, CompletionTarget.Variable, 5, 10, "${", "the closing brace is part of the replaced text");
        Context("echo $", 6, CompletionTarget.Variable, 5, 6, "$", "a lone $ starts a variable");
        Context("ls $HOME/bi", 11, CompletionTarget.Path, 3, 11, "", "after the variable name comes a path");
        Context("echo \"$HO\"", 9, CompletionTarget.Variable, 6, 9, "$", "a variable inside double quotes");
        Context("echo '$HO'", 9, CompletionTarget.Path, 5, 10, "", "not inside single quotes");
        Context("echo \\$HO", 9, CompletionTarget.Path, 5, 9, "", "not after a backslash");
        Context("echo $1", 7, CompletionTarget.Path, 5, 7, "", "positional parameters are not completed");
        Context("cd $PW", 6, CompletionTarget.Variable, 3, 6, "$", "a variable as a cd argument");
        Context("ssh rou", 7, CompletionTarget.Host, 4, 7, "", "ssh: a host");
        Context("ssh root@rou", 12, CompletionTarget.Host, 4, 12, "root@", "ssh: a host after user@");
        Context("ssh -p 22 rou", 13, CompletionTarget.Host, 10, 13, "", "ssh: a host after options");
        Context("ssh -", 5, CompletionTarget.Path, 4, 5, "", "ssh: an option is not a host");
        Context("ping 8.8", 8, CompletionTarget.Host, 5, 8, "", "ping: a host");
        Context("scp fi", 6, CompletionTarget.HostOrPath, 4, 6, "", "scp: a file or a host");
        Context("scp ./fi", 8, CompletionTarget.Path, 4, 8, "", "scp: ./ is a local path");
        Context("scp file root@rou", 17, CompletionTarget.Host, 9, 17, "root@", "scp: user@ is a host");
        Context("scp file router:/tm", 19, CompletionTarget.Path, 9, 19, "", "scp: host:path is not completed as a host");
        Context("sudo ssh rou", 12, CompletionTarget.Host, 9, 12, "", "sudo ssh: a host");
    }

    private static void HistorySearchChecks()
    {
        List<string> history = new List<string> { "git push", "make test", "git status", "ls -l", "git commit -m x" };
        HistorySearch search = new HistorySearch(history);
        Equal(search.Prompt, "(reverse-i-search)`': ", "the empty search prompt");
        Equal(search.Match, null, "no match before typing");
        search.Append('g');
        search.Append('i');
        search.Append('t');
        Equal(search.Match, "git push", "the newest match first");
        Equal(search.MatchPosition, 0, "the caret goes to the match");
        search.Older();
        Equal(search.Match, "git status", "Ctrl+R: the next older match");
        search.Older();
        Equal(search.Match, "git commit -m x", "Ctrl+R again");
        search.Older();
        Check(search.Failed && search.Match == "git commit -m x", "no older match: failed, the last match stays");
        Equal(search.Prompt, "(failed reverse-i-search)`git': ", "the failed prompt");
        search.Newer();
        Check(!search.Failed && search.Match == "git status", "Ctrl+S: back to a newer match");
        search.Append(" s");
        Equal(search.Match, "git status", "the current match still matches the longer query");
        Equal(search.MatchPosition, 0, "the position of the longer query");
        search.Append("x");
        Check(search.Failed && search.Match == "git status", "a query without a match fails and keeps the match");
        search.RemoveLast();
        Check(!search.Failed && search.Match == "git status", "Backspace searches again from the newest command");
        HistorySearch middle = new HistorySearch(history);
        middle.Append("test");
        Equal(middle.MatchPosition, 5, "a match in the middle of a command");
        HistorySearch none = new HistorySearch(new List<string>());
        none.Append('x');
        Check(none.Failed && none.Match == null, "an empty history");
    }

    private static void HistorySuggestionChecks()
    {
        List<string> history = new List<string> { "git checkout main", "git status", "ls -la /tmp", "Make", "make test", "cat a\tb", "echo 1\necho 2" };
        Equal(HistorySuggestion.Find(history, "git"), " checkout main", "the rest of the newest command that starts with the text");
        Equal(HistorySuggestion.Find(history, "git s"), "tatus", "a longer text picks an older command");
        Equal(HistorySuggestion.Find(history, "git status"), null, "a command typed in full has nothing left to suggest");
        Equal(HistorySuggestion.Find(history, "ma"), "ke test", "case-sensitive, as in the shell");
        Equal(HistorySuggestion.Find(history, "-la"), null, "only the beginning of a command matches");
        Equal(HistorySuggestion.Find(history, ""), null, "nothing is suggested on an empty line");
        Equal(HistorySuggestion.Find(history, "  "), null, "nothing is suggested for spaces only");
        Equal(HistorySuggestion.Find(history, "cat"), null, "a command with a tab is not suggested");
        Equal(HistorySuggestion.Find(history, "echo"), null, "a command of several lines is not suggested");
        Equal(HistorySuggestion.Find(new List<string> { null, "pwd" }, "p"), "wd", "a missing entry is skipped");
        Equal(HistorySuggestion.Find(null, "ls"), null, "no history yet");
        Equal(HistorySuggestion.NextWord(" checkout main"), " checkout", "Ctrl+Right: the spaces and the next word");
        Equal(HistorySuggestion.NextWord("eckout main"), "eckout", "Ctrl+Right: the rest of the word being typed");
        Equal(HistorySuggestion.NextWord("  -la"), "  -la", "Ctrl+Right: the last word");
        Equal(HistorySuggestion.NextWord(""), "", "Ctrl+Right: nothing to take");
        Equal(HistorySuggestion.FitToWidth("abcdef", 76, 80), "abcd", "cut at the right edge of the window");
        Equal(HistorySuggestion.FitToWidth("abc", 10, 80), "abc", "a suggestion that fits is drawn in full");
        Equal(HistorySuggestion.FitToWidth("abc", 80, 80), "", "no room when the text ends at the edge");
        Equal(HistorySuggestion.FitToWidth("abc", -3, 80), "", "nothing when the end of the text is scrolled out of view");
        // Up/Down by the beginning of the line: [0] is the typed line, the rest the history, newest first.
        List<string> entries = new List<string> { "git", "git status", "ls", "git", "git push", "make" };
        Equal(HistorySuggestion.StepByPrefix(entries, 0, true, "git"), 1, "Up: the newest command that starts with the typed text");
        Equal(HistorySuggestion.StepByPrefix(entries, 1, true, "git"), 4, "Up again skips other commands and the bare typed text");
        Equal(HistorySuggestion.StepByPrefix(entries, 4, true, "git"), -1, "Up stops at the oldest match");
        Equal(HistorySuggestion.StepByPrefix(entries, 4, false, "git"), 1, "Down: the next newer match");
        Equal(HistorySuggestion.StepByPrefix(entries, 1, false, "git"), 0, "Down past the newest match brings the typed line back");
        Equal(HistorySuggestion.StepByPrefix(entries, 0, false, "git"), -1, "Down on the typed line goes nowhere");
        Equal(HistorySuggestion.StepByPrefix(entries, 0, true, ""), 1, "an empty line: Up goes through every command");
        Equal(HistorySuggestion.StepByPrefix(entries, 2, true, ""), 3, "an empty line: one command at a time");
        Equal(HistorySuggestion.StepByPrefix(entries, 5, true, ""), -1, "an empty line: Up stops at the oldest command");
        Equal(HistorySuggestion.StepByPrefix(entries, 0, true, "zz"), -1, "Up without a match keeps the line");
        Equal(HistorySuggestion.StepByPrefix(entries, 0, true, "Git"), -1, "the beginning is case-sensitive");
        Equal(HistorySuggestion.StepByPrefix(new List<string>(), 0, true, ""), -1, "no history");
        Check(HistorySuggestion.MatchesPrefix("ls -l", "ls") && !HistorySuggestion.MatchesPrefix("ls", "ls") && HistorySuggestion.MatchesPrefix("", ""), "a match is longer than the typed text; an empty one matches all");
    }

    private static void CommandHistoryChecks()
    {
        Equal(string.Join("|", CommandHistoryEditing.Distinct(new[] { "ls", "make", "", "  ", "ls", null, "git status" })), "ls|make|git status", "the window lists every command once, newest first");
        List<string> commands = new List<string> { "git push origin main", "echo PUSH to git", "make test", "git status" };
        Equal(string.Join("|", CommandHistoryEditing.Filter(commands, "")), string.Join("|", commands), "an empty search shows everything");
        Equal(string.Join("|", CommandHistoryEditing.Filter(commands, "git push")), "git push origin main|echo PUSH to git", "every word, in any order and case");
        Equal(string.Join("|", CommandHistoryEditing.Filter(commands, "  test  ")), "make test", "blanks around the search are ignored");
        Equal(CommandHistoryEditing.Filter(commands, "nothing").Count, 0, "no match");
        Equal(CommandHistoryEditing.QuoteHistoryFile("~/.bash_history"), "\"$HOME\"/'.bash_history'", "the history file under the home directory");
        Equal(CommandHistoryEditing.QuoteHistoryFile("/var/my history"), "'/var/my history'", "a history file with a blank is quoted");
        Equal(CommandHistoryEditing.BuildRemoveCommand("~/.bash_history", new string[0]), null, "nothing to remove: no command");
        Equal(CommandHistoryEditing.BuildRemoveCommand("~/.bash_history", new[] { "a\nb" }), null, "a command of several lines is not a line of the file");
        Equal(CommandHistoryEditing.BuildRemoveCommand(null, new[] { "ls" }), null, "no history file");

        // The removal in real shells, on a file with bash time stamps, zsh extended lines and quotes.
        string original = string.Join("\n", new[]
        {
            "ls -la",
            "#1699999999",
            "mysql -pSecret",
            "#1700000000",
            "make test",
            ": 1700000001:0;mysql -pSecret",
            "echo 'it''s' \"$HOME\" \\n",
            "mysql -pSecret",
            "",
            "#1700000002",
        }) + "\n";
        string expected = string.Join("\n", new[]
        {
            "ls -la",
            "#1700000000",
            "make test",
            "",
            "#1700000002",
        }) + "\n";
        string[] remove = { "mysql -pSecret", "echo 'it''s' \"$HOME\" \\n", "not in the file" };
        string work = Path.Combine(Path.GetTempPath(), "smartty-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            List<KeyValuePair<string, string>> shells = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("sh", "sh") };
            if (FindProgram("bash") != null)
            {
                shells.Add(new KeyValuePair<string, string>("bash", "bash"));
            }
            string busybox = FindProgram("busybox");
            if (busybox != null)
            {
                // An OpenWrt-like system: only BusyBox applets.
                string bin = Path.Combine(work, "owrt-bin");
                Directory.CreateDirectory(bin);
                foreach (string applet in new[] { "ash", "sh", "awk", "cat", "rm", "mktemp", "printf", "[" })
                {
                    File.CreateSymbolicLink(Path.Combine(bin, applet), busybox);
                }
                shells.Add(new KeyValuePair<string, string>("BusyBox ash", bin));
            }
            else
            {
                Console.WriteLine("SKIP: removing history lines under BusyBox (busybox is not installed)");
            }
            string script = CommandHistoryEditing.BuildRemoveCommand("~/.bash_history", remove);
            foreach (KeyValuePair<string, string> shell in shells)
            {
                string home = Path.Combine(work, "home-" + shell.Key.Replace(' ', '-'));
                Directory.CreateDirectory(home);
                string file = Path.Combine(home, ".bash_history");
                File.WriteAllText(file, original);
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                Dictionary<string, string> environment = new Dictionary<string, string> { ["HOME"] = home };
                string command = script;
                if (shell.Value.Contains("/"))
                {
                    environment["PATH"] = shell.Value;
                    command = Path.Combine(shell.Value, "ash") + " -c " + Quote(script);
                }
                else if (shell.Value != "sh")
                {
                    command = shell.Value + " -c " + Quote(script);
                }
                RunScript(command, environment, out string error, out int exit);
                string result = File.ReadAllText(file);
                Check(exit == 0 && result == expected, shell.Key + ": the commands, their time stamps and their zsh lines are removed" + (exit == 0 && result == expected ? "" : " (exit " + exit + ", " + error + "): " + result.Replace("\n", "\\n")));
                Equal(File.GetUnixFileMode(file), UnixFileMode.UserRead | UnixFileMode.UserWrite, shell.Key + ": the file keeps its permissions");
                Equal(Directory.GetFiles(home).Length, 1, shell.Key + ": no temporary file is left next to it");
                File.Delete(file);
                RunScript(command, environment, out _, out exit);
                Check(exit == 0 && !File.Exists(file), shell.Key + ": a missing history file is left alone");
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private static void CompletionSourceChecks()
    {
        CompletionSources.ShellDefinitions definitions = CompletionSources.ParseShellDefinitions(new[]
        {
            "bash: cannot set terminal process group (-1): Inappropriate ioctl for device",
            "bash: no job control in this shell",
            "alias ll='ls -alF'",
            "alias gs='git status'\r",
            "l='ls -CF'",
            "alias q=\"it's\"",
            CompletionSources.FunctionsMarker,
            "mkcd",
            "declare -f __git_ps1",
            "not a name (x)",
            CompletionSources.VariablesMarker,
            "HOME",
            "PATH=/usr/bin:/bin",
            "MULTI=line one",
            "line two without equals sign",
            "1BAD",
            "EDITOR"
        });
        Equal(string.Join(",", definitions.Aliases.ConvertAll((KeyValuePair<string, string> alias) => alias.Key + "=" + alias.Value)), "ll=ls -alF,gs=git status,l=ls -CF,q=it's", "aliases of bash and dash, shell noise skipped");
        Equal(string.Join(",", definitions.Functions), "mkcd,__git_ps1", "functions");
        Equal(string.Join(",", definitions.Variables), "EDITOR,HOME,MULTI,PATH", "variables (names only, sorted)");

        List<CompletionSources.HostEntry> hosts = CompletionSources.ParseHostFiles(new[]
        {
            CompletionSources.FileMarker + "/home/u/.ssh/config",
            "Host router nas",
            "  HostName 192.168.1.1",
            "Host *.example.com !bad",
            "host=build",
            "Match host x",
            CompletionSources.FileMarker + "/home/u/.ssh/known_hosts",
            "github.com,140.82.121.4 ssh-ed25519 AAAA",
            "[gitlab.local]:2222 ssh-rsa AAAA",
            "|1|hash=|salt= ssh-rsa AAAA",
            "@cert-authority *.corp ssh-rsa AAAA",
            "# comment",
            "Router ssh-rsa AAAA",
            CompletionSources.FileMarker + "/etc/hosts",
            "127.0.0.1 localhost",
            "192.168.1.10   printer printer.lan  # office",
            "# 10.0.0.1 commented"
        });
        Equal(string.Join(",", hosts.ConvertAll((CompletionSources.HostEntry host) => host.Name)), "router,nas,build,github.com,140.82.121.4,gitlab.local,localhost,printer,printer.lan", "hosts of ssh config, known_hosts and /etc/hosts without patterns, hashes and duplicates");
        Equal(hosts[0].Source, "ssh config", "the source of a host");
    }

    /// <summary>Runs a script with "sh -c" in the given environment; returns stdout (stderr goes to <paramref name="error"/>).</summary>
    private static string RunScript(string script, Dictionary<string, string> environment, out string error, out int exitCode)
    {
        ProcessStartInfo info = new ProcessStartInfo("sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false
        };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(script);
        foreach (KeyValuePair<string, string> item in environment)
        {
            if (item.Value == null)
            {
                info.Environment.Remove(item.Key);
            }
            else
            {
                info.Environment[item.Key] = item.Value;
            }
        }
        using Process process = Process.Start(info);
        process.StandardInput.Close();
        System.Threading.Tasks.Task<string> errorTask = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        error = errorTask.Result;
        exitCode = process.ExitCode;
        return output.Replace("\r", "");
    }

    private static string FindProgram(string name)
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'))
        {
            string path = Path.Combine(dir, name);
            if (dir.Length > 0 && File.Exists(path))
            {
                return path;
            }
        }
        return null;
    }

    private static void ShellSelectionChecks()
    {
        // The answer of the probe.
        ShellSelection.ProbeResult openWrt = ShellSelection.ParseProbe(new[] { "SMARTTY_LOGIN_SHELL=/bin/ash", "SMARTTY_BUSYBOX=/bin/ash", "SMARTTY_SHELL=ash", "SMARTTY_BUSYBOX=ash", "SMARTTY_SHELL=sh", "SMARTTY_BUSYBOX=sh" });
        ShellSelection.Choice choice = ShellSelection.ChooseAutomatic(openWrt);
        Check(choice.Command == "/bin/ash" && choice.IsAsh && choice.Name == "ash", "OpenWrt: the login shell /bin/ash, BusyBox");
        ShellSelection.ProbeResult openWrtWithBash = ShellSelection.ParseProbe(new[] { "SMARTTY_LOGIN_SHELL=/bin/ash", "SMARTTY_SHELL=bash", "SMARTTY_SHELL=ash", "SMARTTY_SHELL=sh" });
        Equal(ShellSelection.ChooseAutomatic(openWrtWithBash).Command, "/bin/ash", "OpenWrt with bash installed: still the ash the user logs in to");
        ShellSelection.ProbeResult debian = ShellSelection.ParseProbe(new[] { "SMARTTY_LOGIN_SHELL=/bin/bash", "SMARTTY_SHELL=bash", "SMARTTY_SHELL=sh" });
        choice = ShellSelection.ChooseAutomatic(debian);
        Check(choice.Command == "/bin/bash" && !choice.IsAsh, "a bash login shell is used as is");
        ShellSelection.ProbeResult zshUser = ShellSelection.ParseProbe(new[] { "SMARTTY_LOGIN_SHELL=/usr/bin/zsh", "SMARTTY_SHELL=bash", "SMARTTY_SHELL=sh" });
        Equal(ShellSelection.ChooseAutomatic(zshUser).Command, "bash", "another login shell (zsh): bash, as before");
        ShellSelection.ProbeResult alpine = ShellSelection.ParseProbe(new[] { "SMARTTY_LOGIN_SHELL=/bin/sh", "SMARTTY_BUSYBOX=/bin/sh", "SMARTTY_SHELL=ash", "SMARTTY_BUSYBOX=ash", "SMARTTY_SHELL=sh", "SMARTTY_BUSYBOX=sh" });
        choice = ShellSelection.ChooseAutomatic(alpine);
        Check(choice.Command == "ash" && choice.IsAsh, "no bash, login shell sh: ash");
        ShellSelection.ProbeResult onlySh = ShellSelection.ParseProbe(new[] { "SMARTTY_LOGIN_SHELL=", "SMARTTY_SHELL=sh", "SMARTTY_BUSYBOX=sh" });
        choice = ShellSelection.ChooseAutomatic(onlySh);
        Check(choice.Command == "sh" && choice.IsAsh, "only a BusyBox sh: sh, started like ash");
        Check(ShellSelection.ChooseAutomatic(ShellSelection.ParseProbe(new[] { "SMARTTY_LOGIN_SHELL=/bin/bash" })).Command == "/bin/bash", "nothing in PATH, but the login shell is bash");
        Check(ShellSelection.ChooseAutomatic(ShellSelection.ParseProbe(new[] { "SMARTTY_LOGIN_SHELL=" })) == null, "nothing known: no shell");
        Check(ShellSelection.ParseProbe(null) == null && ShellSelection.ParseProbe(new[] { "sh: syntax error" }) == null, "no answer or an error line is no probe result");
        ShellSelection.ProbeResult hostile = ShellSelection.ParseProbe(new[] { "SMARTTY_LOGIN_SHELL=/bin/bash;reboot", "SMARTTY_SHELL=rm -rf /", "SMARTTY_SHELL=sh" });
        Check(hostile.LoginShell == null && hostile.Installed.SequenceEqual(new[] { "sh" }), "a login shell path with odd characters and unknown shells are ignored");
        Check(ShellSelection.ParseProbe(new[] { "  SMARTTY_LOGIN_SHELL=/bin/ash\r", "SMARTTY_SHELL=ash\r" }).LoginShell == "/bin/ash", "CR and blanks around the lines are ignored");

        // The manual mode.
        Check(ShellSelection.ChooseManual("  ", openWrt) == null && ShellSelection.ChooseManual(null, null) == null, "manual: an empty shell means the automatic choice");
        choice = ShellSelection.ChooseManual("bash --norc", debian);
        Check(choice.Command == "bash --norc" && !choice.IsAsh && choice.Name == "bash", "manual: the shell is used as typed, with arguments");
        Check(ShellSelection.ChooseManual("/bin/ash", null).IsAsh, "manual: ash is recognized by its name");
        Check(ShellSelection.ChooseManual("sh", openWrt).IsAsh && !ShellSelection.ChooseManual("sh", debian).IsAsh, "manual: sh is started like ash where it is BusyBox (OpenWrt), not on Debian");

        // The wrapper.
        Equal(ShellSelection.WrapInteractive(new ShellSelection.Choice { Command = "bash" }, "echo \"$HOME\""), "bash -i -c \"echo \\\"\\$HOME\\\"\"", "bash: -i -c with the command escaped");
        string ashWrapper = ShellSelection.WrapInteractive(new ShellSelection.Choice { Command = "/bin/ash", IsAsh = true }, "ll");
        Check(ashWrapper.StartsWith("/bin/ash -c \"") && !ashWrapper.Contains(" -i ") && ashWrapper.Contains("\\${ENV:-/etc/shinit}") && ashWrapper.EndsWith("\nll\""), "ash: no -i, $ENV (OpenWrt: /etc/shinit) read first, the command on its own line");

        // The history file.
        Equal(ShellSelection.GetHistoryFile("~/.bash_history", ShellSelection.ChooseAutomatic(openWrt)), "~/.ash_history", "history: BusyBox ash keeps ~/.ash_history");
        Equal(ShellSelection.GetHistoryFile("~/.bash_history", ShellSelection.ChooseAutomatic(debian)), "~/.bash_history", "history: bash keeps ~/.bash_history");
        Equal(ShellSelection.GetHistoryFile(null, new ShellSelection.Choice { Command = "zsh" }), "~/.zsh_history", "history: zsh (manual) keeps ~/.zsh_history");
        Equal(ShellSelection.GetHistoryFile("", null), "~/.bash_history", "history: nothing known, the old default");
        Equal(ShellSelection.GetHistoryFile("~/.my_history", ShellSelection.ChooseAutomatic(openWrt)), "~/.my_history", "history: a file set by the user is kept");

        // The probe and the wrapper in real shells.
        string work = Path.Combine(Path.GetTempPath(), "smartty-shells-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string busybox = FindProgram("busybox");
            if (busybox == null)
            {
                Console.WriteLine("SKIP: the probe and the wrapper under BusyBox ash (busybox is not installed)");
            }
            else
            {
                // An OpenWrt-like system: /bin/ash and /bin/sh are BusyBox, there is no bash.
                string bin = Path.Combine(work, "owrt-bin");
                Directory.CreateDirectory(bin);
                foreach (string applet in new[] { "ash", "sh", "readlink", "cat" })
                {
                    File.CreateSymbolicLink(Path.Combine(bin, applet), busybox);
                }
                string ash = Path.Combine(bin, "ash");
                Dictionary<string, string> environment = new Dictionary<string, string> { ["PATH"] = bin, ["SHELL"] = ash, ["ENV"] = null, ["HOME"] = work };
                string probeOutput = RunScript(Path.Combine(bin, "ash") + " -c " + Quote(ShellSelection.ProbeCommand), environment, out string probeError, out _);
                ShellSelection.Choice detected = ShellSelection.ChooseAutomatic(ShellSelection.ParseProbe(probeOutput.Split('\n')));
                bool found = detected != null && detected.Command == ash && detected.IsAsh;
                Check(found, "BusyBox: the probe finds the ash login shell and that it is BusyBox" + (found ? "" : ": " + probeOutput + probeError));
                Dictionary<string, string> noShell = new Dictionary<string, string>(environment) { ["SHELL"] = null };
                detected = ShellSelection.ChooseAutomatic(ShellSelection.ParseProbe(RunScript(ash + " -c " + Quote(ShellSelection.ProbeCommand), noShell, out _, out _).Split('\n')));
                Check(detected != null && detected.Command == "ash" && detected.IsAsh, "BusyBox without $SHELL: ash from PATH");

                // The wrapper prints nothing but the output of the command (BusyBox ash -i would print its banner) and has the aliases of $ENV.
                string shinit = Path.Combine(work, "shinit");
                File.WriteAllText(shinit, "alias ll='echo from-alias'\nhello() { echo from-function; }\n");
                Dictionary<string, string> withEnv = new Dictionary<string, string>(environment) { ["ENV"] = shinit };
                string output = RunScript(ShellSelection.WrapInteractive(new ShellSelection.Choice { Command = ash, IsAsh = true }, "export A=1 && ll && hello && echo \"$A\""), withEnv, out string error, out int exit);
                Equal(output, "from-alias\nfrom-function\n1\n", "BusyBox ash wrapper: aliases and functions of $ENV, no banner" + (output == "from-alias\nfrom-function\n1\n" ? "" : ": " + error));
                output = RunScript(ShellSelection.WrapInteractive(new ShellSelection.Choice { Command = ash, IsAsh = true }, "echo plain; exit 3"), environment, out error, out exit);
                Check(output == "plain\n" && exit == 3, "BusyBox ash wrapper without $ENV and /etc/shinit: the command runs, its exit code is kept" + (exit == 3 ? "" : ": " + error));
                output = RunScript(ash + " -i -c 'echo x' </dev/null", environment, out _, out _);
                Check(output.Contains("BusyBox"), "(why ash gets no -i: BusyBox ash -i -c prints its banner)");
            }

            string bashPath = FindProgram("bash");
            if (bashPath == null)
            {
                Console.WriteLine("SKIP: the bash wrapper (bash is not installed)");
            }
            else
            {
                string home = Path.Combine(work, "home");
                Directory.CreateDirectory(home);
                File.WriteAllText(Path.Combine(home, ".bashrc"), "alias ll='echo from-bashrc'\n");
                Dictionary<string, string> environment = new Dictionary<string, string> { ["HOME"] = home, ["SHELL"] = bashPath };
                string probeOutput = RunScript(ShellSelection.ProbeCommand, environment, out _, out _);
                ShellSelection.Choice detected = ShellSelection.ChooseAutomatic(ShellSelection.ParseProbe(probeOutput.Split('\n')));
                bool found = detected != null && detected.Command == bashPath && !detected.IsAsh;
                Check(found, "bash: the probe finds the bash login shell" + (found ? "" : ": " + probeOutput));
                string output = RunScript(ShellSelection.WrapInteractive(detected, "ll"), environment, out string error, out _);
                Equal(output, "from-bashrc\n", "bash wrapper: the alias of .bashrc works" + (output == "from-bashrc\n" ? "" : ": " + error));
            }
        }
        finally
        {
            try
            {
                Directory.Delete(work, true);
            }
            catch
            {
            }
        }
    }

    private static string Quote(string text)
    {
        return "'" + text.Replace("'", "'\\''") + "'";
    }

    private static int Main()
    {
        try
        {
            ShellSelectionChecks();
            VariableAndHostChecks();
            HistorySearchChecks();
            HistorySuggestionChecks();
            CommandHistoryChecks();
            CompletionSourceChecks();
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
