using System;
using System.IO;
using System.Linq;
using System.Threading;
using VisualTTY;

// Regression harness for VisualTTY/VcxsrvSetup.cs: choosing the VcXsrv installer from the GitHub release data and the
// fallback through the release page. Run with: dotnet run --project tests/VcxsrvSetupTests
// SMARTTY_VCXSRV_ONLINE=1 also asks GitHub for the latest release and downloads its installer (no installation).
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

    private static void Equal<T>(T expected, T actual, string name)
    {
        Check(Equals(expected, actual), name + " (expected " + (expected?.ToString() ?? "null") + ", got " + (actual?.ToString() ?? "null") + ")");
    }

    // Shaped like https://api.github.com/repos/marchaesen/vcxsrv/releases/latest (fields SmarTTY does not read trimmed).
    private const string ReleaseJson = @"{
  ""url"": ""https://api.github.com/repos/marchaesen/vcxsrv/releases/1"",
  ""tag_name"": ""21.1.16.1"",
  ""name"": ""21.1.16.1"",
  ""draft"": false,
  ""author"": { ""login"": ""marchaesen"", ""id"": 1 },
  ""assets"": [
    { ""name"": ""vcxsrv-64-debug.21.1.16.1.installer.exe"", ""size"": 90000000, ""digest"": null,
      ""browser_download_url"": ""https://github.com/marchaesen/vcxsrv/releases/download/21.1.16.1/vcxsrv-64-debug.21.1.16.1.installer.exe"" },
    { ""name"": ""vcxsrv-64-debug.21.1.16.1.installer.noadmin.exe"", ""size"": 90000000,
      ""browser_download_url"": ""https://github.com/marchaesen/vcxsrv/releases/download/21.1.16.1/vcxsrv-64-debug.21.1.16.1.installer.noadmin.exe"" },
    { ""name"": ""vcxsrv-64.21.1.16.1.installer.noadmin.exe"", ""size"": 45000000,
      ""browser_download_url"": ""https://github.com/marchaesen/vcxsrv/releases/download/21.1.16.1/vcxsrv-64.21.1.16.1.installer.noadmin.exe"" },
    { ""name"": ""vcxsrv-64.21.1.16.1.installer.exe"", ""size"": 45123456, ""content_type"": ""application/x-msdownload"",
      ""digest"": ""sha256:ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789"",
      ""browser_download_url"": ""https://github.com/marchaesen/vcxsrv/releases/download/21.1.16.1/vcxsrv-64.21.1.16.1.installer.exe"" }
  ]
}";

    private static int Main()
    {
        try
        {
            ReleaseSelection();
            ReleasePageFallback();
            Installation();
            if (Environment.GetEnvironmentVariable("SMARTTY_VCXSRV_ONLINE") == "1")
            {
                Online();
            }
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }
        Console.WriteLine();
        Console.WriteLine("VcxsrvSetupTests: all " + _passed + " checks passed.");
        return 0;
    }

    private static void ReleaseSelection()
    {
        VcxsrvSetup.Release release = VcxsrvSetup.ParseRelease(ReleaseJson);
        Check(release != null, "the release data is parsed");
        Equal("21.1.16.1", release.Version, "the version is the release tag");
        Equal("vcxsrv-64.21.1.16.1.installer.exe", release.FileName, "the regular 64-bit installer is chosen (not the debug or noadmin build)");
        Equal("https://github.com/marchaesen/vcxsrv/releases/download/21.1.16.1/vcxsrv-64.21.1.16.1.installer.exe", release.DownloadUrl, "the download URL of that installer is used");
        Equal(45123456L, release.Size, "the published size is kept for checking the download");
        Equal("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", release.Sha256, "the published SHA-256 is kept (lowercase, without the prefix)");

        VcxsrvSetup.Release noDigest = VcxsrvSetup.ParseRelease(ReleaseJson.Replace("\"digest\": \"sha256:", "\"digest\": \"md5:"));
        Check(noDigest != null && noDigest.Sha256 == null, "a digest of another kind is not used as SHA-256");
        Check(VcxsrvSetup.ParseRelease("{\"tag_name\": \"1.0\", \"assets\": []}") == null, "a release without an installer gives nothing");
        Check(VcxsrvSetup.ParseRelease("{\"tag_name\": \"1.0\"}") == null, "a release without assets gives nothing");

        Check(VcxsrvSetup.IsInstaller("vcxsrv-64.1.20.14.0.installer.exe"), "older installer names are recognized");
        Check(VcxsrvSetup.IsInstaller("VcXsrv-64.21.1.16.1.Installer.EXE"), "installer names are compared without case");
        Check(!VcxsrvSetup.IsInstaller("vcxsrv-64-debug.21.1.16.1.installer.exe"), "the debug build is not chosen");
        Check(!VcxsrvSetup.IsInstaller("vcxsrv-64.21.1.16.1.installer.noadmin.exe"), "the noadmin build is not chosen");
        Check(!VcxsrvSetup.IsInstaller("..\\vcxsrv-64.21.1.16.1.installer.exe"), "a name with a path is not accepted");
        Check(!VcxsrvSetup.IsInstaller(null), "a missing name is not accepted");
    }

    private static void ReleasePageFallback()
    {
        Equal("21.1.16.1", VcxsrvSetup.VersionFromReleaseUrl("https://github.com/marchaesen/vcxsrv/releases/tag/21.1.16.1"), "the version is read from the release page redirect");
        Equal(null, VcxsrvSetup.VersionFromReleaseUrl("https://github.com/marchaesen/vcxsrv/releases"), "a redirect without a tag gives no version");
        Equal(null, VcxsrvSetup.VersionFromReleaseUrl("https://github.com/marchaesen/vcxsrv/releases/tag/..%2F..%2Fevil"), "a tag that is not a version number is ignored");
        Equal(null, VcxsrvSetup.VersionFromReleaseUrl(null), "no redirect gives no version");
    }

    private static void Installation()
    {
        Check(VcxsrvSetup.CandidateDirectories().Any((string directory) => directory == VcxsrvSetup.DefaultInstallDirectory), "C:\\Program Files\\VcXsrv is searched");
        Check(VcxsrvSetup.DescribeInstallation(null).Contains(VcxsrvSetup.DefaultInstallDirectory), "the message names the folder that was searched");
        Check(VcxsrvSetup.DescribeInstallation("C:\\Program Files\\VcXsrv\\vcxsrv.exe").Contains("is installed"), "the message names the found executable");
        if (!OperatingSystem.IsWindows())
        {
            Equal(null, VcxsrvSetup.FindExecutable(), "no VcXsrv is found outside Windows (the registry lookup does not throw)");
        }

        // Folders from the registry: a quoted uninstaller with arguments, an icon with its index, a bare path.
        Equal("C:\\Program Files\\VcXsrv", VcxsrvSetup.DirectoryOfCommand("\"C:\\Program Files\\VcXsrv\\uninstall.exe\" /S"), "a quoted uninstaller with arguments gives its folder");
        Equal("C:\\Program Files\\VcXsrv", VcxsrvSetup.DirectoryOfCommand("C:\\Program Files\\VcXsrv\\vcxsrv.exe,0"), "an icon with its index gives the folder of the program");
        Equal("C:\\Program Files\\VcXsrv", VcxsrvSetup.DirectoryOfCommand("C:\\Program Files\\VcXsrv\\uninstall.exe /S"), "an unquoted uninstaller with arguments gives its folder");
        Equal(null, VcxsrvSetup.DirectoryOfCommand("  "), "an empty command gives no folder");

        // A copy next to the program counts as installed: nothing is downloaded (the offline run proves it).
        string folder = Path.Combine(Path.GetDirectoryName(typeof(VcxsrvSetup).Assembly.Location), "vcxsrv");
        string fake = Path.Combine(folder, VcxsrvSetup.ExecutableName);
        bool createdFolder = !Directory.Exists(folder);
        Directory.CreateDirectory(folder);
        File.WriteAllText(fake, "");
        try
        {
            string step = null;
            Equal(fake, VcxsrvSetup.DownloadAndInstall(text => step = text, CancellationToken.None), "an installed VcXsrv is returned without a download");
            Check(step != null && step.Contains("is installed"), "the progress text says that VcXsrv is installed");
        }
        finally
        {
            File.Delete(fake);
            if (createdFolder)
            {
                Directory.Delete(folder);
            }
        }
    }

    private static void Online()
    {
        VcxsrvSetup.Release release = VcxsrvSetup.FindLatestRelease();
        Console.WriteLine("     latest VcXsrv: " + release.Version + " " + release.DownloadUrl);
        Check(VcxsrvSetup.IsInstaller(release.FileName), "GitHub names a 64-bit installer of the latest release");
        string path = VcxsrvSetup.Download(release, null, CancellationToken.None);
        try
        {
            Check(File.Exists(path) && new FileInfo(path).Length > 1024 * 1024, "the installer was downloaded (" + new FileInfo(path).Length + " bytes)");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
