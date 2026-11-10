using System;
using SmarTTY.Utilities;

// What a click in a device terminal (COM, Telnet, Raw TCP) opens (Utilities/LocalLinkPolicy.cs). The text is what the device printed,
// so its paths are paths of the device: "cd /tmp" or the panel of mc showed /tmp as a link that opened C:\tmp of this computer in
// Explorer. Only addresses are links.
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

    private static int Main()
    {
        foreach (string url in new[] { "http://example.com/a.exe", "https://example.com/", "FTP://host/x", "HTTPS://host/a.js", "http://192.168.1.1:8080/cgi-bin/luci" })
        {
            Check(LocalLinkPolicy.IsUrl(url), "address " + url + " is a link (the browser opens it)");
        }
        foreach (string path in new[]
        {
            "/tmp", "/", "/etc/passwd", "/var/log/syslog", "/home/user/notes.txt", "~/x.log",
            @"C:\Users", @"C:\Windows\System32\calc.exe", "C:/Windows/x.log", @"d:\build\out.txt", "C:", "C:foo.txt",
            @"\Windows\x", @"\\host\share\x.txt", "//host/share/x.txt", @"\\?\C:\x", @"\\.\COM3",
            "relative.txt", "build.log", "./run.sh", "../x.log",
            "file:///C:/x.js", "file://host/share", "javascript:alert(1)", "mailto:a@b", "ms-settings:", "search-ms:query=x", "", null,
        })
        {
            Check(!LocalLinkPolicy.IsUrl(path), "'" + (path ?? "null") + "' is no link: it names something of the device, or of this computer");
        }

        Console.WriteLine();
        Console.WriteLine("All " + _passed + " checks passed.");
        return 0;
    }
}
