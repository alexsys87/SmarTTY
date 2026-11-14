using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SmarTTY.CodeEditing;
using L = SmarTTY.CodeEditing.SyntaxHighlighting.Language;

// Regression harness for the checks the editor runs on a file before it replaces it on the server:
//  * which check a file gets (by name, directory, language and "#!" line),
//  * how the output of each check is read (only a named line blocks a save),
//  * the generated shell command, run here with /bin/sh against real files where the tools exist.
// Run with: dotnet run --project tests/ConfigValidationTests
internal static class Program
{
    private static int _passed;

    private const string Temp = "/tmp/0a1b2c.txt";

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

    private static string NameOf(string path, L language, string firstLine = null)
    {
        return ConfigValidators.Find(path, language, firstLine)?.Name;
    }

    private static void SelectionChecks()
    {
        Equal(NameOf("/etc/ssh/sshd_config", L.Config), "sshd -t", "sshd_config gets sshd -t");
        Equal(NameOf("/ETC/SSH/SSHD_CONFIG", L.Plain), "sshd -t", "the file name is matched without regard to case");
        Equal(NameOf("/etc/nginx/nginx.conf", L.Config), "nginx -t", "nginx.conf gets nginx -t");
        Equal(NameOf("/etc/nginx/conf.d/site.conf", L.Config), null, "an included nginx file has no check of its own");
        Equal(NameOf("/etc/sudoers", L.Plain), "visudo -c", "sudoers gets visudo");
        Equal(NameOf("/etc/sudoers.d/90-admins", L.Plain), "visudo -c", "a file in sudoers.d gets visudo");
        Equal(NameOf("/home/u/app.py", L.Python), "python3 syntax check", "Python gets the syntax check");
        Equal(NameOf("/home/u/run.sh", L.Bash), "shell -n", "a shell script gets sh -n / bash -n");
        Equal(NameOf("/home/u/.zshrc", L.Bash), null, "zsh start-up files are not checked with bash");
        Equal(NameOf("/srv/data.json", L.Json), "JSON check", "a .json file gets the JSON check");
        Equal(NameOf("/srv/tsconfig.jsonc", L.Json), null, "a JSON file with comments is not checked");
        Equal(NameOf("/srv/compose.yml", L.Yaml), "YAML check", "YAML gets the YAML check");
        Equal(NameOf("/srv/pom.xml", L.Markup), "xmllint --noout", "an .xml file gets xmllint");
        Equal(NameOf("/srv/index.html", L.Markup), null, "HTML is not XML: no check");
        Equal(NameOf("/etc/hosts", L.Config), null, "a plain config file has no check");
        Equal(NameOf("/tmp/notes.txt", L.Plain), null, "plain text has no check");
        Equal(NameOf("", L.Python), null, "no path, no check");
        Equal(NameOf(null, L.Python), null, "a null path, no check");

        Equal(string.Join(",", ConfigValidators.ShellsFor(null)), "bash,sh", "no #! line: bash, then sh");
        Equal(string.Join(",", ConfigValidators.ShellsFor("#!/bin/bash")), "bash,sh", "#!/bin/bash: bash, then sh");
        Equal(string.Join(",", ConfigValidators.ShellsFor("#!/bin/sh")), "sh", "#!/bin/sh: only sh (bash would accept too much)");
        Equal(string.Join(",", ConfigValidators.ShellsFor("#!/bin/sh /etc/rc.common")), "sh", "an OpenWrt init script: sh");
        Equal(string.Join(",", ConfigValidators.ShellsFor("#!/usr/bin/env bash")), "bash,sh", "env bash");
        Equal(string.Join(",", ConfigValidators.ShellsFor("#!/usr/bin/env -S dash -e")), "sh", "env -S dash");
        Equal(string.Join(",", ConfigValidators.ShellsFor("#!/bin/busybox ash")), "sh", "busybox ash");
        Equal(string.Join(",", ConfigValidators.ShellsFor("#!/bin/ksh")), "ksh,sh", "ksh, then sh");
        Equal(string.Join(",", ConfigValidators.ShellsFor("# no shebang here")), "bash,sh", "a comment is not a #! line");
        Equal(string.Join(",", ConfigValidators.ShellsFor("#!/bin/bash\r")), "bash,sh", "a #! line with a CR from CRLF line ends");

        // B-105: a script of another shell is not checked with bash or sh, by its "#!" line or by its name.
        Equal(ConfigValidators.ShellsFor("#!/bin/zsh").Count, 0, "#!/bin/zsh: no shell can check it");
        Equal(ConfigValidators.ShellsFor("#!/usr/bin/env zsh").Count, 0, "env zsh: no shell can check it");
        Equal(ConfigValidators.ShellsFor("#!/usr/bin/env -S zsh -f").Count, 0, "env -S zsh: no shell can check it");
        Equal(ConfigValidators.ShellsFor("#!/usr/bin/fish").Count, 0, "fish: no shell can check it");
        Equal(ConfigValidators.ShellsFor("#!/bin/csh -f").Count, 0, "csh: no shell can check it");
        Equal(ConfigValidators.ShellsFor("#!").Count, 0, "a #! line that names nothing: no check");
        Equal(string.Join(",", ConfigValidators.ShellsFor("#!/bin/mksh")), "mksh,sh", "mksh, then sh");
        Equal(NameOf("/home/u/.zsh/plugin.zsh", L.Bash), null, "plugin.zsh is not checked with bash");
        Equal(NameOf("/home/u/prompt_foo_setup.zsh", L.Bash), null, "prompt_foo_setup.zsh is not checked with bash");
        Equal(NameOf("/home/u/PLUGIN.ZSH", L.Bash), null, "the extension is matched without regard to case");
        Equal(NameOf("/home/u/.oh-my-zsh/themes/agnoster.zsh-theme", L.Bash), null, "a .zsh-theme file is not checked with bash");
        Equal(NameOf("/etc/zsh/zshrc", L.Bash), null, "/etc/zsh/zshrc is not checked with bash");
        Equal(NameOf("/home/u/.zprofile", L.Bash), null, ".zprofile is not checked with bash");
        Equal(NameOf("/home/u/.zlogin", L.Bash), null, ".zlogin is not checked with bash");
        Equal(NameOf("/home/u/tool", L.Bash, "#!/bin/zsh"), null, "a zsh script without an extension (by its #! line)");
        Equal(NameOf("/home/u/tool.sh", L.Bash, "#!/usr/bin/env zsh"), null, "a .sh file that says zsh is not checked either");
        Equal(NameOf("/home/u/tool", L.Bash, "#!/bin/bash"), "shell -n", "a bash script is checked");
        Equal(NameOf("/home/u/tool", L.Bash, "#!/bin/sh"), "shell -n", "an sh script is checked");
        Equal(NameOf("/home/u/tool", L.Bash, "#!/bin/ksh"), "shell -n", "a ksh script is checked");
        Equal(NameOf("/home/u/tool", L.Bash, null), "shell -n", "a script without a #! line is still checked (bash, then sh)");
        Equal(NameOf("/home/u/tool", L.Bash, "# just a comment"), "shell -n", "a first line that is no #! line: still checked");

        // Python 2 is no python3 syntax.
        Equal(NameOf("/home/u/old.py", L.Python, "#!/usr/bin/python2"), null, "python2 is not checked with python3");
        Equal(NameOf("/home/u/old.py", L.Python, "#!/usr/bin/env python2.7"), null, "env python2.7 is not checked with python3");
        Equal(NameOf("/home/u/new.py", L.Python, "#!/usr/bin/python3"), "python3 syntax check", "python3 is checked");
        Equal(NameOf("/home/u/new.py", L.Python, "#!/usr/bin/env python"), "python3 syntax check", "plain python is checked");
        Equal(NameOf("/home/u/new.py", L.Python, null), "python3 syntax check", "no #! line: checked");

        Equal(ConfigValidators.FindReload("/etc/ssh/sshd_config")?.Name, "sshd", "sshd_config can be reloaded");
        Equal(ConfigValidators.FindReload("/etc/nginx/conf.d/site.conf")?.Name, "nginx", "a file in /etc/nginx can be reloaded");
        Equal(ConfigValidators.FindReload("/etc/nginx/nginx.conf")?.Name, "nginx", "nginx.conf can be reloaded");
        Equal(ConfigValidators.FindReload("/usr/local/etc/ssh/sshd_config")?.Name, "sshd", "sshd_config of a /usr/local prefix can be reloaded");
        Equal(ConfigValidators.FindReload("/home/u/sshd_config")?.Name, null, "a sshd_config in a home folder has no reload");
        Equal(ConfigValidators.FindReload("/tmp/sshd_config")?.Name, null, "a sshd_config in /tmp has no reload");
        Equal(ConfigValidators.FindReload("/etc/hosts")?.Name, null, "other files have no reload");
        Equal(ConfigValidators.FindReload("/home/u/nginx/site.conf")?.Name, null, "a .conf outside /etc/nginx has no reload");
    }

