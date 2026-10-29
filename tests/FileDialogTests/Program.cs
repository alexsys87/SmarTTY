using SmarTTY.IO;
using SmarTTY.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

// Regression harness for the themed file dialog that replaced the WinForms OpenFileDialog/SaveFileDialog. The
// controls live in Controls/ and VisualTTY/, which the Mono test harness compiles and which cannot reference the WPF
// windows, so they ask through the ThemedFileDialog facade; App wires that facade to GUI/LocalFileDialogWindow.
// This harness guards two things: the rules the window is built on (the filter strings and the folder listing, which
// have no UI dependency) and the wiring (the migrated call sites, the facade and the adapter).
// Run with: dotnet run --project tests/FileDialogTests
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

    private static string FindRepositoryRoot()
    {
        string root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "SmarTTY.csproj")))
        {
            root = Path.GetDirectoryName(root);
        }
        return root;
    }

    private static int Main()
    {
        try
        {
            TestFilterParsing();
            TestFilterMatching();
            TestListing();
            TestMigratedCallSites();
            TestFacade();
            TestAdapterAndInstall();
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }
        Console.WriteLine($"All {_passed} file dialog checks passed.");
        return 0;
    }

    private static void TestFilterParsing()
    {
        List<FileDialogFilterEntry> entries = FileDialogFilter.Parse("All files (*.*)|*.*|Text (*.txt;*.log)|*.txt;*.log");
        Equal(entries.Count, 2, "two filter entries are parsed");
        Equal(entries[0].Description, "All files (*.*)", "the first description is kept");
        Equal(entries[0].Patterns.Count, 1, "the first entry has one pattern");
        Equal(entries[0].Patterns[0], "*.*", "the first pattern is kept");
        Equal(entries[1].Description, "Text (*.txt;*.log)", "the second description is kept");
        Equal(entries[1].Patterns.Count, 2, "the second entry splits its patterns on ';'");
        Equal(entries[1].Patterns[1], "*.log", "the second pattern is kept");

        // The strings the application actually uses must parse without a trailing empty entry.
        Equal(FileDialogFilter.Parse("All files|*.*").Count, 1, "an old-style single filter parses to one entry");
        Equal(FileDialogFilter.Parse("OpenSSH keys|id_dsa;id_rsa|All files|*.*").Count, 2,
            "the OpenSSH-key filter parses to two entries (a bare pattern field is one entry)");
        Equal(FileDialogFilter.Parse("").Count, 0, "an empty filter has no entries");
        Equal(FileDialogFilter.Parse("Only a description").Count, 0, "a filter without a pattern field has no entries");

        FileDialogFilterEntry at = FileDialogFilter.EntryAt(entries, 2);
        Check(at != null && at.Description == "Text (*.txt;*.log)", "the 1-based filter index selects the entry");
        Check(FileDialogFilter.EntryAt(entries, 0) == null, "an index below one selects nothing");
        Check(FileDialogFilter.EntryAt(entries, 3) == null, "an index past the end selects nothing");
    }

    private static void TestFilterMatching()
    {
        FileDialogFilterEntry text = new FileDialogFilterEntry("Text", new string[] { "*.txt", "*.log" });
        Check(FileDialogFilter.MatchesEntry(text, "notes.txt"), "a *.txt name matches the text filter");
        Check(FileDialogFilter.MatchesEntry(text, "server.LOG"), "matching ignores case");
        Check(!FileDialogFilter.MatchesEntry(text, "image.png"), "a name outside the patterns does not match");
        Check(FileDialogFilter.MatchesEntry(new FileDialogFilterEntry("All", new string[] { "*.*" }), "anything.bin"),
            "*.* matches everything");
        Check(FileDialogFilter.MatchesEntry(new FileDialogFilterEntry("All", new string[] { "*" }), "anything.bin"),
            "* matches everything");
        Check(FileDialogFilter.MatchesEntry(null, "anything.bin"), "a null entry (no filter) matches everything");
        Check(FileDialogFilter.MatchesEntry(new FileDialogFilterEntry("Empty", new string[0]), "anything.bin"),
            "an entry without patterns matches everything");
        Check(FileDialogFilter.Matches("id_?sa", "id_rsa"), "? matches one character");
        Check(!FileDialogFilter.Matches("id_?sa", "id_rsaa"), "? does not match two characters");
        Check(FileDialogFilter.Matches("*.tar.gz", "backup.tar.gz"), "a multi-dot pattern matches");
    }

    private static void TestListing()
    {
        string root = FindRepositoryRoot();
        if (root == null)
        {
            Console.WriteLine("SKIP: repository root not found, the folder listing is not exercised");
            return;
        }

        string directory = Path.Combine(Path.GetTempPath(), "smartty-file-dialog-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "subfolder"));
        File.WriteAllText(Path.Combine(directory, "alpha.txt"), "hello");
        File.WriteAllText(Path.Combine(directory, "beta.log"), "world");
        try
        {
            FileDialogFilterEntry all = new FileDialogFilterEntry("All files (*.*)", new string[] { "*.*" });
            List<LocalFileEntry> entries = LocalFileListing.List(directory, all, includeFiles: true);
            Equal(entries.Count, 3, "the folder lists its subfolder and both files");
            Check(entries[0].IsDirectory && entries[0].Name == "subfolder", "directories come first");
            Check(entries[1].Name == "alpha.txt" && entries[2].Name == "beta.log", "files are ordered by name");
            Check(entries[1].SizeText.Length != 0, "a file shows a size");
            Equal(entries[0].SizeText, "", "a folder shows no size");

            FileDialogFilterEntry textOnly = new FileDialogFilterEntry("Text", new string[] { "*.txt" });
            List<LocalFileEntry> filtered = LocalFileListing.List(directory, textOnly, includeFiles: true);
            Equal(filtered.Count, 2, "the *.txt filter keeps the subfolder and only the .txt file");
            Check(filtered[0].IsDirectory, "the subfolder is still listed under a file filter");

            List<LocalFileEntry> foldersOnly = LocalFileListing.List(directory, all, includeFiles: false);
            Equal(foldersOnly.Count, 1, "without files only the subfolder is listed");

            Check(LocalFileListing.DirectoryExists(directory), "an existing folder is recognized");
            Check(!LocalFileListing.DirectoryExists(Path.Combine(directory, "nope")), "a missing folder is not");

            string combined = LocalFileListing.Combine(directory, "alpha.txt");
            Equal(combined, Path.Combine(directory, "alpha.txt"), "a name is combined with the current folder");
            Check(LocalFileListing.Combine(directory, "") == null, "an empty name yields no path");

            List<LocalFileEntry> missing = LocalFileListing.List(Path.Combine(directory, "nope"), all, includeFiles: true);
            Equal(missing.Count, 0, "a missing folder lists nothing");
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static void TestMigratedCallSites()
    {
        string root = FindRepositoryRoot();
        if (root == null)
        {
            return;
        }

        string[] migrated = new string[]
        {
            Path.Combine(root, "Controls", "DeviceTerminalControl.cs"),
            Path.Combine(root, "Controls", "DeviceTerminalControl.ZModem.cs"),
            Path.Combine(root, "Controls", "DeviceTerminalControl.Kermit.cs"),
            Path.Combine(root, "Controls", "DeviceTerminalControl.YModem.cs"),
            Path.Combine(root, "Controls", "DeviceTerminalControl.Designer.cs"),
            Path.Combine(root, "VisualTTY", "Backend", "NewConnectionController.cs"),
            Path.Combine(root, "VisualTTY", "XServerLaunchController.cs"),
            Path.Combine(root, "GUI", "Backend", "MainWindowController.cs"),
            Path.Combine(root, "GUI", "PublicKeyManagerWindow.xaml.cs"),
            Path.Combine(root, "GUI", "TftpFirmwareWindow.xaml.cs"),
            Path.Combine(root, "GUI", "GzipBase64TransferWindow.xaml.cs"),
            Path.Combine(root, "GUI", "SCPUploadWindow.xaml.cs"),
            Path.Combine(root, "GUI", "SettingsArchiveDialogs.cs"),
            Path.Combine(root, "GUI", "ConnectionSelectionWindow.xaml.cs"),
            Path.Combine(root, "GUI", "RawConnectionSettingsWindow.xaml.cs")
        };
        foreach (string path in migrated)
        {
            string code = File.ReadAllText(path);
            string name = Path.GetFileName(path);
            Check(!Regex.IsMatch(code, @"\b(OpenFileDialog|SaveFileDialog)\b"), $"the native file dialogs are gone from {name}");
        }

        // These files pulled in Microsoft.Win32 only for the file dialogs.
        string[] win32Dropped = new string[]
        {
            Path.Combine(root, "VisualTTY", "Backend", "NewConnectionController.cs"),
            Path.Combine(root, "VisualTTY", "XServerLaunchController.cs")
        };
        foreach (string path in win32Dropped)
        {
            string code = File.ReadAllText(path);
            Check(!Regex.IsMatch(code, @"\bMicrosoft\.Win32\b"), $"the Microsoft.Win32 import is gone from {Path.GetFileName(path)}");
        }

        string device = File.ReadAllText(Path.Combine(root, "Controls", "DeviceTerminalControl.cs"));
        Check(device.Contains("ThemedFileDialog.OpenFile"), "the device terminal opens files through the themed dialog");
        Check(device.Contains("ThemedFileDialog.SaveFile"), "the device terminal saves files through the themed dialog");
        Check(File.ReadAllText(Path.Combine(root, "Controls", "DeviceTerminalControl.ZModem.cs")).Contains("ThemedFileDialog.OpenFiles"),
            "the ZMODEM send path uses the themed multiselect dialog");

        // The WPF callers that own a Window use the LocalFileDialogWindow helpers directly.
        string[] wpfCallers = new string[]
        {
            Path.Combine(root, "GUI", "Backend", "MainWindowController.cs"),
            Path.Combine(root, "GUI", "PublicKeyManagerWindow.xaml.cs"),
            Path.Combine(root, "GUI", "TftpFirmwareWindow.xaml.cs"),
            Path.Combine(root, "GUI", "GzipBase64TransferWindow.xaml.cs"),
            Path.Combine(root, "GUI", "SCPUploadWindow.xaml.cs"),
            Path.Combine(root, "GUI", "SettingsArchiveDialogs.cs"),
            Path.Combine(root, "GUI", "ConnectionSelectionWindow.xaml.cs"),
            Path.Combine(root, "GUI", "RawConnectionSettingsWindow.xaml.cs")
        };
        foreach (string path in wpfCallers)
        {
            Check(File.ReadAllText(path).Contains("LocalFileDialogWindow."),
                $"{Path.GetFileName(path)} opens files through the themed dialog window");
        }
    }

    private static void TestFacade()
    {
        string root = FindRepositoryRoot();
        if (root == null)
        {
            return;
        }

        string facadePath = Path.Combine(root, "Controls", "ThemedFileDialog.cs");
        Check(File.Exists(facadePath), "the ThemedFileDialog facade exists");
        if (!File.Exists(facadePath))
        {
            return;
        }
        string facade = File.ReadAllText(facadePath);
        Check(facade.Contains("ShowImpl"), "the facade exposes the injected ShowImpl hook");
        Check(facade.Contains("static string OpenFile"), "the facade offers an OpenFile entry point");
        Check(facade.Contains("static string[] OpenFiles"), "the facade offers a multiselect OpenFiles entry point");
        Check(facade.Contains("static string SaveFile"), "the facade offers a SaveFile entry point");
        Check(facade.Contains("SaveFileEx"), "the facade reports the chosen filter index for the log-format callers");
        Check(Regex.IsMatch(facade, @"\bOpenFileDialog\b") && Regex.IsMatch(facade, @"\bSaveFileDialog\b"),
            "the facade falls back to the native dialogs when nothing is injected");
    }

    private static void TestAdapterAndInstall()
    {
        string root = FindRepositoryRoot();
        if (root == null)
        {
            return;
        }

        string adapterPath = Path.Combine(root, "GUI", "ThemedFileDialogAdapter.cs");
        Check(File.Exists(adapterPath), "the adapter that points the facade at the themed dialog exists");
        if (File.Exists(adapterPath))
        {
            string adapter = File.ReadAllText(adapterPath);
            Check(adapter.Contains("ThemedFileDialog.ShowImpl"), "the adapter installs the facade hook");
            Check(adapter.Contains("LocalFileDialogWindow.Browse"), "the adapter opens the themed local file dialog");
        }

        string windowPath = Path.Combine(root, "GUI", "LocalFileDialogWindow.xaml.cs");
        Check(File.Exists(windowPath), "the themed file dialog window exists");
        Check(File.Exists(Path.Combine(root, "GUI", "LocalFileDialogWindow.xaml")), "the themed file dialog markup exists");

        string app = File.ReadAllText(Path.Combine(root, "App.xaml.cs"));
        Check(app.Contains("ThemedFileDialogAdapter.Install()"), "App wires the file dialog adapter on startup");
    }
}
