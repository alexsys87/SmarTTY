using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using SmarTTY;

// Checks of "Install from URL..." of the package manager window: which links are accepted, the name a downloaded file gets
// (Content-Disposition, redirections, the first bytes), and the download itself against a local HTTP server.
internal static class UrlDownloadTests
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

    private static byte[] DebBytes(int size)
    {
        byte[] head = Encoding.ASCII.GetBytes("!<arch>\ndebian-binary   1700000000  0     0     100644  4         `\n2.0\n");
        byte[] data = new byte[Math.Max(size, head.Length)];
        Buffer.BlockCopy(head, 0, data, 0, head.Length);
        for (int i = head.Length; i < data.Length; i++)
        {
            data[i] = (byte)(i * 31);
        }
        return data;
    }

    private static readonly byte[] RpmBytes = { 0xED, 0xAB, 0xEE, 0xDB, 3, 0, 0, 0, 1, 2, 3, 4 };

    public static int Run()
    {
        CheckLinks();
        CheckNames();
        CheckContent();
        CheckChoice();
        CheckProgressText();
        CheckDownloads();
        return _passed;
    }

    private static void CheckLinks()
    {
        string[] urls = PackageUrlDownload.SplitUrls("https://a/x.deb\r\n\r\n# a comment https://b/y.deb\n  https://c/z.deb https://d/w.deb\nhttps://a/x.deb\n");
        Check(urls.SequenceEqual(new[] { "https://a/x.deb", "https://c/z.deb", "https://d/w.deb" }), "URL list: lines and blanks split it, comments and empty lines are skipped, a repeated link is kept once");
        Check(PackageUrlDownload.SplitUrls(null).Length == 0 && PackageUrlDownload.SplitUrls(" \n\t\n").Length == 0, "URL list: nothing in an empty text");

        Check(PackageUrlDownload.ParseUrl(" https://github.com/o/r/releases/download/v1/foo_1.0_amd64.deb ", out string error)?.Host == "github.com" && error == null, "URL: https is accepted, blanks around it are ignored");
        Check(PackageUrlDownload.ParseUrl("http://192.168.1.10:8080/foo.ipk", out _) != null, "URL: http with an address and a port is accepted");
        Check(PackageUrlDownload.ParseUrl("ftp://host/foo.deb", out error) == null && error.Contains("http"), "URL: ftp is refused");
        Check(PackageUrlDownload.ParseUrl("file:///C:/foo.deb", out _) == null, "URL: file:// is refused (Install package file... picks those)");
        Check(PackageUrlDownload.ParseUrl("foo.deb", out _) == null && PackageUrlDownload.ParseUrl("", out error) == null && error != null, "URL: a relative or empty link is refused");
    }

    private static void CheckNames()
    {
        Check(PackageUrlDownload.FileNameFromContentDisposition("attachment; filename=\"foo_1.0_amd64.deb\"") == "foo_1.0_amd64.deb", "Content-Disposition: quoted filename");
        Check(PackageUrlDownload.FileNameFromContentDisposition("attachment; filename=foo.rpm") == "foo.rpm", "Content-Disposition: unquoted filename");
        Check(PackageUrlDownload.FileNameFromContentDisposition("attachment; filename*=UTF-8''foo%20bar%2B1.deb; filename=\"fallback.deb\"") == "foo bar+1.deb", "Content-Disposition: filename* (RFC 5987) wins over filename");
        Check(PackageUrlDownload.FileNameFromContentDisposition("attachment; filename=\"a;b.deb\"") == "a;b.deb", "Content-Disposition: a semicolon inside quotes is part of the name");
        Check(PackageUrlDownload.FileNameFromContentDisposition("attachment; filename=\"../../etc/evil.deb\"") == "evil.deb", "Content-Disposition: folders of the server's name are dropped");
        Check(PackageUrlDownload.FileNameFromContentDisposition("attachment; filename=\"C:\\Windows\\evil.deb\"") == "evil.deb", "Content-Disposition: Windows folders are dropped too");
        Check(PackageUrlDownload.FileNameFromContentDisposition("inline") == null && PackageUrlDownload.FileNameFromContentDisposition(null) == null, "Content-Disposition: no name -> null");

        Check(PackageUrlDownload.FileNameFromUri(new Uri("https://h/pool/foo%2B1_amd64.deb?token=abc#x")) == "foo+1_amd64.deb", "URL name: the last segment, decoded, without query");
        Check(PackageUrlDownload.FileNameFromUri(new Uri("https://h/dir/")) == null && PackageUrlDownload.FileNameFromUri(new Uri("https://h")) == null, "URL name: a path that ends with a slash has none");

        Check(PackageUrlDownload.MakeSafeFileName("foo bar.deb") == "foo_bar.deb", "Safe name: a space becomes _");
        Check(PackageUrlDownload.MakeSafeFileName("$(reboot)`x`'q'.rpm") == "__reboot__x__q_.rpm", "Safe name: shell characters become _");
        Check(PackageUrlDownload.MakeSafeFileName("-rf.deb") == "_rf.deb", "Safe name: a leading dash is not taken for an option");
        Check(PackageUrlDownload.MakeSafeFileName("foo_1:2.3.deb") == "foo_1_2.3.deb", "Safe name: a colon (an NTFS stream) becomes _");
        Check(PackageUrlDownload.MakeSafeFileName("nul.deb") == "_nul.deb" && PackageUrlDownload.MakeSafeFileName("COM1.ipk") == "_COM1.ipk", "Safe name: Windows device names get a prefix");
        Check(PackageUrlDownload.MakeSafeFileName("..") == null && PackageUrlDownload.MakeSafeFileName("   ") == null && PackageUrlDownload.MakeSafeFileName("$$$") == null, "Safe name: nothing usable -> null");
        Check(PackageUrlDownload.MakeSafeFileName("пакет.deb") == "_____.deb", "Safe name: letters outside ASCII become _");
        foreach (string name in new[] { "foo bar.deb", "$(x).rpm", "-x.ipk", "a:b.apk", "nul.deb", "x\u0000y.deb" })
        {
            string safe = PackageUrlDownload.MakeSafeFileName(name);
            Check(safe != null && LocalPackageFiles.IsSafeFileName(safe), "Safe name: '" + name.Replace("\u0000", "\\0") + "' -> '" + safe + "' passes IsSafeFileName");
        }
    }

    private static void CheckContent()
    {
        byte[] deb = DebBytes(100);
        Check(PackageUrlDownload.SniffExtension(deb, deb.Length) == ".deb", "Sniff: an ar archive with debian-binary is a .deb");
        Check(PackageUrlDownload.SniffExtension(RpmBytes, RpmBytes.Length) == ".rpm", "Sniff: the RPM lead is an .rpm");
        Check(PackageUrlDownload.SniffExtension(new byte[] { 0x1F, 0x8B, 8, 0 }, 4) == null, "Sniff: a gzip archive (.ipk, .apk) tells nothing");
        Check(PackageUrlDownload.SniffExtension(Encoding.ASCII.GetBytes("!<arch>\nfoo"), 11) == null, "Sniff: another ar archive is not a .deb");
        Check(PackageUrlDownload.SniffExtension(deb, 5) == null && PackageUrlDownload.SniffExtension(null, 0) == null, "Sniff: too few bytes -> null");

        Check(PackageUrlDownload.LooksLikeHtml(Encoding.ASCII.GetBytes("\r\n  <!DOCTYPE html><html>"), 24), "HTML: a doctype after blank lines");
        Check(PackageUrlDownload.LooksLikeHtml(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.ASCII.GetBytes("<HTML><body>")).ToArray(), 15), "HTML: <HTML> after a UTF-8 byte order mark");
        Check(!PackageUrlDownload.LooksLikeHtml(deb, deb.Length) && !PackageUrlDownload.LooksLikeHtml(RpmBytes, RpmBytes.Length), "HTML: packages are not web pages");
    }

    private static void CheckChoice()
    {
        AptPackageManagerClient apt = new AptPackageManagerClient();
        OpkgPackageManagerClient opkg = new OpkgPackageManagerClient();
        PacmanPackageManagerClient pacman = new PacmanPackageManagerClient();
        byte[] deb = DebBytes(64);
        byte[] gzip = { 0x1F, 0x8B, 8, 0, 0, 0 };
        string error;

        Check(PackageUrlDownload.ChooseFileName(apt, "attachment; filename=\"tool_2.1_amd64.deb\"", new Uri("https://h/get?id=5"), new Uri("https://h/get?id=5"), deb, deb.Length, out error) == "tool_2.1_amd64.deb" && error == null, "Choice: the Content-Disposition name");
        Check(PackageUrlDownload.ChooseFileName(apt, null, new Uri("https://sourceforge.net/projects/p/files/foo_1.0_all.deb/download"), new Uri("https://mirror.example/p/foo_1.0_all.deb?viasf=1"), deb, deb.Length, out _) == "foo_1.0_all.deb", "Choice: the address after redirections (SourceForge .../download)");
        Check(PackageUrlDownload.ChooseFileName(apt, null, new Uri("https://github.com/o/r/releases/download/v1/foo_1.0_arm64.deb"), new Uri("https://objects.example/abc-123-def?sig=1"), deb, deb.Length, out _) == "foo_1.0_arm64.deb", "Choice: the link itself when the redirected address has no package name");
        Check(PackageUrlDownload.ChooseFileName(apt, null, new Uri("https://h/download?id=5"), new Uri("https://h/download?id=5"), deb, deb.Length, out _) == "download.deb", "Choice: no package name, the first bytes tell a .deb");
        Check(PackageUrlDownload.ChooseFileName(apt, null, new Uri("https://h/my%20tool.deb"), new Uri("https://h/my%20tool.deb"), deb, deb.Length, out _) == "my_tool.deb", "Choice: the name is made safe for the shell");
        Check(PackageUrlDownload.ChooseFileName(opkg, null, new Uri("http://192.168.1.1/luci-app-x_1.0_all.ipk"), new Uri("http://192.168.1.1/luci-app-x_1.0_all.ipk"), gzip, gzip.Length, out _) == "luci-app-x_1.0_all.ipk", "Choice: an .ipk for opkg");
        Check(PackageUrlDownload.ChooseFileName(pacman, null, new Uri("https://h/yay-12.0-1-x86_64.pkg.tar.zst"), new Uri("https://h/yay-12.0-1-x86_64.pkg.tar.zst"), gzip, gzip.Length, out _) == "yay-12.0-1-x86_64.pkg.tar.zst", "Choice: a .pkg.tar.zst for pacman");

        Check(PackageUrlDownload.ChooseFileName(apt, null, new Uri("https://h/foo.rpm"), new Uri("https://h/foo.rpm"), RpmBytes, RpmBytes.Length, out error) == null && error.Contains("apt-get") && error.Contains("Debian packages") && error.Contains("foo.rpm"), "Choice: an .rpm for apt is refused with the expected kind");
        Check(PackageUrlDownload.ChooseFileName(new DnfPackageManagerClient(), null, new Uri("https://h/get/5"), new Uri("https://h/get/5"), RpmBytes, RpmBytes.Length, out _) == "5.rpm", "Choice: no package name, the first bytes tell an .rpm");
        byte[] html = Encoding.ASCII.GetBytes("<!doctype html><title>Release v1</title>");
        Check(PackageUrlDownload.ChooseFileName(apt, null, new Uri("https://h/releases/foo.deb"), new Uri("https://h/login"), html, html.Length, out error) == null && error.Contains("web page"), "Choice: a web page is refused even when the link ends in .deb");
        Check(PackageUrlDownload.ChooseFileName(null, null, new Uri("https://h/foo.deb"), null, deb, deb.Length, out error) == null && error != null, "Choice: no installer -> refused");

        Check(new LocalPackageInstallRequest(new[] { "a.deb" }, apt, null, false).TemporaryDirectory == null, "Request: picked files have no temporary folder");
        Check(new LocalPackageInstallRequest(new[] { "a.deb" }, apt, null, false, "/tmp/x").TemporaryDirectory == "/tmp/x", "Request: downloaded files keep the folder to remove");
    }

    private static void CheckProgressText()
    {
        Check(PackageUrlDownload.FormatSize(512) == "512 bytes" && PackageUrlDownload.FormatSize(1536) == "1.5 KB" && PackageUrlDownload.FormatSize(5 * 1024 * 1024) == "5.0 MB" && PackageUrlDownload.FormatSize(3L * 1024 * 1024 * 1024 / 2) == "1.50 GB", "Sizes: bytes, KB, MB, GB");
        Check(PackageUrlDownload.FormatProgress(1, 3, "foo.deb", 512 * 1024, 1024 * 1024) == "Downloading 2 of 3: foo.deb, 50% (512.0 KB of 1.0 MB)", "Progress: n of m, percent and sizes");
        Check(PackageUrlDownload.FormatProgress(0, 1, "foo.deb", 2048, 0) == "Downloading foo.deb, 2.0 KB", "Progress: one file of unknown size");
        Check(PackageUrlDownload.BarValue(1, 4, 50, 100) == 37.5 && PackageUrlDownload.BarValue(0, 1, 10, 0) == -1 && PackageUrlDownload.BarValue(0, 1, 200, 100) == 100, "Progress bar: the share of all downloads; -1 without a total");
    }

    private sealed class TestServer : IDisposable
    {
        private readonly HttpListener _listener = new HttpListener();

        private readonly Dictionary<string, Action<HttpListenerContext>> _routes = new Dictionary<string, Action<HttpListenerContext>>();

        public readonly string Prefix;

        public TestServer()
        {
            TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Prefix = "http://127.0.0.1:" + port + "/";
            _listener.Prefixes.Add(Prefix);
            _listener.Start();
            new Thread(Serve) { IsBackground = true }.Start();
        }

        public void Route(string path, Action<HttpListenerContext> handler)
        {
            lock (_routes)
            {
                _routes[path] = handler;
            }
        }

        private void Serve()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = _listener.GetContext();
                }
                catch
                {
                    return;
                }
                Action<HttpListenerContext> handler;
                lock (_routes)
                {
                    _routes.TryGetValue(context.Request.Url.AbsolutePath, out handler);
                }
                try
                {
                    if (handler == null)
                    {
                        context.Response.StatusCode = 404;
                        context.Response.StatusDescription = "Not Found";
                    }
                    else
                    {
                        handler(context);
                    }
                    context.Response.Close();
                }
                catch
                {
                    // The client went away (the cancellation check).
                }
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
        }
    }

    private static void Send(HttpListenerContext context, byte[] body, string contentDisposition = null, bool chunked = false)
    {
        if (contentDisposition != null)
        {
            context.Response.AddHeader("Content-Disposition", contentDisposition);
        }
        context.Response.ContentType = "application/octet-stream";
        if (chunked)
        {
            context.Response.SendChunked = true;
        }
        else
        {
            context.Response.ContentLength64 = body.Length;
        }
        context.Response.OutputStream.Write(body, 0, body.Length);
    }

    private static T Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T exception)
        {
            return exception;
        }
        catch (Exception other)
        {
            throw new Exception("FAIL: expected " + typeof(T).Name + ", got " + other.GetType().Name + ": " + other.Message);
        }
        throw new Exception("FAIL: expected " + typeof(T).Name + ", nothing was thrown");
    }

    private static void CheckDownloads()
    {
        // The environment of a build machine may name a proxy: the local server is reached directly.
#pragma warning disable SYSLIB0014
        WebRequest.DefaultWebProxy = null;
#pragma warning restore SYSLIB0014
        AptPackageManagerClient apt = new AptPackageManagerClient();
        byte[] deb = DebBytes(300000);
        string directory = Path.Combine(Path.GetTempPath(), "smartty-url-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using TestServer server = new TestServer();
            server.Route("/pool/foo_1.0_all.deb", context => Send(context, deb));
            server.Route("/redirect", context => context.Response.Redirect(server.Prefix + "pool/bar_2.0_all.deb"));
            server.Route("/pool/bar_2.0_all.deb", context => Send(context, deb));
            server.Route("/get", context => Send(context, deb, "attachment; filename=\"baz_3.0_all.deb\""));
            server.Route("/blob", context => Send(context, deb, chunked: true));
            server.Route("/release/qux.deb", context => Send(context, Encoding.ASCII.GetBytes("<!DOCTYPE html><html><body>Sign in</body></html>")));
            server.Route("/empty.deb", context => Send(context, new byte[0]));

            long lastReceived = -1;
            long lastTotal = -1;
            string path = PackageUrlDownloader.Download(new Uri(server.Prefix + "pool/foo_1.0_all.deb"), apt, directory, (received, total) => { lastReceived = received; lastTotal = total; }, CancellationToken.None);
            Check(Path.GetFileName(path) == "foo_1.0_all.deb" && File.ReadAllBytes(path).SequenceEqual(deb), "Download: the file arrives whole under the name of the link");
            Check(lastReceived == deb.Length && lastTotal == deb.Length, "Download: the progress reaches the size the server announced");

            path = PackageUrlDownloader.Download(new Uri(server.Prefix + "redirect"), apt, directory, null, CancellationToken.None);
            Check(Path.GetFileName(path) == "bar_2.0_all.deb", "Download: a redirection is followed and names the file");

            path = PackageUrlDownloader.Download(new Uri(server.Prefix + "get?id=7"), apt, directory, null, CancellationToken.None);
            Check(Path.GetFileName(path) == "baz_3.0_all.deb", "Download: the Content-Disposition name is used");

            lastTotal = -1;
            path = PackageUrlDownloader.Download(new Uri(server.Prefix + "blob"), apt, directory, (received, total) => lastTotal = total, CancellationToken.None);
            Check(Path.GetFileName(path) == "blob.deb" && new FileInfo(path).Length == deb.Length && lastTotal == 0, "Download: chunked without a size, the first bytes tell a .deb");

            string[] before = Directory.GetFiles(directory);
            InvalidDataException page = Throws<InvalidDataException>(() => PackageUrlDownloader.Download(new Uri(server.Prefix + "release/qux.deb"), apt, directory, null, CancellationToken.None));
            Check(page.Message.Contains("web page") && Directory.GetFiles(directory).SequenceEqual(before), "Download: a web page is refused and leaves no file");

            IOException missing = Throws<IOException>(() => PackageUrlDownloader.Download(new Uri(server.Prefix + "nothing.deb"), apt, directory, null, CancellationToken.None));
            Check(missing.Message.Contains("404") && missing.Message.Contains("nothing.deb"), "Download: the HTTP status of a failed download is told");

            Check(Throws<InvalidDataException>(() => PackageUrlDownloader.Download(new Uri(server.Prefix + "empty.deb"), apt, directory, null, CancellationToken.None)).Message.Contains("empty"), "Download: an empty file is refused");

            InvalidDataException twice = Throws<InvalidDataException>(() => PackageUrlDownloader.Download(new Uri(server.Prefix + "pool/foo_1.0_all.deb"), apt, directory, null, CancellationToken.None));
            Check(twice.Message.Contains("same file name") && Directory.GetFiles(directory).SequenceEqual(before), "Download: two links with the same file name are refused, the first file is kept");

            InvalidDataException wrongKind = Throws<InvalidDataException>(() => PackageUrlDownloader.Download(new Uri(server.Prefix + "pool/foo_1.0_all.deb"), new OpkgPackageManagerClient(), directory, null, CancellationToken.None));
            Check(wrongKind.Message.Contains("opkg"), "Download: a .deb for opkg is refused after the download");

            using CancellationTokenSource cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Throws<OperationCanceledException>(() => PackageUrlDownloader.Download(new Uri(server.Prefix + "pool/foo_1.0_all.deb"), apt, directory, null, cancelled.Token));
            Check(true, "Download: a cancelled download throws OperationCanceledException");

            using CancellationTokenSource midway = new CancellationTokenSource();
            Throws<OperationCanceledException>(() => PackageUrlDownloader.Download(new Uri(server.Prefix + "blob"), apt, directory, (received, total) => { if (received > 0) midway.Cancel(); }, midway.Token));
            Check(Directory.GetFiles(directory).SequenceEqual(before), "Download: cancelled midway, the partial file is removed");

            Check(Throws<IOException>(() => PackageUrlDownloader.Download(new Uri("http://127.0.0.1:1/foo.deb"), apt, directory, null, CancellationToken.None)).Message.Contains("127.0.0.1"), "Download: a refused connection is told with the link");

            string own = PackageUrlDownloader.CreateDownloadDirectory();
            Check(Directory.Exists(own) && own.StartsWith(PackageUrlDownloader.DownloadsRoot, StringComparison.Ordinal) && Directory.GetFileSystemEntries(own).Length == 0, "Download folder: a new empty folder under the downloads root");
            PackageUrlDownloader.DeleteStaleDirectories(TimeSpan.FromDays(1));
            Check(Directory.Exists(own), "Download folder: a fresh folder is not removed as stale");
            Directory.Delete(own);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