    private static List<ValidationIssue> Parse(string output)
    {
        return ConfigValidators.ParseIssues(output.Split('\n'), Temp);
    }

    private static string Show(List<ValidationIssue> issues)
    {
        return string.Join(" | ", issues.Select(i => i.Line + ":" + i.Column + " " + i.Message));
    }

    private static void ParseChecks()
    {
        // bash -n
        List<ValidationIssue> bash = Parse(
            Temp + ": line 5: syntax error near unexpected token `fi'\n" +
            Temp + ": line 5: `fi'\n" +
            Temp + ": line 9: syntax error: unexpected end of file");
        Equal(bash.Count, 2, "bash: one problem per line");
        Equal(bash[0].Line, 5, "bash: first line");
        Equal(bash[0].Message, "syntax error near unexpected token `fi'", "bash: the message, without the file name");
        Equal(bash[1].Line, 9, "bash: second line");

        // dash
        List<ValidationIssue> dash = Parse(Temp + ": 7: Syntax error: \"fi\" unexpected");
        Equal(dash.Count, 1, "dash: a problem");
        Equal(dash[0].Line, 7, "dash: the line");
        Equal(dash[0].Message, "Syntax error: \"fi\" unexpected", "dash: the message");

        // sshd -t
        List<ValidationIssue> sshd = Parse(Temp + " line 12: Bad configuration option: PermitRootLogn\n" + Temp + ": terminating, 1 bad configuration options");
        Equal(sshd.Count, 1, "sshd: only the line that names a line");
        Equal(sshd[0].Line, 12, "sshd: the line");
        Equal(sshd[0].Message, "Bad configuration option: PermitRootLogn", "sshd: the message");
        Equal(Parse("sshd: no hostkeys available -- exiting.").Count, 0, "sshd: a message about the host is not a problem of the file");

        // nginx -t
        List<ValidationIssue> nginx = Parse("nginx: [emerg] unknown directive \"foo\" in " + Temp + ":14\nnginx: configuration file " + Temp + " test failed");
        Equal(nginx.Count, 1, "nginx: a problem");
        Equal(nginx[0].Line, 14, "nginx: the line");
        Equal(nginx[0].Message, "nginx: [emerg] unknown directive \"foo\"", "nginx: the message without the location");
        Equal(Parse("nginx: [alert] could not open error log file: open() \"/var/log/nginx/error.log\" failed (13: Permission denied)").Count, 0, "nginx: a permission error is not a problem of the file");
        Equal(Parse("nginx: [emerg] unknown directive \"x\" in /etc/nginx/conf.d/other.conf:3").Count, 0, "nginx: a problem in another file is not this file's");

        // B-81: warnings and environment failures are not syntax errors
        Equal(Parse("nginx: [warn] the \"user\" directive makes sense only if the master process runs with super-user privileges, ignored in " + Temp + ":3").Count, 0, "nginx: a [warn] line with a location is not an error");
        Equal(Parse("nginx: [alert] could not open error log file: open() \"/var/log/nginx/error.log\" failed (13: Permission denied) in " + Temp + ":2").Count, 0, "nginx: an [alert] line is not an error");
        Equal(Parse("nginx: [emerg] open() \"/etc/nginx/secret.conf\" failed (13: Permission denied) in " + Temp + ":9").Count, 0, "nginx: a permission error of an include is not a problem of the syntax");
        Equal(Parse("nginx: [emerg] mkdir() \"/var/lib/nginx/tmp\" failed (13: Permission denied)").Count, 0, "nginx: a mkdir failure is not a problem of the syntax");
        Equal(Parse("nginx: [emerg] open() \"/tmp/mime.types\" failed (2: No such file or directory) in " + Temp + ":12").Count, 0, "nginx: a relative include looked for next to the temporary file is not an error");
        List<ValidationIssue> mixed = Parse("nginx: [warn] something in " + Temp + ":1\nnginx: [emerg] open() \"/tmp/mime.types\" failed (2: No such file or directory) in " + Temp + ":2\nnginx: [emerg] unknown directive \"foo\" in " + Temp + ":14");
        Equal(mixed.Count, 1, "nginx: only the real error is kept next to the noise");
        Equal(mixed[0].Line, 14, "nginx: the real error line");

        // visudo
        List<ValidationIssue> visudo = Parse(Temp + ":3:12: syntax error\nRoot ALL=(ALL) ALL\n           ^");
        Equal(visudo.Count, 1, "visudo: a problem");
        Equal(visudo[0].Line, 3, "visudo: the line");
        Equal(visudo[0].Column, 12, "visudo: the column");
        Equal(visudo[0].Message, "syntax error", "visudo: the message");
        Equal(Parse(">>> " + Temp + ": syntax error near line 8 <<<")[0].Line, 8, "old visudo: near line N");

        // B-105: a problem in a file that the edited one includes is not a problem of the edited file.
        Equal(Parse(">>> /etc/sudoers.d/extra: syntax error near line 2 <<<").Count, 0, "visudo: an error of an #includedir file names no line of this file");
        Equal(Parse("visudo: /etc/sudoers.d/extra:2:5: syntax error").Count, 0, "visudo: gcc style error of another file");
        Equal(Parse("/etc/ssh/sshd_config.d/10-x.conf line 3: Bad configuration option: Foo").Count, 0, "sshd: an error of an Include file names no line of this file");
        Equal(Parse("/etc/ssh/sshd_config.d/10-x.conf: line 3: Bad configuration option: Foo").Count, 0, "sshd: the colon form of another file");
        Equal(Parse("keys/x.conf line 3: bad").Count, 1, "a relative name without a slash prefix is still read as before (nothing tells it is a path)");
        List<ValidationIssue> own = Parse(Temp + " line 4: Bad configuration option: Foo\n/etc/ssh/sshd_config.d/x.conf line 9: Bad option");
        Equal(own.Count, 1, "sshd: own and foreign problems together: only the own one");
        Equal(own[0].Line, 4, "sshd: the own problem's line");
        Equal(Parse(">>> " + Temp + ": syntax error near line 8 <<<").Count, 1, "old visudo: the temporary file is the edited one");
        Equal(Parse("syntax error near line 5").Count, 1, "no file named at all: the line is the edited file's");
        Equal(Parse("error at line 5, column 2 (see /usr/share/doc/x)").Count, 0, "a message that points to another path in it is not taken");

        // xmllint
        List<ValidationIssue> xml = Parse(Temp + ":4: parser error : Opening and ending tag mismatch: a line 2 and b\n</b>\n    ^");
        Equal(xml.Count, 1, "xmllint: a problem");
        Equal(xml[0].Line, 4, "xmllint: the line is the one before 'line 2'");
        Equal(xml[0].Message, "parser error : Opening and ending tag mismatch: a line 2 and b", "xmllint: the message");

        // jq
        List<ValidationIssue> jq = Parse("jq: error (at " + Temp + ":3): Cannot index number\n");
        Equal(jq.Count, 1, "jq: a problem");
        Equal(jq[0].Line, 3, "jq: the line");
        List<ValidationIssue> jq5 = Parse("parse error: Invalid numeric literal at line 6, column 4");
        Equal(jq5.Count, 1, "jq 1.5: a problem");
        Equal(jq5[0].Line, 6, "jq 1.5: the line");
        Equal(jq5[0].Column, 4, "jq 1.5: the column");

        // The checks written in Python.
        List<ValidationIssue> py = Parse("3:7: invalid syntax");
        Equal(py.Count, 1, "python: a problem");
        Equal(py[0].Line, 3, "python: the line");
        Equal(py[0].Column, 7, "python: the column");
        Equal(py[0].Message, "invalid syntax", "python: the message");

        // Order, limit, empty input.
        List<ValidationIssue> ordered = Parse(Temp + ": line 9: b\n" + Temp + ": line 2: a");
        Equal(ordered[0].Line, 2, "problems are in line order");
        Equal(Parse("").Count, 0, "empty output: no problems");
        Equal(ConfigValidators.ParseIssues(null, Temp).Count, 0, "null output: no problems");
        List<string> many = new List<string>();
        for (int i = 1; i <= 80; i++)
        {
            many.Add(Temp + ": line " + i + ": bad");
        }
        Equal(ConfigValidators.ParseIssues(many, Temp).Count, 50, "at most 50 problems are kept");
        Equal(ConfigValidators.ParseIssues(new[] { Temp + ": line 3: " + new string('x', 500) }, Temp)[0].Message.Length, 303, "a long message is cut");
    }

