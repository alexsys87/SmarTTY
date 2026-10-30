using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace VisualTTY;

/// <summary>
/// Locates an installed VcXsrv (the X server SmarTTY starts for forwarded X11 programs) and downloads and runs the installer
/// of its latest release. VcXsrv is not shipped with SmarTTY; its releases are published at github.com/marchaesen/vcxsrv.
/// </summary>
public static class VcxsrvSetup
{
    public sealed class Release
    {
        public string Version;

        public string FileName;

        public string DownloadUrl;

        /// <summary>Size of the installer in bytes; 0 if unknown.</summary>
        public long Size;

        /// <summary>SHA-256 of the installer (lowercase hex) as published by GitHub; null if unknown.</summary>
        public string Sha256;
    }

    [DataContract]
    private sealed class GitHubRelease
    {
        [DataMember(Name = "tag_name")]
        public string TagName;

        [DataMember(Name = "assets")]
        public List<GitHubAsset> Assets;
    }

    [DataContract]
    private sealed class GitHubAsset
    {
        [DataMember(Name = "name")]
        public string Name;

        [DataMember(Name = "size")]
        public long Size;

        [DataMember(Name = "browser_download_url")]
        public string DownloadUrl;

        [DataMember(Name = "digest")]
        public string Digest;
    }

    /// <summary>Where the VcXsrv installer puts the X server by default.</summary>
    public const string DefaultInstallDirectory = "C:\\Program Files\\VcXsrv";

    public const string ExecutableName = "vcxsrv.exe";

    public const string ReleasesPage = "https://github.com/marchaesen/vcxsrv/releases/latest";

    private const string LatestReleaseApi = "https://api.github.com/repos/marchaesen/vcxsrv/releases/latest";

    private const string DownloadUrlFormat = "https://github.com/marchaesen/vcxsrv/releases/download/{0}/vcxsrv-64.{0}.installer.exe";

    private const string UserAgent = "SmarTTY";

    // The regular 64-bit installer: "vcxsrv-64.21.1.16.1.installer.exe" (not the "-debug" build nor the ".noadmin" one).
    private static readonly Regex InstallerName = new Regex("^vcxsrv-64\\.[0-9][0-9.]*\\.installer\\.exe$", RegexOptions.IgnoreCase);

    private static readonly Regex VersionText = new Regex("^[0-9][0-9.]*$");

    // ERROR_CANCELLED: the user declined the elevation prompt.
    private const int ErrorCancelled = 1223;

