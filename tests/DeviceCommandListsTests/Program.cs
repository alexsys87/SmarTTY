using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VisualTTY.DeviceTerminal;

// Checks of the saved history and the command lists of a COM/TCP device (VisualTTY/DeviceTerminal/DeviceCommandLists.cs) on real files in a temporary folder: what is
// loaded and offered, what is saved and what is not, deleting, clearing, and the lists. The panel is a fake that keeps a log.
internal static class Program
{
    private sealed class FakeHost : IDeviceCommandHost
    {
        public readonly List<string> Log = new List<string>();
        public readonly List<string> TerminalHistory = new List<string>();
        public string Suggestions = "";

        int IDeviceCommandHost.TerminalHistoryCount => TerminalHistory.Count;

        void IDeviceCommandHost.ClearTerminalHistory()
        {
            TerminalHistory.Clear();
            Log.Add("clear");
        }

        void IDeviceCommandHost.AddToTerminalHistory(string command)
        {
            TerminalHistory.Add(command);
            Log.Add("add " + command);
        }

        void IDeviceCommandHost.RemoveFromTerminalHistory(IReadOnlyList<string> commands)
        {
            TerminalHistory.RemoveAll((string command) => commands.Contains(command));
            Log.Add("remove " + string.Join(",", commands));
        }

        void IDeviceCommandHost.SetSuggestions(IReadOnlyList<string> suggestions)
        {
            Suggestions = string.Join("|", suggestions);
            Log.Add("suggest " + Suggestions);
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        Console.WriteLine("PASS: " + name);
    }

    private static void Equal<T>(T actual, T expected, string name)
    {
        bool same = EqualityComparer<T>.Default.Equals(actual, expected);
        Check(same, name + (same ? "" : " (expected '" + expected + "', got '" + actual + "')"));
    }

    private static string NewFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "devcmd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string Items(CommandHistory history) => string.Join("|", history.Items);

    private static void ConfigureChecks()
    {
        string folder = NewFolder();
        DeviceLineInputStorage storage = new DeviceLineInputStorage(folder);
        CommandHistory saved = new CommandHistory();
        saved.Add("uname -a");
        saved.Add("uptime");
        storage.SaveHistory("tcp-a", saved);
        storage.SaveCommands("tcp-a", new[] { "reboot", "uptime" });
        storage.SaveCommands(null, new[] { "ls /", "reboot" });

        FakeHost host = new FakeHost();
        host.TerminalHistory.Add("stale");
        DeviceCommandLists lists = new DeviceCommandLists(host);
        Check(lists.Key == null && lists.SavedHistory.Count == 0, "nothing is loaded before Configure");
        lists.Configure("tcp-a", folder);
        Equal(lists.Key, "tcp-a", "the key of the device");
        Equal(Items(lists.SavedHistory), "uname -a|uptime", "the saved history is loaded");
        Equal(string.Join("|", lists.DeviceCommands), "reboot|uptime", "so is the list of the device");
        Equal(string.Join("|", lists.CommonCommands), "ls /|reboot", "and the list of all devices");
        Equal(string.Join(";", host.Log), "clear;add uname -a;add uptime;suggest uptime|uname -a|reboot|ls /", "the history of the terminal is cleared and filled from the saved one, oldest first, then Tab is given the newest history first and the lists after it");

        // A connection without a key has no history and no list of its own, but the list of all devices.
        host = new FakeHost();
        lists = new DeviceCommandLists(host);
        lists.Configure(null, folder);
        Check(lists.Key == null && lists.SavedHistory.Count == 0 && lists.DeviceCommands.Count == 0, "no key: no history, no list of the device");
        Equal(host.Suggestions, "ls /|reboot", "but the list of all devices is offered");
        Equal(host.Log[0], "clear", "and the history of the terminal is cleared");

        // Configure again: the old state is replaced.
        lists.Configure("tcp-a", folder);
        Equal(Items(lists.SavedHistory), "uname -a|uptime", "configuring again loads the device that is meant now");
        Directory.Delete(folder, true);
    }

    private static void SubmitChecks()
    {
        string folder = NewFolder();
        FakeHost host = new FakeHost();
        DeviceCommandLists lists = new DeviceCommandLists(host);
        lists.Configure("tcp-a", folder);
        DeviceLineInputStorage storage = new DeviceLineInputStorage(folder);
        host.Log.Clear();

        lists.OnSubmitted("uptime", "root@board:~# ");
        Equal(Items(lists.SavedHistory), "uptime", "a sent command is saved");
        Equal(Items(storage.LoadHistory("tcp-a")), "uptime", "to the file of the device");
        Equal(string.Join(";", host.Log), "suggest uptime", "and Tab is given it");

        host.Log.Clear();
        lists.OnSubmitted(" hidden", "root@board:~# ");
        lists.OnSubmitted("   ", "root@board:~# ");
        lists.OnSubmitted("", "root@board:~# ");
        lists.OnSubmitted(null, "root@board:~# ");
        lists.OnSubmitted("uptime", "root@board:~# ");
        Check(Items(lists.SavedHistory) == "uptime" && host.Log.Count == 0, "a leading blank, a blank line, nothing and a repeat of the last command are not saved, and Tab is not told");
        lists.OnSubmitted("ls", "root@board:~# ");
        lists.OnSubmitted("uptime", "root@board:~# ");
        Equal(Items(lists.SavedHistory), "ls|uptime", "an older command sent again goes to the end");
        lists.OnSubmitted("secret-pw", "Enter new password> ");
        lists.OnSubmitted("1234", "PIN: ");
        lists.OnSubmitted("x", "Введите пароль: ");
        Equal(Items(lists.SavedHistory), "ls|uptime", "what is typed at a prompt for a secret is not saved");
        Equal(Items(storage.LoadHistory("tcp-a")), "ls|uptime", "nor written to the file");

        // No key: nothing is saved.
        host = new FakeHost();
        lists = new DeviceCommandLists(host);
        lists.Configure(null, folder);
        host.Log.Clear();
        lists.OnSubmitted("reboot", "# ");
        lists.Remember("reboot");
        Check(lists.SavedHistory.Count == 0 && host.Log.Count == 0, "a connection without a key saves nothing");

        // Remember is the same without the prompt rule.
        lists = new DeviceCommandLists(new FakeHost());
        lists.Configure("tcp-b", folder);
        lists.Remember("Password: x");
        Equal(Items(lists.SavedHistory), "Password: x", "a line remembered by Remember is not looked at for a secret prompt");
        Directory.Delete(folder, true);
    }