    private static void InterpretChecks()
    {
        ConfigValidator bash = ConfigValidators.Find("/a.sh", L.Bash, null);
        string[] bad = { Temp + ": line 2: syntax error" };
        Equal(bash.Interpret(bad, 2, Temp).Outcome, ValidationOutcome.Failed, "exit 2 with a line: Failed");
        Equal(bash.Interpret(bad, 0, Temp).Outcome, ValidationOutcome.Passed, "exit 0: Passed, whatever was printed");
        Equal(bash.Interpret(bad, 127, Temp).Outcome, ValidationOutcome.Unavailable, "exit 127: the program is missing");
        Equal(bash.Interpret(bad, null, Temp).Outcome, ValidationOutcome.Unavailable, "no exit status (timeout, lost connection): Unavailable");
        Equal(bash.Interpret(null, 1, Temp).Outcome, ValidationOutcome.Unavailable, "no output (not run): Unavailable");
        Equal(bash.Interpret(new[] { "permission denied" }, 1, Temp).Outcome, ValidationOutcome.Inconclusive, "exit 1 without a line: Inconclusive, does not block");
        Equal(bash.Interpret(new string[0], 1, Temp).Outcome, ValidationOutcome.Inconclusive, "exit 1 without output: Inconclusive");
        Equal(bash.Interpret(new[] { "nginx: [warn] x in " + Temp + ":3", "nginx: [emerg] open() \"/tmp/mime.types\" failed (2: No such file or directory) in " + Temp + ":4" }, 1, Temp).Outcome, ValidationOutcome.Inconclusive, "only warnings and environment failures: Inconclusive, not Failed");
        Equal(bash.Interpret(bad, 2, Temp).Issues.Count, 1, "the issues are returned");
        Equal(bash.Interpret(new[] { "/etc/ssh/sshd_config.d/x.conf line 3: Bad configuration option: Foo" }, 255, Temp).Outcome, ValidationOutcome.Inconclusive, "an error only in an included file does not block the save");
    }

