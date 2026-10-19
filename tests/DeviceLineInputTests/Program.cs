using System;
using System.IO;
using System.Linq;
using VisualTTY.DeviceTerminal;

// Regression harness for the saved command history and the user command lists of COM/TCP terminals.
// Run with: dotnet run --project tests/DeviceLineInputTests
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

    private static bool Same(System.Collections.Generic.IEnumerable<string> actual, params string[] expected)
    {
        return actual.SequenceEqual(expected);
    }

    private static int Main()
    {
        // --- history ---
        CommandHistory history = new CommandHistory(3);
        Check(history.Add("help"), "history: first command recorded");
        Check(!history.Add("help"), "history: immediate repeat is not recorded again");
        Check(!history.Add("   "), "history: blank line ignored");
        Check(!history.Add(""), "history: empty line ignored");
        Check(!history.Add(null), "history: null ignored");
        Check(!history.Add(" secret"), "history: leading space means do not record");
        history.Add("ls");
        history.Add("help");
        Check(Same(history.Items, "ls", "help"), "history: older duplicate moves to the end");
        history.Add("a");
        history.Add("b");
        Check(Same(history.Items, "help", "a", "b"), "history: capacity drops the oldest");
        Check(history.Add("trailing   ") && history.Items.Last() == "trailing", "history: trailing blanks trimmed");

        CommandHistory loaded = CommandHistory.Parse(history.Serialize(), 3);
        Check(Same(loaded.Items, history.Items.ToArray()), "history: serialize/parse round trip");
        Check(CommandHistory.Parse("one\r\ntwo\r\n\r\n one\r\nthree", 10).Items.Count == 3, "history: parse handles CRLF, blanks and duplicates");
        history.Clear();
        Check(history.Count == 0, "history: clear");

        // --- command list file ---
        string listText = "# boot commands\r\nprintenv\r\n\r\n  boot  \r\nprintenv\r\n#disabled\r\nreset";
        Check(Same(CommandListFile.Parse(listText), "printenv", "boot", "reset"), "commands: comments, blanks, trimming and duplicates");
        Check(Same(CommandListFile.Parse(CommandListFile.Format(new[] { "a", "b", "a", "# c", "d" })), "a", "b", "d"), "commands: format/parse round trip");
        Check(CommandListFile.Parse(null).Count == 0, "commands: null text");
        Check(Same(CommandListFile.BuildSuggestions(new[] { "old", "recent" }, new[] { "dev", "recent" }, new[] { "common", "dev" }),
            "recent", "old", "dev", "common"), "suggestions: history newest first, then device, then common, no duplicates");
        Check(CommandListFile.BuildSuggestions(null, null, null).Count == 0, "suggestions: nulls are fine");

        // --- line splitting ---
        Check(Same(LineInputText.SplitLines("a\r\nb\nc\rd"), "a", "b", "c", "d"), "split: mixed line breaks");
        Check(LineInputText.SplitLines("a\n").Count == 1 && LineInputText.SplitLines("").Count == 0 && LineInputText.SplitLines(null).Count == 0, "split: trailing break, empty, null");

        // --- prompts that ask for a secret ---
        Check(LineInputText.LooksLikeSecretPrompt("Password: "), "secret prompt: Password:");
        Check(LineInputText.LooksLikeSecretPrompt("root@host's PASSWORD:"), "secret prompt: any case");
        Check(LineInputText.LooksLikeSecretPrompt("Enter passphrase for key"), "secret prompt: passphrase");
        Check(LineInputText.LooksLikeSecretPrompt("Пароль: "), "secret prompt: Russian");
        Check(LineInputText.LooksLikeSecretPrompt("PIN: "), "secret prompt: PIN");
        Check(!LineInputText.LooksLikeSecretPrompt("login: ") && !LineInputText.LooksLikeSecretPrompt("> ") && !LineInputText.LooksLikeSecretPrompt("=> ") && !LineInputText.LooksLikeSecretPrompt(null) && !LineInputText.LooksLikeSecretPrompt(""), "secret prompt: ordinary prompts are fine");

        // --- keys ---
        Check(DeviceLineInputStorage.MakeSerialKey("com3") == DeviceLineInputStorage.MakeSerialKey(" COM3 "), "key: serial port name is case and space insensitive");
        Check(DeviceLineInputStorage.MakeSerialKey("COM3") == DeviceLineInputStorage.MakeSerialKey(@"\\.\COM3"), "key: device path prefix is the same port");
        Check(DeviceLineInputStorage.MakeSerialKey("COM3") == "serial_COM3", "key: serial format is file-name safe");
        Check(DeviceLineInputStorage.MakeTcpKey("Router.Local", 23) == "tcp-router.local-23", "key: tcp format");
        Check(DeviceLineInputStorage.MakeTcpKey("fe80::1", 23) == "tcp-fe80__1-23", "key: IPv6 host is file-name safe");
        Check(DeviceLineInputStorage.MakeSerialKey("  ") == null && DeviceLineInputStorage.MakeTcpKey("", 1) == null, "key: unknown device has no key");
        string longKey = DeviceLineInputStorage.MakeSerialKey(new string('X', 300));
        Check(longKey.Length <= 100 && !longKey.Any(c => Path.GetInvalidFileNameChars().Contains(c)), "key: long names are cut");

        // --- storage ---
        string root = Path.Combine(Path.GetTempPath(), "smartty-lineinput-" + Guid.NewGuid().ToString("N"));
        try
        {
            DeviceLineInputStorage storage = new DeviceLineInputStorage(root);
            string key = DeviceLineInputStorage.MakeSerialKey("COM7");
            Check(storage.LoadHistory(key).Count == 0, "storage: missing history is empty");
            Check(storage.LoadCommands(key).Count == 0 && storage.LoadCommands(null).Count == 0, "storage: missing command lists are empty");
            CommandHistory saved = new CommandHistory();
            saved.Add("Привет мир");
            saved.Add("help");
            Check(storage.SaveHistory(key, saved), "storage: history saved");
            Check(Same(storage.LoadHistory(key).Items, "Привет мир", "help"), "storage: history round trip with Cyrillic");
            Check(Same(storage.LoadHistory(DeviceLineInputStorage.MakeSerialKey("COM8")).Items), "storage: histories are per device");
            Check(storage.SaveHistory(key, saved), "storage: history can be overwritten");
            Check(!File.Exists(storage.HistoryPath(key) + ".tmp"), "storage: no temporary file left behind");
            Check(storage.SaveCommands(key, new[] { "reset", "# note", "boot" }) && storage.SaveCommands(null, new[] { "help" }), "storage: command lists saved");
            Check(Same(storage.LoadCommands(key), "reset", "boot"), "storage: device command list round trip");
            Check(Same(storage.LoadCommands(null), "help"), "storage: common command list is separate");
            Check(storage.DeleteHistory(key) && storage.LoadHistory(key).Count == 0, "storage: history deleted");
            Check(storage.DeleteHistory(key), "storage: deleting a missing history is fine");
            Check(!storage.SaveHistory(null, saved), "storage: no key, nothing saved");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(_passed + " checks passed");
        return 0;
    }
}