    private static void DeleteAndClearChecks()
    {
        string folder = NewFolder();
        FakeHost host = new FakeHost();
        DeviceCommandLists lists = new DeviceCommandLists(host);
        lists.Configure("tcp-a", folder);
        DeviceLineInputStorage storage = new DeviceLineInputStorage(folder);
        foreach (string command in new[] { "a", "b", "c" })
        {
            lists.Remember(command);
            host.TerminalHistory.Add(command);
        }
        host.Log.Clear();

        lists.Delete(new[] { "b", "zzz" });
        Equal(string.Join(";", host.Log), "remove b,zzz;suggest c|a", "deleting takes the commands out of the terminal history and of Tab");
        Equal(Items(lists.SavedHistory), "a|c", "out of the saved history");
        Equal(Items(storage.LoadHistory("tcp-a")), "a|c", "and out of its file");
        Equal(string.Join("|", host.TerminalHistory), "a|c", "(the terminal history lost it)");

        File.Delete(storage.HistoryPath("tcp-a"));
        host.Log.Clear();
        lists.Delete(new[] { "zzz" });
        Check(!File.Exists(storage.HistoryPath("tcp-a")) && host.Log.Count == 2, "a command that is not in the saved history does not write the file again, and Tab is given the list anyway");

        lists = new DeviceCommandLists(host);
        lists.Configure(null, folder);
        host.Log.Clear();
        lists.Delete(new[] { "x" });
        Equal(string.Join(";", host.Log), "remove x;suggest ", "with no key it only tells the terminal (no list is saved in this folder)");

        // Clear.
        host = new FakeHost();
        lists = new DeviceCommandLists(host);
        lists.Configure("tcp-c", folder);
        Check(lists.HasNothingToClear, "nothing to clear with both histories empty");
        host.TerminalHistory.Add("only-in-terminal");
        Check(!lists.HasNothingToClear, "something to clear when the terminal holds a command");
        host.TerminalHistory.Clear();
        lists.Remember("saved");
        Check(!lists.HasNothingToClear, "something to clear when the saved history holds a command");
        host.TerminalHistory.Add("saved");
        host.Log.Clear();
        lists.ClearHistory();
        Check(lists.SavedHistory.Count == 0 && host.TerminalHistory.Count == 0, "clearing empties both histories");
        Check(!File.Exists(storage.HistoryPath("tcp-c")), "and deletes the file of the device");
        Equal(string.Join(";", host.Log), "clear;suggest ", "and Tab is given the lists only (none here)");

        lists = new DeviceCommandLists(host);
        lists.Configure(null, folder);
        host.TerminalHistory.Add("x");
        host.Log.Clear();
        lists.ClearHistory();
        Equal(string.Join(";", host.Log), "clear;suggest ", "with no key nothing is deleted from disk");
        Directory.Delete(folder, true);
    }

    private static void ListChecks()
    {
        string folder = NewFolder();
        FakeHost host = new FakeHost();
        DeviceCommandLists lists = new DeviceCommandLists(host);
        lists.Configure("tcp-a", folder);
        DeviceLineInputStorage storage = new DeviceLineInputStorage(folder);
        host.Log.Clear();

        Equal(lists.SaveList(true, new[] { "reboot", "# comment", "show version" }), true, "the list of the device is saved");
        Equal(string.Join("|", storage.LoadCommands("tcp-a")), "reboot|show version", "to its file, without the comment");
        Equal(host.Log.Count, 0, "saving does not tell Tab");
        lists.SetList(true, new List<string> { "reboot", "show version" });
        Equal(host.Suggestions, "reboot|show version", "the list of the device is what Tab offers");
        Equal(lists.SaveList(false, new[] { "ls /", "reboot" }), true, "the list of all devices is saved");
        Equal(string.Join("|", storage.LoadCommands(null)), "ls /|reboot", "to the shared file");
        lists.SetList(false, new List<string> { "ls /", "reboot" });
        Equal(host.Suggestions, "reboot|show version|ls /", "it comes after the list of the device, a repeated command keeps its first place");
        Equal(string.Join("|", lists.DeviceCommands), "reboot|show version", "the list of the device is kept");
        Equal(string.Join("|", lists.CommonCommands), "ls /|reboot", "and the shared one");

        // A list that cannot be written.
        string blocker = Path.Combine(folder, "blocker");
        File.WriteAllText(blocker, "x");
        DeviceCommandLists broken = new DeviceCommandLists(new FakeHost());
        broken.Configure("tcp-a", Path.Combine(blocker, "sub"));
        Equal(broken.SaveList(true, new[] { "a" }), false, "a list that cannot be written says so");
        Directory.Delete(folder, true);
    }

    private static int Main()
    {
        try
        {
            ConfigureChecks();
            SubmitChecks();
            DeleteAndClearChecks();
            ListChecks();
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            return 1;
        }
        return 0;
    }
}