    private static void CommandChecks()
    {
        ConfigValidator bash = ConfigValidators.Find("/a.sh", L.Bash, "#!/bin/bash");
        string command = bash.BuildCommand("'/tmp/x.txt'");
        Check(command.Contains("command -v bash"), "the command looks for bash first");
        Check(command.IndexOf("command -v bash", StringComparison.Ordinal) < command.IndexOf("command -v sh", StringComparison.Ordinal), "bash is tried before sh");
        Check(command.Contains("bash -n '/tmp/x.txt' 2>&1; exit $?;"), "the file is quoted into the command and the output merged");
        Check(command.EndsWith("exit 127", StringComparison.Ordinal), "no program found: exit 127");
        Check(!command.Contains("{FILE}"), "no placeholder is left");
    }

    // ---- The generated command, run for real (Unix only; a tool that is not installed skips its checks) ----

    private sealed class Run
    {
        public int Status;

        public List<string> Lines;
    }

    private static Run Execute(string command)
    {
        ProcessStartInfo info = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(command);
        using Process process = Process.Start(info);
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return new Run { Status = process.ExitCode, Lines = output.Split('\n').ToList() };
    }

    private static bool Has(string program)
    {
        return Execute("command -v " + program + " >/dev/null 2>&1").Status == 0;
    }

