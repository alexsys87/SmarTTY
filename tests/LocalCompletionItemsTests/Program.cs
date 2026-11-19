using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SmarTTY.Terminal;
using VisualTTY;
using VisualTTY.DeviceTerminal;

// Checks of what the completion of the input line of a COM/TCP terminal offers (Terminal/LocalCompletionItems.cs): the built-in commands,
// the programs on this computer, the names of a directory of the device, and the small rules that read the word being completed.
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
        bool same = EqualityComparer<T>.Default.Equals(actual, expected);
        Check(same, name + (same ? "" : " (expected '" + expected + "', got '" + actual + "')"));
    }

    private static string Join(IEnumerable<string> items) => string.Join("|", items);

    private static void DirectoryPrefixChecks()
    {
        Equal(LocalCompletionItems.GetDeviceDirectoryPrefix("/etc/ho"), "/etc/", "the directory part of an absolute word");
        Equal(LocalCompletionItems.GetDeviceDirectoryPrefix("~/"), "~/", "of a home word");
        Equal(LocalCompletionItems.GetDeviceDirectoryPrefix("a/b/c"), "a/b/", "of a relative word");
        Equal(LocalCompletionItems.GetDeviceDirectoryPrefix("file"), "", "a word with no slash names something in the directory of the shell");
        Equal(LocalCompletionItems.GetDeviceDirectoryPrefix(""), "", "an empty word");
        Equal(LocalCompletionItems.GetDeviceDirectoryPrefix(null), "", "no word");
        Equal(LocalCompletionItems.GetDeviceDirectoryPrefix("\"my dir/fi"), "my dir/", "a quoted word is read as the shell reads it");
        Equal(LocalCompletionItems.GetDeviceDirectoryPrefix("/"), "/", "the root");
    }

    private static void WordRuleChecks()
    {
        Check(!LocalCompletionItems.ContainsUnescapedSpace("abc"), "no space");
        Check(LocalCompletionItems.ContainsUnescapedSpace("a b"), "a space");
        Check(!LocalCompletionItems.ContainsUnescapedSpace("a\\ b"), "a space after a backslash is escaped");
        Check(LocalCompletionItems.ContainsUnescapedSpace("a\\\\ b"), "a space after two backslashes is not");
        Check(!LocalCompletionItems.ContainsUnescapedSpace("a\\\\\\ b"), "after three it is");
        Check(LocalCompletionItems.ContainsUnescapedSpace(" a"), "a space at the start");
        Check(!LocalCompletionItems.ContainsUnescapedSpace(""), "empty");
        Equal(new string(LocalCompletionItems.CompletionMetaChars), "`$<>|&;", "the characters that make a word no word of a path");
        Equal(LocalCompletionItems.CommonPrefix("settings", "set"), "set", "the common start");
        Equal(LocalCompletionItems.CommonPrefix("abc", "xyz"), "", "nothing in common");
        Equal(LocalCompletionItems.CommonPrefix("same", "same"), "same", "the same word");
        Equal(LocalCompletionItems.CommonPrefix("Abc", "abc"), "", "the case matters");
        Equal(LocalCompletionItems.CommonPrefix("", "abc"), "", "an empty word");
    }

    private static void PathItemChecks()
    {
        Equal(LocalCompletionItems.BuildPathItems("/", null, false).Count, 0, "no entries, no items");
        DeviceDirectoryEntry[] entries =
        {
            new DeviceDirectoryEntry("zeta.txt", false),
            new DeviceDirectoryEntry("beta", true),
            new DeviceDirectoryEntry("Alpha.c", false),
            new DeviceDirectoryEntry("alpha", true),
            new DeviceDirectoryEntry("my file", false),
            new DeviceDirectoryEntry("my dir", true),
        };
        List<CompletionItem> items = LocalCompletionItems.BuildPathItems("/home/", entries, false);
        Equal(Join(items.Select((CompletionItem item) => item.Text)), "alpha|beta|my dir|Alpha.c|my file|zeta.txt", "directories first, then files, each sorted by name (by code)");
        CompletionItem beta = items.First((CompletionItem item) => item.Text == "beta");
        Equal(beta.InsertText, "/home/beta/", "a directory is inserted with its path and a slash");
        Equal(beta.Icon, VirtualTerminalPopupItemIcon.Directory, "with the icon of a directory");
        VirtualTerminalPopupSummary.FileOrDirectoryInfo info = (VirtualTerminalPopupSummary.FileOrDirectoryInfo)beta.Summary;
        Equal(info.FullPath, "/home/beta", "the summary has the full path");
        Check(info.IsDirectory, "and says it is a directory");
        CompletionItem file = items.First((CompletionItem item) => item.Text == "Alpha.c");
        Equal(file.InsertText, "/home/Alpha.c", "a file is inserted with its path");
        Equal(file.Icon, VirtualTerminalPopupItemIcon.File, "with the icon of a file");
        Check(!((VirtualTerminalPopupSummary.FileOrDirectoryInfo)file.Summary).IsDirectory, "the summary says it is no directory");
        Equal(items.First((CompletionItem item) => item.Text == "my file").InsertText, "/home/my\\ file", "a name with a space is escaped for the shell");
        Equal(items.First((CompletionItem item) => item.Text == "my dir").InsertText, "/home/my\\ dir/", "a directory too, and the slash stays out of the escape");
        List<CompletionItem> directories = LocalCompletionItems.BuildPathItems("", entries, true);
        Equal(Join(directories.Select((CompletionItem item) => item.Text)), "alpha|beta|my dir", "only the directories when asked");
        Equal(directories[0].InsertText, "alpha/", "no directory part: just the name");
    }

    private static void BuiltInChecks()
    {
        List<CompletionItem> items = LocalCompletionItems.GetBuiltInCommands().ToList();
        Equal(items.Count, 30, "30 built-in commands");
        Equal(Join(items.Select((CompletionItem item) => item.Text)),
            "help|clear|history|exit|quit|cd|pwd|ls|dir|cat|type|echo|set|unset|export|env|send|sendhex|sendfile|recv|recvfile|break|reset|reconnect|disconnect|log|logstart|logstop|baud|settings",
            "in this order");
        Check(items.All((CompletionItem item) => item.Text == item.InsertText && item.Icon == VirtualTerminalPopupItemIcon.Command), "each is inserted as it is, with the icon of a command");
        Check(items.All((CompletionItem item) => ((VirtualTerminalPopupSummary.OptionInfo)item.Summary).Option == item.Text), "the summary names the command");
        VirtualTerminalPopupSummary.OptionInfo cd = (VirtualTerminalPopupSummary.OptionInfo)items.First((CompletionItem item) => item.Text == "cd").Summary;
        Equal(cd.Description.Summary, "cd <directory>", "the signature of cd");
        Equal(cd.Description.Info, "Change the current directory", "the description of cd");
        VirtualTerminalPopupSummary.OptionInfo baud = (VirtualTerminalPopupSummary.OptionInfo)items.First((CompletionItem item) => item.Text == "baud").Summary;
        Equal(baud.Description.Summary + " / " + baud.Description.Info, "baud <rate> / Set the baud rate, e.g. 115200", "baud");
        Check(items.All((CompletionItem item) => !((VirtualTerminalPopupSummary.OptionInfo)item.Summary).Description.Info.StartsWith("Built-in command:")), "every command has a description of its own");
        Equal(LocalCompletionItems.GetBuiltInCommands().Count(), 30, "the list can be made again");
    }

    private static void ExecutableChecks()
    {
        string root = Path.Combine(Path.GetTempPath(), "smartty-local-completion-" + Guid.NewGuid().ToString("N"));
        string first = Path.Combine(root, "first");
        string second = Path.Combine(root, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        string oldPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            File.WriteAllText(Path.Combine(first, "alpha.exe"), "");
            File.WriteAllText(Path.Combine(first, "Beta.sh"), "");
            File.WriteAllText(Path.Combine(first, "noextension"), "");
            File.WriteAllText(Path.Combine(second, "ALPHA.cmd"), "");
            File.WriteAllText(Path.Combine(second, "gamma.bat"), "");
            Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator.ToString(), new[] { first, "", Path.Combine(root, "missing"), second }));
            List<CompletionItem> all = LocalCompletionItems.GetLocalExecutables("").ToList();
            Equal(Join(all.Select((CompletionItem item) => item.Text).OrderBy((string name) => name, StringComparer.Ordinal)), "Beta|alpha|gamma|noextension", "the programs of the folders of PATH, once by name without the extension (the case of a name does not matter)");
            CompletionItem alpha = all.First((CompletionItem item) => item.Text == "alpha");
            Equal(alpha.InsertText, "alpha", "inserted as the name");
            Equal(alpha.Icon, VirtualTerminalPopupItemIcon.Command, "with the icon of a command");
            Equal(((VirtualTerminalPopupSummary.PathInfo)alpha.Summary).FullPath, Path.Combine(first, "alpha.exe"), "the summary has the path of the first one found");
            Equal(Join(LocalCompletionItems.GetLocalExecutables("BE").Select((CompletionItem item) => item.Text)), "Beta", "a filter matches the start of the name, whatever the case");
            Equal(LocalCompletionItems.GetLocalExecutables("zzz").Count(), 0, "nothing matches");
            Environment.SetEnvironmentVariable("PATH", null);
            Equal(LocalCompletionItems.GetLocalExecutables("").Count(), 0, "no PATH, no programs");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Main()
    {
        DirectoryPrefixChecks();
        WordRuleChecks();
        PathItemChecks();
        BuiltInChecks();
        ExecutableChecks();
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " local completion checks passed.");
    }
}
