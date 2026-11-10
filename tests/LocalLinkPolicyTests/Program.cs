using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using SmarTTY.Utilities;

// What a click on a path in a device terminal does (Utilities/LocalLinkPolicy.cs). The path is what the device printed and may point at
// any file of this machine; the shell would run a .js, a .py, a .bat or a .exe by its extension. Only addresses, folders and
// documents go to the shell, the rest is opened as text in Notepad.
// Run with: dotnet run --project tests/LocalLinkPolicyTests
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

    private static LocalLinkAction File_(string path)
    {
        return LocalLinkPolicy.Decide(path, isDirectory: false, isFile: true);
    }

    private static int Main()
    {
        // Addresses and folders.
        foreach (string url in new[] { "http://example.com/a.exe", "https://example.com/", "FTP://host/x", "HTTPS://host/a.js" })
        {
            Check(LocalLinkPolicy.Decide(url, false, false) == LocalLinkAction.OpenWithShell, "address " + url + " goes to the browser");
        }
        Check(LocalLinkPolicy.Decide("C:\\logs", true, false) == LocalLinkAction.OpenWithShell, "a folder is opened in Explorer");
        Check(LocalLinkPolicy.Decide("C:\\Windows\\System32\\calc.exe", false, false) == LocalLinkAction.Ignore, "a path that is no file here is left alone");
        Check(LocalLinkPolicy.Decide(null, false, false) == LocalLinkAction.Ignore && LocalLinkPolicy.Decide("", true, true) == LocalLinkAction.Ignore, "no path is left alone");
        Check(!LocalLinkPolicy.IsUrl("file:///C:/x.js") && !LocalLinkPolicy.IsUrl("javascript:alert(1)") && !LocalLinkPolicy.IsUrl("mailto:a@b") && !LocalLinkPolicy.IsUrl(null), "only http, https and ftp are addresses");

        // Documents and data: their own program.
        foreach (string shown in new[] { "C:\\a\\boot.log", "/tmp/notes.TXT", "C:\\a\\report.pdf", "C:\\a\\shot.PNG", "C:\\a\\conf.json", "C:\\a\\data.csv", "C:\\a\\x.yaml" })
        {
            Check(File_(shown) == LocalLinkAction.OpenWithShell, "document " + shown + " is opened by its program");
        }

        // What the shell would run, and what a browser runs scripts of: text.
        foreach (string run in new[]
        {
            "C:\\a\\x.js", "C:\\a\\x.JSE", "C:\\a\\x.vbs", "C:\\a\\x.vbe", "C:\\a\\x.wsf", "C:\\a\\x.hta", "C:\\a\\x.py", "C:\\a\\x.pyw", "C:\\a\\x.sh", "C:\\a\\x.pl",
            "C:\\a\\x.rb", "C:\\a\\x.php", "C:\\a\\x.lua", "C:\\a\\x.ps1", "C:\\a\\x.bat", "C:\\a\\x.cmd", "C:\\a\\x.exe", "C:\\a\\x.com", "C:\\a\\x.scr", "C:\\a\\x.pif",
            "C:\\a\\x.msi", "C:\\a\\x.msp", "C:\\a\\x.reg", "C:\\a\\x.cpl", "C:\\a\\x.jar", "C:\\a\\x.lnk", "C:\\a\\x.url", "C:\\a\\x.scf", "C:\\a\\x.application",
            "C:\\a\\x.appref-ms", "C:\\a\\x.gadget", "C:\\a\\x.msc", "C:\\a\\x.docm", "C:\\a\\x.xlsm", "C:\\a\\x.dll", "C:\\a\\x.html", "C:\\a\\x.htm", "C:\\a\\x.svg",
            "C:\\a\\x.desktop", "C:\\a\\x.c", "/etc/hosts", "C:\\a\\noextension", "C:\\a\\x."
        })
        {
            Check(File_(run) == LocalLinkAction.OpenInNotepad, run + " is shown as text, not run");
        }

        // Tricks with the name: the extension is the last one of the whole name, exactly.
        Check(File_("C:\\a.txt\\run.exe") == LocalLinkAction.OpenInNotepad, "a folder that looks like a document does not make a .exe one");
        Check(File_("C:\\a\\x.exe.txt") == LocalLinkAction.OpenWithShell && File_("C:\\a\\x.txt.exe") == LocalLinkAction.OpenInNotepad, "only the last extension counts");
        Check(File_("C:\\a\\x.txt ") == LocalLinkAction.OpenInNotepad && File_("C:\\a\\x.txt.") == LocalLinkAction.OpenInNotepad, "a blank or a dot after the extension is not a document");
        Check(File_("C:\\a\\x.txt:evil.exe") == LocalLinkAction.OpenInNotepad, "the name of an alternate data stream is judged by its own extension");
        Check(File_("C:\\a\\x.exe:note.txt") == LocalLinkAction.OpenWithShell, "a data stream that is a document is a document");
        Check(File_("C:\\a\\bad\0name.txt") != LocalLinkAction.Ignore, "a name with a character that no path holds does not fail");

        // The default list of extensions of the terminal links: what is opened by the shell is only what is shown, never what is run.
        string source = File.ReadAllText(typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(item => item.Key == "SettingsSource").Value);
        Match match = Regex.Match(source, "string DefaultAutoLinkExtensions = \"([^\"]+)\"");
        Check(match.Success, "the default list of link extensions is found in its source");
        List<string> defaults = match.Groups[1].Value.Split(';').Where(item => item.Length > 0).ToList();
        Check(defaults.Count > 50, "the default list is read (" + defaults.Count + " extensions)");
        List<string> byShell = defaults.Where(extension => File_("C:\\a\\x" + extension) == LocalLinkAction.OpenWithShell).ToList();
        string[] runnable = { ".js", ".py", ".sh", ".bash", ".pl", ".rb", ".php", ".lua", ".html", ".htm", ".svg", ".desktop", ".service", ".cs", ".ts", ".swift", ".go", ".java" };
        Check(runnable.All(extension => !byShell.Contains(extension, StringComparer.OrdinalIgnoreCase)), "no extension of the default list that a script host, an interpreter or a browser runs goes to the shell");
        Check(byShell.All(extension => new[] { ".txt", ".log", ".json", ".xml", ".md", ".csv", ".tsv", ".yml", ".yaml", ".toml", ".ini", ".cfg", ".conf", ".cnf", ".properties", ".diff", ".patch" }.Contains(extension)),
            "of the default list the shell opens only text and data files (" + string.Join(" ", byShell) + ")");
        Check(defaults.Where(extension => !byShell.Contains(extension)).All(extension => File_("C:\\a\\x" + extension) == LocalLinkAction.OpenInNotepad), "the rest of the default list is opened as text");

        // A path of this machine is opened only after a question: the device may be another Windows computer with its own C:\.
        Check(LocalLinkPolicy.ConfirmationText("https://example.com/", LocalLinkAction.OpenWithShell, false) == null, "an address is opened without a question");
        Check(LocalLinkPolicy.ConfirmationText("C:\\nothing\\here.log", LocalLinkAction.Ignore, false) == null && LocalLinkPolicy.ConfirmationText(null, LocalLinkAction.OpenWithShell, false) == null, "nothing to open, nothing to ask");
        string folder = LocalLinkPolicy.ConfirmationText("C:\\logs", LocalLinkAction.OpenWithShell, true);
        Check(folder != null && folder.Contains("C:\\logs") && folder.Contains("this computer") && folder.Contains("Explorer"), "a folder: the question names the path, this computer and Explorer");
        Check(LocalLinkPolicy.ConfirmationText("C:\\a\\report.pdf", LocalLinkAction.OpenWithShell, false).Contains("with its program"), "a document: opened with its program");
        Check(LocalLinkPolicy.ConfirmationText("C:\\a\\x.js", LocalLinkAction.OpenInNotepad, false).Contains("not run"), "a script: the question says it is shown as text and not run");

        Console.WriteLine();
        Console.WriteLine("All " + _passed + " checks passed.");
        return 0;
    }
}
