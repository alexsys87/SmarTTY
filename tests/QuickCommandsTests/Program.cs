using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using SmarTTY.Terminal;
using VisualTTY;

// Regression harness for Tools > Quick commands (Terminal/QuickCommands.cs): the rules of commands and scripts, the items of the
// menu of the terminal, QuickCommands.xml (read back exactly, accepted by the settings archive), and the shell commands that put a
// script into the current folder and the text typed to run it, run for real by /bin/sh and by BusyBox sh.
// Run with: dotnet run --project tests/QuickCommandsTests
internal static class Program
{
    private static int _passed;

    private static string _work;

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

    /// <summary>
    /// The upload replaces a file of the same name without a word, so only a check that ran to its end and printed nothing may let
    /// it go on unasked: a check that timed out (no lines), lost its channel, was closed without a status or failed is "unknown".
    /// </summary>
    private static void ExistsAnswerTests()
    {
        Equal(QuickCommandRules.ReadExistsAnswer(new string[0], 0), RemoteNameState.Free, "exists answer: no output from a check that ended with 0 is 'free'");
        Equal(QuickCommandRules.ReadExistsAnswer(new[] { "exists" }, 0), RemoteNameState.Exists, "exists answer: 'exists' is 'exists'");
        Equal(QuickCommandRules.ReadExistsAnswer(new[] { "exists\r" }, 0), RemoteNameState.Exists, "exists answer: a carriage return after it does not matter");
        Equal(QuickCommandRules.ReadExistsAnswer(new[] { " exists " }, 0), RemoteNameState.Exists, "exists answer: nor do blanks");
        Equal(QuickCommandRules.ReadExistsAnswer(new[] { "exists" }, null), RemoteNameState.Exists, "exists answer: 'exists' counts without an exit status (the file is there)");
        Equal(QuickCommandRules.ReadExistsAnswer(new[] { "exists" }, 1), RemoteNameState.Exists, "exists answer: 'exists' counts after another status as well");
        Equal(QuickCommandRules.ReadExistsAnswer(null, null), RemoteNameState.Unknown, "exists answer: a check that could not be run or was abandoned (null) is 'unknown'");
        Equal(QuickCommandRules.ReadExistsAnswer(null, 0), RemoteNameState.Unknown, "exists answer: no lines at all are 'unknown', whatever the status");
        Equal(QuickCommandRules.ReadExistsAnswer(new string[0], null), RemoteNameState.Unknown, "exists answer: no output and no exit status (the channel was closed) is 'unknown'");
        Equal(QuickCommandRules.ReadExistsAnswer(new string[0], 1), RemoteNameState.Unknown, "exists answer: no output and a failure is 'unknown'");
        Equal(QuickCommandRules.ReadExistsAnswer(new string[0], -1), RemoteNameState.Unknown, "exists answer: no output and a signal is 'unknown'");
        Equal(QuickCommandRules.ReadExistsAnswer(new[] { "sh: 1: not found" }, 127), RemoteNameState.Unknown, "exists answer: a shell that did not start is 'unknown'");
        Equal(QuickCommandRules.ReadExistsAnswer(new[] { "Last login: today" }, 0), RemoteNameState.Free, "exists answer: other lines of a check that ended with 0 do not make the name taken");
        Equal(QuickCommandRules.ReadExistsAnswer(new[] { "not exists" }, 0), RemoteNameState.Free, "exists answer: only the whole line 'exists' counts");
        Equal(QuickCommandRules.ReadExistsAnswer(new string[] { null }, 0), RemoteNameState.Free, "exists answer: an empty entry is no answer");
    }

