using System;
using System.IO;
using System.Net;
using System.Threading;

namespace SmarTTY;

/// <summary>
/// Downloads a package file from a link to this computer, for the "Install from URL..." button of the package manager window:
/// the file is then uploaded to the host (SFTP or SCP, like any other upload) and installed there, so the host needs no access
/// to the internet. The requests go through the proxy of Windows, as those of a browser do. Tested by tests/ApkPackageManagerTests
/// against a local HTTP server.
/// </summary>
public static class PackageUrlDownloader
{
    private const string UserAgent = "SmarTTY";

    /// <summary>The folder of this computer under which every download gets a folder of its own.</summary>
    public static string DownloadsRoot => Path.Combine(Path.GetTempPath(), "SmarTTY", "PackageDownloads");

    /// <summary>
    /// Makes an empty folder for the files of one download (a name nobody can guess, like the upload directory on the host) and
    /// removes the folders of earlier downloads that are older than a day: they are normally removed when their upload ends, but
    /// SmarTTY may have been closed before that.
    /// </summary>
    public static string CreateDownloadDirectory()
    {
        DeleteStaleDirectories(TimeSpan.FromDays(1));
        string directory = Path.Combine(DownloadsRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>Removes the folders under <see cref="DownloadsRoot"/> that were made longer than <paramref name="age"/> ago (best effort).</summary>
    public static void DeleteStaleDirectories(TimeSpan age)
    {
        try
        {
            if (!Directory.Exists(DownloadsRoot))
            {
                return;
            }
            DateTime limit = DateTime.UtcNow - age;
            foreach (string directory in Directory.GetDirectories(DownloadsRoot))
            {
                try
                {
                    if (Directory.GetCreationTimeUtc(directory) < limit)
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                }
                catch
                {
                    // In use by another SmarTTY, or already gone.
                }
            }
        }
        catch
        {
            // A leftover in the temp folder is harmless.
        }
    }

    /// <summary>
    /// Downloads <paramref name="url"/> into <paramref name="directory"/> and returns the path of the file, named as
    /// <see cref="PackageUrlDownload.ChooseFileName"/> decides. <paramref name="progress"/> gets the bytes received so far and the total
    /// (0 when the server does not tell it). Throws <see cref="InvalidDataException"/> when the file is not a package of
    /// <paramref name="installer"/> (nothing is left behind then), <see cref="IOException"/> with the answer of the server when the
    /// download fails, and <see cref="OperationCanceledException"/> when it is cancelled.
    /// </summary>
    public static string Download(Uri url, ILocalPackageFileInstaller installer, string directory, Action<long, long> progress, CancellationToken cancellation)
    {
        if (url == null)
        {
            throw new ArgumentNullException(nameof(url));
        }
        EnableTls12();
        cancellation.ThrowIfCancellationRequested();
        string partial = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".part");
        HttpWebRequest request = CreateRequest(url);
        try
        {
            using (cancellation.Register(request.Abort))
            {
                using HttpWebResponse response = GetResponse(request, url);
                long total = Math.Max(0, response.ContentLength);
                byte[] head = new byte[PackageUrlDownload.HeadLength];
                int headCount = 0;
                long received = 0;
                using (Stream source = response.GetResponseStream())
                using (FileStream target = new FileStream(partial, FileMode.CreateNew, FileAccess.Write))
                {
                    byte[] buffer = new byte[81920];
                    int read;
                    progress?.Invoke(0, total);
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (headCount < head.Length)
                        {
                            int copy = Math.Min(read, head.Length - headCount);
                            Buffer.BlockCopy(buffer, 0, head, headCount, copy);
                            headCount += copy;
                        }
                        target.Write(buffer, 0, read);
                        received += read;
                        progress?.Invoke(received, total);
                    }
                }
                cancellation.ThrowIfCancellationRequested();
                if (total > 0 && received != total)
                {
                    throw new IOException("The download of " + url + " is incomplete (" + received + " of " + total + " bytes). Try again.");
                }
                if (received == 0)
                {
                    throw new InvalidDataException("The server sent an empty file for " + url);
                }
                string name = PackageUrlDownload.ChooseFileName(installer, response.Headers["Content-Disposition"], url, response.ResponseUri, head, headCount, out string error);
                if (name == null)
                {
                    throw new InvalidDataException(error);
                }
                string path = Path.Combine(directory, name);
                if (File.Exists(path))
                {
                    throw new InvalidDataException("Two of the links give the same file name, " + name + ": they would overwrite each other on the server. Remove one of them.");
                }
                File.Move(partial, path);
                return path;
            }
        }
        catch (Exception exception) when (cancellation.IsCancellationRequested && !(exception is OperationCanceledException))
        {
            // Abort() makes the pending read fail with a WebException or an IOException.
            throw new OperationCanceledException("The download was cancelled", exception, cancellation);
        }
        finally
        {
            TryDelete(partial);
        }
    }

    private static HttpWebResponse GetResponse(HttpWebRequest request, Uri url)
    {
        try
        {
            return (HttpWebResponse)request.GetResponse();
        }
        catch (WebException exception) when (exception.Response is HttpWebResponse response)
        {
            using (response)
            {
                throw new IOException("The server answered " + (int)response.StatusCode + " " + response.StatusDescription + " for " + url, exception);
            }
        }
        catch (WebException exception) when (exception.Status != WebExceptionStatus.RequestCanceled)
        {
            throw new IOException("Could not download " + url + ": " + exception.Message, exception);
        }
    }

    private static HttpWebRequest CreateRequest(Uri url)
    {
#pragma warning disable SYSLIB0014 // HttpWebRequest is what .NET Framework 4.8 has; the tests build it for .NET 8.
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
#pragma warning restore SYSLIB0014
        request.UserAgent = UserAgent;
        request.Accept = "*/*";
        request.Timeout = 30000;
        request.ReadWriteTimeout = 60000;
        request.AllowAutoRedirect = true;
        request.MaximumAutomaticRedirections = 20;
        // The proxy of Windows, with the credentials of the user (a proxy of a company asks for them).
        if (request.Proxy != null)
        {
            request.Proxy.Credentials = CredentialCache.DefaultCredentials;
        }
        return request;
    }

    // .NET Framework 4.8 uses the protocols of the operating system unless an older default is configured; most servers need TLS 1.2.
    private static void EnableTls12()
    {
#pragma warning disable SYSLIB0014
        SecurityProtocolType protocols = ServicePointManager.SecurityProtocol;
        if (protocols != SecurityProtocolType.SystemDefault && (protocols & SecurityProtocolType.Tls12) == 0)
        {
            ServicePointManager.SecurityProtocol = protocols | SecurityProtocolType.Tls12;
        }
#pragma warning restore SYSLIB0014
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