    private static ValidationResult RunValidator(string path, L language, string firstLine, string content, out string file)
    {
        string directory = Path.Combine(Path.GetTempPath(), "cfgcheck" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        file = Path.Combine(directory, "upload.txt");
        File.WriteAllText(file, content);
        ConfigValidator validator = ConfigValidators.Find(path, language, firstLine);
        Run run = Execute(validator.BuildCommand("'" + file + "'"));
        try
        {
            return validator.Interpret(run.Lines, run.Status, file);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void LiveChecks()
    {
        if (!File.Exists("/bin/sh"))
        {
            Console.WriteLine("SKIP: no /bin/sh, the generated commands are not run");
            return;
        }
        string file;
        if (Has("bash"))
        {
            Equal(RunValidator("/a.sh", L.Bash, "#!/bin/bash", "#!/bin/bash\necho ok\nif true; then\n  echo x\nfi\n", out file).Outcome, ValidationOutcome.Passed, "live bash: a good script passes");
            ValidationResult broken = RunValidator("/a.sh", L.Bash, "#!/bin/bash", "#!/bin/bash\necho ok\nif true; then\n  echo x\n", out file);
            Equal(broken.Outcome, ValidationOutcome.Failed, "live bash: a script with a missing fi fails");
            Check(broken.Issues.Count > 0 && broken.Issues[0].Line >= 4, "live bash: the problem has a line (" + Show(broken.Issues) + ")");
        }
        if (Has("bash"))
        {
            // The case of B-105: typical zsh code is no bash, and bash -n must not be asked about it.
            string zshCode = "#!/usr/bin/env zsh\nfiles=(*(.N))\ndiff =(print a) =(print b)\n";
            Check(ConfigValidators.Find("/home/u/plugin.zsh", L.Bash, "#!/usr/bin/env zsh") == null, "live zsh: plugin.zsh has no check");
            Check(ConfigValidators.Find("/home/u/tool", L.Bash, "#!/usr/bin/env zsh") == null, "live zsh: a zsh shebang has no check");
            // ... and that really is a bash syntax error, i.e. that the old behaviour was a false report.
            string zshDirectory = Path.Combine(Path.GetTempPath(), "zshcheck" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(zshDirectory);
            string zshFile = Path.Combine(zshDirectory, "z.txt");
            File.WriteAllText(zshFile, zshCode);
            Check(Execute("bash -n '" + zshFile + "' >/dev/null 2>&1").Status != 0, "live zsh: bash does reject zsh code (so the check had to go)");
            Directory.Delete(zshDirectory, true);
        }
        if (Has("python3"))
        {
            Equal(RunValidator("/a.py", L.Python, null, "def f(x):\n    return x + 1\n", out file).Outcome, ValidationOutcome.Passed, "live python3: a good file passes");
            ValidationResult broken = RunValidator("/a.py", L.Python, null, "x = 1\ndef f(:\n    pass\n", out file);
            Equal(broken.Outcome, ValidationOutcome.Failed, "live python3: a syntax error fails");
            Equal(broken.Issues[0].Line, 2, "live python3: the line of the error (" + Show(broken.Issues) + ")");
            ValidationResult tabs = RunValidator("/a.py", L.Python, null, "if True:\n    a = 1\n  b = 2\n", out file);
            Equal(tabs.Outcome, ValidationOutcome.Failed, "live python3: an indentation error fails");
            Equal(RunValidator("/a.py", L.Python, null, "print(\"привет\")\n", out file).Outcome, ValidationOutcome.Passed, "live python3: non-ASCII text passes");

            Equal(RunValidator("/a.json", L.Json, null, "{\"a\": [1, 2, 3]}\n", out file).Outcome, ValidationOutcome.Passed, "live json: a good file passes");
            ValidationResult badJson = RunValidator("/a.json", L.Json, null, "{\n  \"a\": 1,\n  \"b\": \n}\n", out file);
            Equal(badJson.Outcome, ValidationOutcome.Failed, "live json: a broken file fails");
            Check(badJson.Issues.Count > 0, "live json: the problem has a line (" + Show(badJson.Issues) + ")");

            string yamlProbe = Execute("python3 -c 'import yaml' 2>/dev/null").Status == 0 ? "yes" : "no";
            if (yamlProbe == "yes")
            {
                Equal(RunValidator("/a.yml", L.Yaml, null, "a: 1\nb:\n  - x\n  - y\n", out file).Outcome, ValidationOutcome.Passed, "live yaml: a good file passes");
                ValidationResult badYaml = RunValidator("/a.yml", L.Yaml, null, "a: 1\nb: [1, 2\nc: 3\n", out file);
                Equal(badYaml.Outcome, ValidationOutcome.Failed, "live yaml: a broken file fails");
                Check(badYaml.Issues.Count > 0, "live yaml: the problem has a line (" + Show(badYaml.Issues) + ")");
                Equal(RunValidator("/a.yml", L.Yaml, null, "Resources:\n  Bucket: !Ref MyBucket\n", out file).Outcome, ValidationOutcome.Passed, "live yaml: a custom tag is not an error");
                // B-105: templates are no YAML until they are rendered.
                Equal(RunValidator("/chart/templates/deploy.yaml", L.Yaml, null, "apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: {{ .Release.Name }}\nspec:\n  {{- if .Values.x }}\n  replicas: 2\n  {{- end }}\n", out file).Outcome, ValidationOutcome.Unavailable, "live yaml: a Helm template is not reported as a YAML error");
                Equal(RunValidator("/a.yaml", L.Yaml, null, "{% if x %}\na: 1\n{% endif %}\n", out file).Outcome, ValidationOutcome.Unavailable, "live yaml: a Jinja block is not reported as a YAML error");
                Equal(RunValidator("/play.yml", L.Yaml, null, "- hosts: all\n  vars:\n    x: \"{{ y }}\"\n", out file).Outcome, ValidationOutcome.Passed, "live yaml: a quoted Ansible expression is plain YAML and passes");
                Equal(RunValidator("/play.yml", L.Yaml, null, "- hosts: all\n  vars:\n    x: \"{{ y }}\"\n    z: [1, 2\n", out file).Outcome, ValidationOutcome.Unavailable, "live yaml: a broken file that also holds an expression is not blamed (it may be a template)");
                Equal(RunValidator("/a.yml", L.Yaml, null, "a: ${{ secrets.X }}\nb: [1, 2\n", out file).Outcome, ValidationOutcome.Failed, "live yaml: GitHub's ${{ }} is no template: a real error is still reported");
            }
            else
            {
                Equal(RunValidator("/a.yml", L.Yaml, null, "a: [1, 2\n", out file).Outcome, ValidationOutcome.Unavailable, "live yaml without PyYAML: Unavailable, never a failure");
            }
        }
        if (Has("sh") && Has("xmllint"))
        {
            Equal(RunValidator("/a.xml", L.Markup, null, "<a><b/></a>\n", out file).Outcome, ValidationOutcome.Passed, "live xmllint: a good file passes");
            ValidationResult badXml = RunValidator("/a.xml", L.Markup, null, "<a>\n<b>\n</a>\n", out file);
            Equal(badXml.Outcome, ValidationOutcome.Failed, "live xmllint: a broken file fails");
        }
        if (Has("sh"))
        {
            Equal(RunValidator("/etc/ssh/sshd_config", L.Config, null, "Port 22\n", out file).Outcome == ValidationOutcome.Failed, false, "live sshd: a file never fails just because the host cannot run sshd");
        }
    }

    private static void Main()
    {
        SelectionChecks();
        ParseChecks();
        InterpretChecks();
        CommandChecks();
        LiveChecks();
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " config validation checks passed.");
    }
}
