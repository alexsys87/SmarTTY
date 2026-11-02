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

    private static int Main()
    {
        _work = Path.Combine(Path.GetTempPath(), "smartty-quick-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_work);
        try
        {
            RuleTests();
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
            int exit = Run(busybox, QuickCommandRules.BuildUploadCommand(remotePath), data, out _, out string error);
            Check(exit == 0 && File.ReadAllBytes(remotePath).SequenceEqual(data), label + " " + fileName + ": the upload stores the script (" + error.Trim() + ")");
            Check((File.GetUnixFileMode(remotePath) & UnixFileMode.UserExecute) != 0 && (File.GetUnixFileMode(remotePath) & UnixFileMode.OtherExecute) != 0, label + " " + fileName + ": the script is executable (755)");
            Check(!File.Exists(remotePath + ".smartty-part"), label + " " + fileName + ": no temporary file is left");
            Run(busybox, QuickCommandRules.BuildExistsCommand(remotePath), null, out string existsAfter, out _);
            Equal(existsAfter.Trim(), "exists", label + " " + fileName + ": an existing file is reported");

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