    private static int Main()
    {
        _work = Path.Combine(Path.GetTempPath(), "smartty-quick-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_work);
        try
        {
            RuleTests();
            ExistsAnswerTests();
            MenuTests();
            XmlTests();
            if (OperatingSystem.IsWindows())
            {
                Console.WriteLine("SKIP: the shell commands are not run on Windows");
            }
            else
            {
                ShellTests(null);
                string busybox = new[] { "/usr/bin/busybox", "/bin/busybox" }.FirstOrDefault(File.Exists);
                if (busybox == null)
                {
                    Console.WriteLine("SKIP: busybox is not installed, the commands are not run with the tools of OpenWrt");
                }
                else
                {
                    ShellTests(busybox);
                }
            }
            Console.WriteLine($"All {_passed} checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }
        finally
        {
            try
            {
                Directory.Delete(_work, true);
            }
            catch
            {
            }
        }
    }

    private static QuickCommand Command(string text, string name = null, bool enabled = true) => new QuickCommand { Kind = QuickCommandKind.Command, Text = text, Name = name, Enabled = enabled };

    private static QuickCommand Script(string fileName, string text, string name = null, bool enabled = true) => new QuickCommand { Kind = QuickCommandKind.Script, FileName = fileName, Text = text, Name = name, Enabled = enabled };

    private static void RuleTests()
    {
        Check(QuickCommandRules.Validate(Command("cat /proc/mtd")) == null, "a command");
        Check(QuickCommandRules.Validate(Command("  ")) != null && QuickCommandRules.Validate(Command(null)) != null, "an empty command is refused");
        Check(QuickCommandRules.Validate(Command(new string('x', 5000))).Contains("script"), "a very long command is refused, a script is suggested");
        Equal(QuickCommandRules.ToCommandLine("  sudo make install \r\n"), "sudo make install", "the spaces around a command are dropped");
        Equal(QuickCommandRules.ToCommandLine("cd /tmp &&\r\n  make\nmake install"), "cd /tmp && make make install", "pasted lines are joined into one line");

        Check(QuickCommandRules.Validate(Script("setup.sh", "#!/bin/sh\necho hi\n")) == null, "a script");
        Check(QuickCommandRules.Validate(Script("", "echo")).Contains("file name"), "a script needs a file name");
        Check(QuickCommandRules.Validate(Script("../x.sh", "echo")) != null && QuickCommandRules.Validate(Script("a/b.sh", "echo")) != null && QuickCommandRules.Validate(Script("..", "echo")) != null, "the file name stays in the current folder");
        Check(QuickCommandRules.Validate(Script("a\tb.sh", "echo")) != null && QuickCommandRules.Validate(Script(" a.sh", "echo")) != null, "no control characters, no spaces around the name");
        Check(QuickCommandRules.Validate(Script("my script.sh", "echo")) == null, "a name with a space inside is fine");
        Check(QuickCommandRules.Validate(Script("a.sh", "  \n ")).Contains("empty"), "an empty script is refused");
        Check(QuickCommandRules.Validate(Script("a.sh", "echo \0")) != null, "binary data is refused");

        byte[] bytes = QuickCommandRules.ScriptBytes("﻿#!/bin/sh\r\necho привет\r\n\techo end");
        Equal(Encoding.UTF8.GetString(bytes), "#!/bin/sh\necho привет\n\techo end\n", "the script is stored with Unix line ends, no BOM, a final line break, UTF-8");

        Equal(QuickCommandRules.BuildInvocation("setup.sh"), "./setup.sh", "invocation: ./name");
        Equal(QuickCommandRules.BuildInvocation("my script.sh"), "./'my script.sh'", "invocation: a name that needs quotes");
        Equal(QuickCommandRules.BuildInvocation("it's.sh"), "./'it'\\''s.sh'", "invocation: a quote in the name");
        Equal(QuickCommandRules.BuildInvocation("setup.sh", "/root"), "/root/setup.sh", "invocation: the full path when the script is in another folder");
        Equal(QuickCommandRules.BuildInvocation("setup.sh", "/"), "/setup.sh", "invocation: the root folder");
        Equal(QuickCommandRules.RemotePath("/home/u/", "a.sh"), "/home/u/a.sh", "remote path");
        Equal(QuickCommandRules.RemotePath(null, "a.sh"), "./a.sh", "remote path: the current folder when no folder is given");

        SerialRuleTests();
    }

    // A script typed into a device (COM port, Telnet) goes through the terminal line of the device: what that line takes for a key
    // must not be in the text (all of this was tried on a pty with dash, bash and BusyBox ash).
    private static void SerialRuleTests()
    {
        Check(QuickCommandRules.ValidateForSerial(Command("cat /proc/mtd")) == null, "serial: a command has no limits of the line of the device");
        Check(QuickCommandRules.ValidateForSerial(Script("a.sh", "#!/bin/sh\r\n\tif true; then\r\n\t\techo \"привет, мир — ✓\"\r\n\tfi\r\n")) == null, "serial: tabs, UTF-8 and CR LF pass");
        Check(QuickCommandRules.ValidateForSerial(Script("a.sh", "  \n")) is string empty && empty.Contains("empty"), "serial: an empty script is refused as before");
        Check(QuickCommandRules.ValidateForSerial(Script("", "echo")) != null, "serial: a script needs a file name as before");
        foreach (int key in new[] { 0x01, 0x03, 0x04, 0x08, 0x11, 0x13, 0x15, 0x16, 0x17, 0x1A, 0x1B, 0x1C, 0x7F })
        {
            string problem = QuickCommandRules.ValidateForSerial(Script("a.sh", "#!/bin/sh\necho a" + (char)key + "b\n"));
            Check(problem != null && problem.StartsWith("Line 2 ", StringComparison.Ordinal) && problem.Contains("0x" + key.ToString("X2")), "serial: the control character 0x" + key.ToString("X2") + " is refused with its line (" + problem + ")");
        }
        Check(QuickCommandRules.ValidateForSerial(Script("a.sh", "#!/bin/sh\n\n\necho a\u0003\n")) is string third && third.StartsWith("Line 4 ", StringComparison.Ordinal), "serial: the line number counts the empty lines");
        Check(QuickCommandRules.ValidateForSerial(Script("a.sh", "#!/bin/sh\n" + new string('a', QuickCommandRules.MaxSerialLineLength) + "\n")) == null, "serial: a line of the longest size passes");
        Check(QuickCommandRules.ValidateForSerial(Script("a.sh", "#!/bin/sh\n" + new string('a', QuickCommandRules.MaxSerialLineLength + 1) + "\n")) is string tooLong && tooLong.StartsWith("Line 2 ", StringComparison.Ordinal) && tooLong.Contains("longer than"), "serial: a longer line is refused");
        Check(QuickCommandRules.ValidateForSerial(Script("a.sh", new string('я', QuickCommandRules.MaxSerialLineLength / 2) + "\n")) == null, "serial: the line is measured in bytes, not characters (the longest line of UTF-8 passes)");
        Check(QuickCommandRules.ValidateForSerial(Script("a.sh", new string('я', QuickCommandRules.MaxSerialLineLength / 2 + 1) + "\n")) != null, "serial: the line is measured in bytes, not characters (one more is refused)");
        Check(QuickCommandRules.ValidateForSerial(Script("a.sh", "echo a\n" + new string('b', 3000) + "\n" + new string('c', 3000) + "\n")) == null, "serial: the limit is for a line, not for the whole script");
    }

    private static void MenuTests()
    {
        QuickCommandList list = new QuickCommandList();
        list.Items.Add(Command("cat /proc/mtd", "Flash partitions"));
        list.Items.Add(Command("sudo make install"));
        list.Items.Add(Command("reboot", "Reboot", enabled: false));
        list.Items.Add(Script("setup.sh", "#!/bin/sh\necho hi", "Setup"));
        list.Items.Add(Command(""));
        List<QuickCommand> items = QuickCommandRules.MenuItems(list, scriptsPossible: true);
        Check(items.Select(i => i.MenuText).SequenceEqual(new[] { "Flash partitions", "sudo make install", "Setup" }), "menu: the shown items in their order, a hidden and an invalid one left out (" + string.Join(", ", items.Select(i => i.MenuText)) + ")");
        Check(QuickCommandRules.MenuItems(list, scriptsPossible: false).All(i => i.Kind == QuickCommandKind.Command), "menu: no scripts where they cannot run");
        Equal(Script("x.sh", "echo").MenuText, "x.sh", "menu text: a script without a name shows its file name");
        Check(Command(new string('a', 100)).MenuText.Length == 60 && Command(new string('a', 100)).MenuText.EndsWith("..."), "menu text: a long command is shortened");
        Check(QuickCommandRules.MenuItems(null, true).Count == 0, "menu: no list");
    }

    private static void XmlTests()
    {
        string directory = Path.Combine(_work, "settings");
        Check(QuickCommandList.Load(directory).Items.Count == 0, "xml: no file is an empty list");
        QuickCommandList list = new QuickCommandList();
        string script = "#!/bin/sh\n# <tag> & \"quotes\" 'single'\n\tif [ -d /tmp ]; then\n    echo привет $HOME\n  fi\n\n";
        list.Items.Add(Command("cat /proc/mtd | grep -v \"dev:\" && echo <done>", "MTD & more"));
        list.Items.Add(Script("setup.sh", script, "Setup", enabled: false));
        list.Save(directory);
        string path = Path.Combine(directory, QuickCommandList.FileName);
        Check(File.Exists(path) && !File.Exists(path + ".tmp"), "xml: saved as QuickCommands.xml, no temporary file left");
        QuickCommandList read = QuickCommandList.Load(directory);
        Check(read.Items.Count == 2 && read.Items[0].Name == "MTD & more" && read.Items[0].Text == "cat /proc/mtd | grep -v \"dev:\" && echo <done>" && read.Items[0].Kind == QuickCommandKind.Command && read.Items[0].Enabled, "xml: a command is read back exactly");
        Check(read.Items[1].Kind == QuickCommandKind.Script && read.Items[1].FileName == "setup.sh" && !read.Items[1].Enabled && read.Items[1].Text == script, "xml: a script with tabs, indentation, empty lines, quotes and Cyrillic is read back exactly, switched off");

        // Saving again replaces the file.
        read.Items.RemoveAt(0);
        read.Save(directory);
        Check(QuickCommandList.Load(directory).Items.Count == 1, "xml: saving replaces the file");

        // CR LF in the file (edited on Windows) comes back as LF.
        File.WriteAllText(path, "<?xml version=\"1.0\"?>\r\n<QuickCommandList><Items><QuickCommand><Kind>Script</Kind><FileName>a.sh</FileName><Text>echo 1&#xD;\r\necho 2</Text></QuickCommand></Items></QuickCommandList>");
        Equal(QuickCommandList.Load(directory).Items[0].Text, "echo 1\necho 2", "xml: CR LF becomes LF");
        Check(QuickCommandList.Load(directory).Items[0].Enabled, "xml: an item without <Enabled> is shown");

        File.WriteAllText(path, "<QuickCommandList><Items><QuickCommand>");
        bool threw = false;
        try
        {
            QuickCommandList.Load(directory);
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }
        Check(threw, "xml: a damaged file throws, so that the window can say so");
        File.WriteAllText(path, "<!DOCTYPE x [<!ENTITY e \"boom\">]><QuickCommandList><Items /></QuickCommandList>");
        threw = false;
        try
        {
            QuickCommandList.Load(directory);
        }
        catch (Exception)
        {
            threw = true;
        }
        Check(threw, "xml: a DTD is not processed");

        // The settings archive takes the file as the quick commands part (the same root element).
        list.Save(directory);
        string local = Path.Combine(_work, "local");
        Directory.CreateDirectory(local);
        SettingsLocations locations = new SettingsLocations { DataDirectory = directory, LocalDataDirectory = local };
        Check(SettingsArchive.Describe(locations).Available == SettingsParts.QuickCommands, "archive: the saved file is found as the quick commands");
        string archive = Path.Combine(_work, "settings.tar.gz");
        SettingsArchive.Export(archive, locations, SettingsParts.Default);
        string target = Path.Combine(_work, "imported");
        Directory.CreateDirectory(target);
        SettingsArchive.ImportResult result = SettingsArchive.Import(archive, new SettingsLocations { DataDirectory = target, LocalDataDirectory = local }, SettingsParts.Default, null);
        Check(result.QuickCommandsImported && QuickCommandList.Load(target).Items.Count == 2 && QuickCommandList.Load(target).Items[1].Text == script, "archive: exported and imported, the list is the same");
        Equal(SettingsArchiveFileName(), QuickCommandList.FileName, "archive: the file name is the same in both places");
    }

    private static string SettingsArchiveFileName() => SettingsArchive.QuickCommandsFileName;

    /// <summary>Puts a script into a folder with the upload command, then runs the text the menu types, from that folder.</summary>
    private static void ShellTests(string busybox)
    {
        string label = busybox == null ? "sh" : "BusyBox";
        string folder = Path.Combine(_work, "remote-" + Guid.NewGuid().ToString("N"), "my folder");
        Directory.CreateDirectory(folder);
        foreach (string fileName in new[] { "setup.sh", "my script.sh", "it's.sh", "-dash.sh" })
        {
            string remotePath = QuickCommandRules.RemotePath(folder, fileName);
            byte[] data = QuickCommandRules.ScriptBytes("#!/bin/sh\r\necho \"ran $0 in $(pwd)\"\r\necho arg=$1");
            Equal(Run(busybox, QuickCommandRules.BuildExistsCommand(remotePath), null, out string existsBefore, out _), 0, label + " " + fileName + ": the check of the name runs");
            Check(existsBefore.Trim() == "", label + " " + fileName + ": a missing file is not reported");
            Equal(QuickCommandRules.ReadExistsAnswer(existsBefore.Split('\n'), 0), RemoteNameState.Free, label + " " + fileName + ": the answer of the check is read as 'free'");
            int exit = Run(busybox, QuickCommandRules.BuildUploadCommand(remotePath), data, out _, out string error);
            Check(exit == 0 && File.ReadAllBytes(remotePath).SequenceEqual(data), label + " " + fileName + ": the upload stores the script (" + error.Trim() + ")");
            Check((File.GetUnixFileMode(remotePath) & UnixFileMode.UserExecute) != 0 && (File.GetUnixFileMode(remotePath) & UnixFileMode.OtherExecute) != 0, label + " " + fileName + ": the script is executable (755)");
            Check(!File.Exists(remotePath + ".smartty-part"), label + " " + fileName + ": no temporary file is left");
            Equal(Run(busybox, QuickCommandRules.BuildExistsCommand(remotePath), null, out string existsAfter, out _), 0, label + " " + fileName + ": the check ends with status 0 when the file is there, as when it is not");
            Equal(existsAfter.Trim(), "exists", label + " " + fileName + ": an existing file is reported");
            Equal(QuickCommandRules.ReadExistsAnswer(existsAfter.Split('\n'), 0), RemoteNameState.Exists, label + " " + fileName + ": the answer of the check is read as 'exists'");

            // What the menu types into the command line, run in the current folder of the terminal.
            string invocation = QuickCommandRules.BuildInvocation(fileName);
            exit = Run(busybox, "cd " + SmarTTY.IO.RemotePathUtils.ShellQuote(folder) + " && " + invocation + " 42", null, out string output, out error);
            Check(exit == 0 && output.Contains("ran ./") && output.Contains("in " + folder) && output.Contains("arg=42"), label + " " + fileName + ": " + invocation + " runs it in the current folder (" + output.Trim().Replace('\n', ' ') + error.Trim() + ")");
            // From another folder, by its full path (the folder of the terminal could not be told).
            exit = Run(busybox, "cd / && " + QuickCommandRules.BuildInvocation(fileName, folder), null, out output, out _);
            Check(exit == 0 && output.Contains("ran " + remotePath), label + " " + fileName + ": the full path runs it from another folder");
        }

        // Replacing an existing script.
        string replaced = QuickCommandRules.RemotePath(folder, "setup.sh");
        byte[] second = QuickCommandRules.ScriptBytes("#!/bin/sh\necho second");
        Check(Run(busybox, QuickCommandRules.BuildUploadCommand(replaced), second, out _, out _) == 0 && File.ReadAllBytes(replaced).SequenceEqual(second), label + ": a script is replaced");

        // A dangling link counts as an existing name: the upload would write through it.
        string link = Path.Combine(folder, "link.sh");
        File.CreateSymbolicLink(link, Path.Combine(folder, "nowhere"));
        Run(busybox, QuickCommandRules.BuildExistsCommand(link), null, out string linkExists, out _);
        Equal(linkExists.Trim(), "exists", label + ": a dangling link is reported");

        // A folder that does not exist: the upload fails and leaves nothing.
        string missing = QuickCommandRules.RemotePath(Path.Combine(folder, "missing"), "a.sh");
        Check(Run(busybox, QuickCommandRules.BuildUploadCommand(missing), second, out _, out string missingError) != 0 && missingError.Length > 0, label + ": the upload into a missing folder fails with a message");

        SerialShellTests(busybox, label, folder);
    }

    // The command that is typed into the shell of a device (BuildSerialUploadCommand) stores its standard input the same way: the
    // lines that follow it on the terminal line are read by cat until Ctrl-D (the end of the input), which a pipe shows as its end.
    private static void SerialShellTests(string busybox, string label, string folder)
    {
        foreach (string fileName in new[] { "setup.sh", "my script.sh", "it's.sh", "-dash.sh" })
        {
            string remotePath = QuickCommandRules.RemotePath(folder, fileName);
            byte[] data = QuickCommandRules.ScriptBytes("#!/bin/sh\r\necho \"ran $0 in $(pwd)\"\r\n\techo \"tab\tinside\" привет\r\necho arg=$1");
            string command = QuickCommandRules.BuildSerialUploadCommand(remotePath);
            Check(command.StartsWith("/bin/sh -c ", StringComparison.Ordinal) && command.Contains("trap "), label + " " + fileName + ": serial command: one /bin/sh -c word with a trap");
            int exit = Run(busybox, command, data, out _, out string error);
            Check(exit == 0 && File.ReadAllBytes(remotePath).SequenceEqual(data), label + " " + fileName + ": serial upload stores the script (" + error.Trim() + ")");
            Check((File.GetUnixFileMode(remotePath) & UnixFileMode.UserExecute) != 0 && (File.GetUnixFileMode(remotePath) & UnixFileMode.OtherExecute) != 0, label + " " + fileName + ": serial upload: the script is executable");
            Check(!File.Exists(remotePath + ".smartty-part"), label + " " + fileName + ": serial upload: no temporary file is left");
            exit = Run(busybox, "cd " + SmarTTY.IO.RemotePathUtils.ShellQuote(folder) + " && " + QuickCommandRules.BuildInvocation(fileName) + " 7", null, out string output, out error);
            Check(exit == 0 && output.Contains("arg=7") && output.Contains("tab\tinside привет"), label + " " + fileName + ": the stored script runs (" + output.Trim().Replace('\n', ' ') + error.Trim() + ")");
        }

        // The path is relative to the current folder of the shell, as the terminal types it ("./name").
        if (busybox == null)
        {
            string relative = "relative.sh";
            byte[] relativeData = QuickCommandRules.ScriptBytes("#!/bin/sh\necho relative");
            int relativeExit = Run(null, "cd " + SmarTTY.IO.RemotePathUtils.ShellQuote(folder) + " && " + QuickCommandRules.BuildSerialUploadCommand(QuickCommandRules.RemotePath(null, relative)), relativeData, out _, out string relativeError);
            Check(relativeExit == 0 && File.ReadAllBytes(Path.Combine(folder, relative)).SequenceEqual(relativeData), "sh: serial upload into the current folder by ./name (" + relativeError.Trim() + ")");
        }

        // A file with this name is replaced.
        string replaced = QuickCommandRules.RemotePath(folder, "setup.sh");
        byte[] second = QuickCommandRules.ScriptBytes("#!/bin/sh\necho second");
        Check(Run(busybox, QuickCommandRules.BuildSerialUploadCommand(replaced), second, out _, out _) == 0 && File.ReadAllBytes(replaced).SequenceEqual(second), label + ": serial upload replaces a script");

        // A folder that does not exist: the upload fails with a message and leaves nothing.
        string missing = QuickCommandRules.RemotePath(Path.Combine(folder, "missing-serial"), "a.sh");
        Check(Run(busybox, QuickCommandRules.BuildSerialUploadCommand(missing), second, out _, out string missingError) != 0 && missingError.Length > 0, label + ": serial upload into a missing folder fails with a message");

        SerialCancelTest(busybox, label, folder);
    }

    // Esc in the terminal ends the upload with Ctrl-C: the tty sends SIGINT to the whole foreground job (the shell of the command and
    // cat). The command has to leave neither half a script under the name nor its temporary file. setsid gives the process a group
    // of its own (its pid), which is what the foreground job is.
    private static void SerialCancelTest(string busybox, string label, string folder)
    {
        string setsid = new[] { "/usr/bin/setsid", "/bin/setsid" }.FirstOrDefault(File.Exists);
        if (setsid == null)
        {
            Console.WriteLine("SKIP: setsid is not installed, the cancelled upload is not tried");
            return;
        }
        string remotePath = QuickCommandRules.RemotePath(folder, "cancel.sh");
        string temporary = remotePath + ".smartty-part";
        string command = QuickCommandRules.BuildSerialUploadCommand(remotePath);
        ProcessStartInfo start = new ProcessStartInfo(setsid)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("/bin/sh");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(busybox != null ? busybox + " sh -c " + command.Substring("/bin/sh -c ".Length) : command);
        if (busybox != null)
        {
            start.Environment["PATH"] = Applets(busybox) + ":" + Environment.GetEnvironmentVariable("PATH");
        }
        using Process process = Process.Start(start);
        System.Threading.Tasks.Task<string> output = process.StandardOutput.ReadToEndAsync();
        System.Threading.Tasks.Task<string> error = process.StandardError.ReadToEndAsync();
        byte[] half = Encoding.UTF8.GetBytes("#!/bin/sh\necho half\n");
        process.StandardInput.BaseStream.Write(half, 0, half.Length);
        process.StandardInput.BaseStream.Flush();
        Check(WaitFor(() => File.Exists(temporary) && new FileInfo(temporary).Length >= half.Length, 5000), label + ": while the upload runs the text is in the temporary file");
        // The kill program, not the built-in of a shell (dash does not take "--" to tell a group from an option).
        ProcessStartInfo kill = new ProcessStartInfo("/usr/bin/env") { RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in new[] { "kill", "-INT", "--", "-" + process.Id })
        {
            kill.ArgumentList.Add(argument);
        }
        using (Process killer = Process.Start(kill))
        {
            string killError = killer.StandardError.ReadToEnd();
            killer.WaitForExit();
            Equal(killer.ExitCode, 0, label + ": SIGINT goes to the group of the upload (" + killError.Trim() + ")");
        }
        Check(process.WaitForExit(5000), label + ": the cancelled upload ends");
        Check(WaitFor(() => !File.Exists(temporary), 5000), label + ": a cancelled upload removes its temporary file");
        Check(!File.Exists(remotePath), label + ": a cancelled upload leaves no script under the name");
    }

    private static bool WaitFor(Func<bool> condition, int milliseconds)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < end)
        {
            if (condition())
            {
                return true;
            }
            System.Threading.Thread.Sleep(20);
        }
        return condition();
    }

    private static string _applets;

    private static string Applets(string busybox)
    {
        if (_applets == null)
        {
            _applets = Path.Combine(_work, "applets");
            Directory.CreateDirectory(_applets);
            foreach (string applet in new[] { "cat", "chmod", "mv", "rm", "pwd", "sh" })
            {
                File.CreateSymbolicLink(Path.Combine(_applets, applet), busybox);
            }
        }
        return _applets;
    }

    /// <summary>"/bin/sh -c '...'" as it is, or with BusyBox sh and its applets first in PATH.</summary>
    private static int Run(string busybox, string command, byte[] input, out string stdout, out string stderr)
    {
        ProcessStartInfo start = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(busybox != null && command.StartsWith("/bin/sh -c ", StringComparison.Ordinal) ? busybox + " sh -c " + command.Substring("/bin/sh -c ".Length) : busybox != null ? busybox + " sh -c " + SmarTTY.IO.RemotePathUtils.ShellQuote(command) : command);
        if (busybox != null)
        {
            start.Environment["PATH"] = Applets(busybox) + ":" + Environment.GetEnvironmentVariable("PATH");
        }
        using Process process = Process.Start(start);
        System.Threading.Tasks.Task<string> output = process.StandardOutput.ReadToEndAsync();
        System.Threading.Tasks.Task<string> error = process.StandardError.ReadToEndAsync();
        try
        {
            if (input != null)
            {
                process.StandardInput.BaseStream.Write(input, 0, input.Length);
            }
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }
        process.WaitForExit();
        stdout = output.Result;
        stderr = error.Result;
        return process.ExitCode;
    }
}