    /// <summary>
    /// Directories that may hold vcxsrv.exe, most specific first: a copy next to SmarTTY.exe, the folder recorded by the
    /// installer and the standard installation folders (SmarTTY is a 32-bit process, so "Program Files" of the 64-bit
    /// system comes from ProgramW6432).
    /// </summary>
    public static IEnumerable<string> CandidateDirectories()
    {
        List<string> directories = new List<string>();
        try
        {
            directories.Add(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "vcxsrv"));
        }
        catch
        {
        }
        directories.AddRange(RegisteredDirectories());
        foreach (string variable in new[] { "ProgramW6432", "ProgramFiles", "ProgramFiles(x86)" })
        {
            string programFiles = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrEmpty(programFiles))
            {
                directories.Add(Path.Combine(programFiles, "VcXsrv"));
            }
        }
        directories.Add(DefaultInstallDirectory);
        string localPrograms = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localPrograms))
        {
            directories.Add(Path.Combine(localPrograms, "Programs", "VcXsrv"));
        }
        return directories.Where((string directory) => !string.IsNullOrEmpty(directory)).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    // The installer records its folder as HKLM\SOFTWARE\VcXsrv\Install_Dir and in its uninstall entry.
    private static IEnumerable<string> RegisteredDirectories()
    {
        List<string> directories = new List<string>();
        foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using RegistryKey root = RegistryKey.OpenBaseKey(hive, view);
                    using (RegistryKey key = root.OpenSubKey("SOFTWARE\\VcXsrv"))
                    {
                        AddDirectory(directories, key?.GetValue("Install_Dir") as string);
                    }
                    using (RegistryKey key = root.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\VcXsrv"))
                    {
                        AddDirectory(directories, key?.GetValue("InstallLocation") as string);
                        string uninstaller = (key?.GetValue("UninstallString") as string)?.Trim().Trim('"');
                        if (!string.IsNullOrEmpty(uninstaller))
                        {
                            AddDirectory(directories, Path.GetDirectoryName(uninstaller));
                        }
                    }
                }
                catch
                {
                }
            }
        }
        return directories;
    }

    private static void AddDirectory(List<string> directories, string directory)
    {
        if (!string.IsNullOrWhiteSpace(directory))
        {
            directories.Add(directory.Trim().Trim('"'));
        }
    }

    /// <summary>Full path of the installed vcxsrv.exe, or null if VcXsrv is not installed.</summary>
    public static string FindExecutable()
    {
        foreach (string directory in CandidateDirectories())
        {
            try
            {
                string path = Path.Combine(directory, ExecutableName);
                if (File.Exists(path))
                {
                    return path;
                }
            }
            catch
            {
            }
        }
        return null;
    }

    /// <summary>Text for the user: where VcXsrv was found or where it was looked for.</summary>
    public static string DescribeInstallation(string executable)
    {
        if (executable != null)
        {
            return "VcXsrv is installed: " + executable;
        }
        return "VcXsrv is not installed (" + ExecutableName + " was not found in " + DefaultInstallDirectory + " or next to SmarTTY).";
    }

    /// <summary>Asks GitHub for the latest VcXsrv release and its 64-bit installer.</summary>
    public static Release FindLatestRelease()
    {
        EnableTls12();
        Exception apiError;
        try
        {
            Release release = ParseRelease(DownloadText(LatestReleaseApi));
            if (release != null)
            {
                return release;
            }
            apiError = new InvalidDataException("The latest VcXsrv release has no 64-bit installer");
        }
        catch (Exception exception)
        {
            apiError = exception;
        }
        // The API is rate-limited for anonymous use; the release page redirects to the tag of the latest release as well.
        try
        {
            string version = FindLatestVersionFromPage();
            if (version != null)
            {
                return new Release
                {
                    Version = version,
                    FileName = "vcxsrv-64." + version + ".installer.exe",
                    DownloadUrl = string.Format(DownloadUrlFormat, version)
                };
            }
        }
        catch
        {
        }
        throw new WebException("Could not find the latest VcXsrv release: " + apiError.Message, apiError);
    }

    internal static Release ParseRelease(string json)
    {
        GitHubRelease release;
        using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
        {
            release = (GitHubRelease)new DataContractJsonSerializer(typeof(GitHubRelease)).ReadObject(stream);
        }
        GitHubAsset asset = release?.Assets?.FirstOrDefault((GitHubAsset candidate) => IsInstaller(candidate.Name) && !string.IsNullOrEmpty(candidate.DownloadUrl));
        if (asset == null)
        {
            return null;
        }
        string digest = asset.Digest;
        return new Release
        {
            Version = release.TagName,
            FileName = asset.Name,
            DownloadUrl = asset.DownloadUrl,
            Size = asset.Size,
            Sha256 = (digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) ? digest.Substring(7).ToLowerInvariant() : null
        };
    }

    internal static bool IsInstaller(string fileName)
    {
        return fileName != null && InstallerName.IsMatch(fileName);
    }

    // ".../releases/latest" answers with a redirect to ".../releases/tag/<version>".
    private static string FindLatestVersionFromPage()
    {
        HttpWebRequest request = CreateRequest(ReleasesPage);
        request.AllowAutoRedirect = false;
        request.Method = "HEAD";
        using HttpWebResponse response = (HttpWebResponse)request.GetResponse();
        return VersionFromReleaseUrl(response.Headers["Location"]);
    }

    internal static string VersionFromReleaseUrl(string url)
    {
        const string marker = "/releases/tag/";
        int index = url?.IndexOf(marker, StringComparison.Ordinal) ?? -1;
        if (index < 0)
        {
            return null;
        }
        string version = Uri.UnescapeDataString(url.Substring(index + marker.Length).Trim('/'));
        return VersionText.IsMatch(version) ? version : null;
    }

    /// <summary>
    /// Downloads the installer into the temporary folder and checks its size and SHA-256 when GitHub published them.
    /// <paramref name="progress"/> gets the bytes received so far and the total (0 if unknown).
    /// </summary>
    public static string Download(Release release, Action<long, long> progress, CancellationToken cancellation)
    {
        EnableTls12();
        string fileName = Path.GetFileName(release.FileName ?? "");
        if (!IsInstaller(fileName))
        {
            throw new InvalidDataException("Unexpected VcXsrv installer name: " + release.FileName);
        }
        string directory = Path.Combine(Path.GetTempPath(), "SmarTTY");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, fileName);
        string partial = path + ".part";
        HttpWebRequest request = CreateRequest(release.DownloadUrl);
        using (cancellation.Register(request.Abort))
        {
            try
            {
                using HttpWebResponse response = (HttpWebResponse)request.GetResponse();
                long total = (release.Size > 0) ? release.Size : Math.Max(0, response.ContentLength);
                using Stream source = response.GetResponseStream();
                using FileStream target = File.Create(partial);
                using SHA256 sha256 = SHA256.Create();
                byte[] buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    target.Write(buffer, 0, read);
                    sha256.TransformBlock(buffer, 0, read, null, 0);
                    received += read;
                    progress?.Invoke(received, total);
                }
                sha256.TransformFinalBlock(buffer, 0, 0);
                if (release.Size > 0 && received != release.Size)
                {
                    throw new InvalidDataException("The VcXsrv installer download is incomplete (" + received + " of " + release.Size + " bytes)");
                }
                if (release.Sha256 != null && !string.Equals(ToHex(sha256.Hash), release.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The downloaded VcXsrv installer does not match the SHA-256 published on GitHub");
                }
            }
            catch (WebException) when (cancellation.IsCancellationRequested)
            {
                TryDelete(partial);
                throw new OperationCanceledException(cancellation);
            }
            catch
            {
                TryDelete(partial);
                throw;
            }
        }
        TryDelete(path);
        File.Move(partial, path);
        return path;
    }

    /// <summary>
    /// Runs the installer (it asks for administrator rights) and waits until it finishes. Returns its exit code; throws
    /// <see cref="OperationCanceledException"/> if the user declined the elevation prompt.
    /// </summary>
    public static int RunInstaller(string installerPath, CancellationToken cancellation)
    {
        Process process;
        try
        {
            process = Process.Start(new ProcessStartInfo(installerPath)
            {
                UseShellExecute = true,
                Verb = "runas"
            });
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorCancelled)
        {
            throw new OperationCanceledException("The VcXsrv installation was cancelled", exception);
        }
        if (process == null)
        {
            throw new InvalidOperationException("Could not start the VcXsrv installer");
        }
        using (process)
        {
            while (!process.WaitForExit(500))
            {
                cancellation.ThrowIfCancellationRequested();
            }
            return process.ExitCode;
        }
    }

    /// <summary>
    /// Finds the latest release, downloads its installer, runs it and returns the installed vcxsrv.exe.
    /// <paramref name="progress"/> receives the text of the current step. An installed VcXsrv is returned as it is, nothing
    /// is downloaded (the buttons are disabled then, but a second window or the Tools menu may have started the same).
    /// </summary>
    public static string DownloadAndInstall(Action<string> progress, CancellationToken cancellation)
    {
        string installed = FindExecutable();
        if (installed != null)
        {
            progress(DescribeInstallation(installed));
            return installed;
        }
        progress("Looking for the latest VcXsrv release...");
        Release release = FindLatestRelease();
        string title = "Downloading VcXsrv " + release.Version;
        progress(title + "...");
        int lastPercent = -1;
        string installer = Download(release, (long received, long total) =>
        {
            int percent = (total > 0) ? (int)(received * 100 / total) : (int)(received / (1024 * 1024));
            if (percent != lastPercent)
            {
                lastPercent = percent;
                progress((total > 0) ? (title + ": " + percent + "%") : (title + ": " + percent + " MB"));
            }
        }, cancellation);
        progress("Waiting for the VcXsrv " + release.Version + " installer to finish...");
        int exitCode = RunInstaller(installer, cancellation);
        TryDelete(installer);
        return FindExecutable() ?? throw new FileNotFoundException("The VcXsrv installer finished (exit code " + exitCode + "), but " + ExecutableName + " was not found in " + DefaultInstallDirectory + ".");
    }

    public static void OpenReleasesPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(ReleasesPage) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private static HttpWebRequest CreateRequest(string url)
    {
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
        request.UserAgent = UserAgent;
        request.Timeout = 30000;
        request.ReadWriteTimeout = 60000;
        if (url.StartsWith("https://api.github.com/", StringComparison.Ordinal))
        {
            request.Accept = "application/vnd.github+json";
        }
        return request;
    }

    private static string DownloadText(string url)
    {
        using HttpWebResponse response = (HttpWebResponse)CreateRequest(url).GetResponse();
        using StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    // .NET Framework 4.8 uses the protocols of the operating system unless an older default is configured; GitHub needs TLS 1.2.
    private static void EnableTls12()
    {
        SecurityProtocolType protocols = ServicePointManager.SecurityProtocol;
        if (protocols != SecurityProtocolType.SystemDefault && (protocols & SecurityProtocolType.Tls12) == 0)
        {
            ServicePointManager.SecurityProtocol = protocols | SecurityProtocolType.Tls12;
        }
    }

    private static string ToHex(byte[] data)
    {
        StringBuilder text = new StringBuilder(data.Length * 2);
        foreach (byte value in data)
        {
            text.Append(value.ToString("x2"));
        }
        return text.ToString();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }
}
