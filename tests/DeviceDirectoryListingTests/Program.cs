using System;
using System.Linq;
using VisualTTY.DeviceTerminal;

// Checks of the directory listing that the COM/TCP terminal asks the device's shell for (path completion).
internal static class Program
{
    private static int _failures;

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            _failures++;
            Console.WriteLine("FAIL: " + name);
        }
        else
        {
            Console.WriteLine("ok:   " + name);
        }
    }

    private static int Main()
    {
        string command = DeviceDirectoryListing.BuildCommand("/etc/", "7");
        Check(command.StartsWith(" ", StringComparison.Ordinal), "command starts with a space (kept out of the history)");
        Check(command.Contains("cd -- '/etc/'"), "directory is quoted");
        Check(!command.Contains(DeviceDirectoryListing.BeginMarker("7")), "echo of the command has no whole begin marker");
        Check(!command.Contains(DeviceDirectoryListing.EndMarker("7")), "echo of the command has no whole end marker");

        Check(DeviceDirectoryListing.QuoteDirectory("") == ".", "empty directory is the shell's directory");
        Check(DeviceDirectoryListing.QuoteDirectory("~/") == "~/", "home directory keeps the tilde");
        Check(DeviceDirectoryListing.QuoteDirectory("~/my dir/") == "~/'my dir/'", "tilde stays unquoted");
        Check(DeviceDirectoryListing.QuoteDirectory("it's/") == "'it'\\''s/'", "single quote is escaped");

        // A typical answer of BusyBox ash: the echo, the listing, the prompt and a cursor position query.
        DeviceDirectoryCapture capture = new DeviceDirectoryCapture("7");
        string shown = capture.Feed(command + "\r\n" + "__SMARTTY_LS_B7\r\n" + "bin/\r\n\u001b[1;34mlib/\u001b[0m\r\n.profile\r\n");
        Check(shown.Length == 0, "echo and listing are not shown");
        Check(!capture.HasListing, "listing is not complete before the end marker");
        shown = capture.Feed("passwd\r\n__SMARTTY_LS_E7\r\nroot@OpenWrt:~# \u001b[6n");
        Check(shown.Length == 0, "prompt after the listing is not shown");
        Check(capture.IsDone, "capture ends with the prompt");
        Check(capture.Entries.Count == 4, "four names listed");
        Check(capture.Entries[0].Name == "bin" && capture.Entries[0].IsDirectory, "directory recognised");
        Check(capture.Entries[1].Name == "lib" && capture.Entries[1].IsDirectory, "colours are stripped");
        Check(capture.Entries[2].Name == ".profile" && !capture.Entries[2].IsDirectory, "hidden file listed");
        Check(capture.Feed("hello") == "hello", "text after the capture is shown");

        // Chunks split anywhere, the prompt arriving separately.
        capture = new DeviceDirectoryCapture("8");
        string answer = "__SMARTTY_LS_B8\r\nusr/\r\n__SMARTTY_LS_E8\r\n";
        foreach (char ch in answer)
        {
            capture.Feed(ch.ToString());
        }
        Check(capture.HasListing && !capture.IsDone, "listing complete, prompt still awaited");
        Check(capture.Feed("$ ").Length == 0 && capture.IsDone, "prompt ends the capture");
        Check(capture.Entries.Single().Name == "usr", "name from split chunks");

        // A device that does not answer like a shell: its text is given back.
        capture = new DeviceDirectoryCapture("9");
        capture.Feed("Unknown command 'printf'\r\n=> ");
        Check(capture.Abort() == "Unknown command 'printf'\r\n=> ", "text is given back on abort");

        // The end marker of another listing does not end this one.
        capture = new DeviceDirectoryCapture("10");
        capture.Feed("__SMARTTY_LS_B10\r\n__SMARTTY_LS_E1\r\n");
        Check(!capture.HasListing && capture.Entries.Count == 1, "marker of another token is a name");

        Console.WriteLine(_failures == 0 ? "All checks passed." : _failures + " check(s) failed.");
        return _failures == 0 ? 0 : 1;
    }
}
