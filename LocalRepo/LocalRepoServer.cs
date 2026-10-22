using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SmarTTY.LocalRepo;

/// <summary>
/// A small read-only HTTP server for local OpenWrt package folders. It listens on the loopback interface only: the device
/// reaches it through a reverse SSH tunnel. The folders are served under /r0/, /r1/, ... (in the order they were given).
/// </summary>
public sealed class LocalRepoServer : IDisposable
{
    private readonly string[] _Roots;

    private readonly int _RequestedPort;

    private TcpListener _Listener;

    private Thread _AcceptThread;

    private volatile bool _Stopped;

    private volatile IList<LocalRepoFeed> _Feeds = new LocalRepoFeed[0];

    /// <summary>Raised (on a worker thread) for every request, e.g. "GET /r0/base/Packages.gz - 200, 1234 bytes".</summary>
    public event Action<string> RequestLogged;

    /// <summary>The TCP port; after Start it is the port that is really in use (it differs from the requested one only for port 0).</summary>
    public int Port { get; private set; }

    /// <summary>The feeds listed on the start page of the server.</summary>
    public IList<LocalRepoFeed> Feeds
    {
        get
        {
            return _Feeds;
        }
        set
        {
            _Feeds = value ?? new LocalRepoFeed[0];
        }
    }

    public LocalRepoServer(IList<string> roots, int port)
    {
        _Roots = new string[roots.Count];
        roots.CopyTo(_Roots, 0);
        _RequestedPort = port;
        Port = port;
    }

    /// <summary>Starts listening; throws a SocketException when the port is busy.</summary>
    public void Start()
    {
        TcpListener listener = new TcpListener(IPAddress.Loopback, _RequestedPort);
        listener.Start();
        _Listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _AcceptThread = new Thread(AcceptLoop)
        {
            IsBackground = true,
            Name = "Local repository server"
        };
        _AcceptThread.Start();
    }

    public void Stop()
    {
        _Stopped = true;
        try
        {
            _Listener?.Stop();
        }
        catch
        {
        }
        if (_AcceptThread != null && Thread.CurrentThread != _AcceptThread)
        {
            _AcceptThread.Join(TimeSpan.FromSeconds(2));
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private void AcceptLoop()
    {
        while (!_Stopped)
        {
            TcpClient client;
            try
            {
                client = _Listener.AcceptTcpClient();
            }
            catch (Exception exception) when (exception is SocketException || exception is ObjectDisposedException || exception is InvalidOperationException)
            {
                break;
            }
            ThreadPool.QueueUserWorkItem((object state) => HandleClient((TcpClient)state), client);
        }
    }

    private void Log(string message)
    {
        try
        {
            RequestLogged?.Invoke(message);
        }
        catch
        {
        }
    }

    private void HandleClient(TcpClient client)
    {
        string requestText = "?";
        try
        {
            using (client)
            {
                client.ReceiveTimeout = 15000;
                client.SendTimeout = 60000;
                using (NetworkStream stream = client.GetStream())
                {
                    string header = ReadHeader(stream);
                    if (header == null)
                    {
                        return;
                    }
                    string requestLine = header.Substring(0, header.IndexOf('\n') < 0 ? header.Length : header.IndexOf('\n')).Trim();
                    string[] parts = requestLine.Split(' ');
                    if (parts.Length < 2)
                    {
                        SendText(stream, 400, "Bad Request", "Bad request", head: false);
                        return;
                    }
                    string method = parts[0];
                    requestText = method + " " + parts[1];
                    bool head = method == "HEAD";
                    if (method != "GET" && !head)
                    {
                        SendText(stream, 405, "Method Not Allowed", "Only GET and HEAD are supported", head: false);
                        Log(requestText + " - 405");
                        return;
                    }
                    string note = Route(stream, parts[1], head, out int status, out long length);
                    Log(requestText + " - " + status + (length >= 0 ? ", " + length + " bytes" : "") + (note == null ? "" : " (" + note + ")"));
                }
            }
        }
        catch (Exception exception) when (exception is IOException || exception is SocketException || exception is ObjectDisposedException)
        {
            Log(requestText + " - interrupted");
        }
        catch (Exception exception)
        {
            Log(requestText + " - error: " + exception.Message);
        }
    }

    /// <summary>Reads the request header (up to the empty line); returns null if the client sent too much or nothing.</summary>
    private static string ReadHeader(Stream stream)
    {
        byte[] buffer = new byte[16 * 1024];
        int length = 0;
        while (length < buffer.Length)
        {
            int read = stream.Read(buffer, length, buffer.Length - length);
            if (read <= 0)
            {
                return null;
            }
            length += read;
            string text = Encoding.ASCII.GetString(buffer, 0, length);
            if (text.Contains("\r\n\r\n") || text.Contains("\n\n"))
            {
                return text;
            }
        }
        return null;
    }

    /// <summary>Sends the response for a request target; returns a short note for the log and the status and body length through the out parameters.</summary>
    private string Route(Stream stream, string target, bool head, out int status, out long length)
    {
        status = 404;
        length = -1;
        int query = target.IndexOf('?');
        if (query >= 0)
        {
            target = target.Substring(0, query);
        }
        string path;
        try
        {
            path = Uri.UnescapeDataString(target);
        }
        catch (UriFormatException)
        {
            status = 400;
            SendText(stream, 400, "Bad Request", "Bad request", head);
            return null;
        }
        if (path.IndexOf('\0') >= 0)
        {
            status = 400;
            SendText(stream, 400, "Bad Request", "Bad request", head);
            return null;
        }
        if (path == "/")
        {
            byte[] page = Encoding.UTF8.GetBytes(BuildStartPage());
            status = 200;
            length = page.Length;
            SendBytes(stream, 200, "OK", "text/html; charset=utf-8", page, head);
            return null;
        }

        string filePath = MapToFile(path);
        if (filePath == null)
        {
            SendText(stream, 404, "Not Found", "Not found", head);
            return null;
        }

        string fileName = Path.GetFileName(filePath);
        string directory = Path.GetDirectoryName(filePath);
        bool isIndex = fileName == "Packages" || fileName == "Packages.gz";
        bool isSignature = fileName == "Packages.sig";
        if ((isIndex || isSignature) && directory != null && Directory.Exists(directory) && IpkIndexBuilder.NeedsGeneratedIndex(directory))
        {
            if (isSignature)
            {
                // The signature of an older index does not match the generated one.
                SendText(stream, 404, "Not Found", "Not found", head);
                return "the index is generated, so there is no signature";
            }
            IpkIndexBuilder.Index index = IpkIndexBuilder.GetIndex(directory);
            byte[] body = fileName == "Packages" ? index.Packages : index.PackagesGz;
            status = 200;
            length = body.Length;
            SendBytes(stream, 200, "OK", fileName == "Packages" ? "text/plain; charset=utf-8" : "application/octet-stream", body, head);
            string note = "index generated from " + index.Count + " .ipk files";
            if (index.Warnings.Count > 0)
            {
                note += "; skipped: " + string.Join("; ", index.Warnings.ToArray());
            }
            return note;
        }

        if (!File.Exists(filePath))
        {
            SendText(stream, 404, "Not Found", "Not found", head);
            return null;
        }
        using (FileStream file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            status = 200;
            length = file.Length;
            SendHeader(stream, 200, "OK", fileName == "Packages" ? "text/plain; charset=utf-8" : "application/octet-stream", file.Length);
            if (!head)
            {
                file.CopyTo(stream, 64 * 1024);
            }
        }
        return null;
    }

    /// <summary>Maps "/r0/base/foo.ipk" to a file of the first root folder; returns null for anything that is not inside a root folder.</summary>
    private string MapToFile(string path)
    {
        if (!path.StartsWith("/r", StringComparison.Ordinal))
        {
            return null;
        }
        int slash = path.IndexOf('/', 1);
        string indexText = slash < 0 ? path.Substring(2) : path.Substring(2, slash - 2);
        if (!int.TryParse(indexText, out int rootIndex) || rootIndex < 0 || rootIndex >= _Roots.Length)
        {
            return null;
        }
        string relative = slash < 0 ? "" : path.Substring(slash + 1);
        return ResolveInside(_Roots[rootIndex], relative);
    }

    /// <summary>
    /// Combines a root folder with a relative URL path. Returns null if the path tries to leave the folder
    /// (".." segments, backslashes, drive letters, characters that are not valid in file names).
    /// </summary>
    internal static string ResolveInside(string root, string relativeUrlPath)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        List<string> segments = new List<string>();
        foreach (string segment in relativeUrlPath.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }
            if (segment == ".." || segment.IndexOfAny(invalid) >= 0 || segment.IndexOf('\\') >= 0 || segment.IndexOf(':') >= 0 || segment.EndsWith(".", StringComparison.Ordinal) || segment.EndsWith(" ", StringComparison.Ordinal))
            {
                return null;
            }
            segments.Add(segment);
        }
        string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string full = segments.Count == 0 ? rootFull : Path.GetFullPath(Path.Combine(rootFull, string.Join(Path.DirectorySeparatorChar.ToString(), segments.ToArray())));
        StringComparison comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.Equals(rootFull, comparison) && !full.StartsWith(rootFull + Path.DirectorySeparatorChar, comparison))
        {
            return null;
        }
        return full;
    }

    private string BuildStartPage()
    {
        StringBuilder page = new StringBuilder();
        page.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>SmarTTY local OpenWrt repository</title></head><body>");
        page.Append("<h3>SmarTTY local OpenWrt repository</h3>");
        IList<LocalRepoFeed> feeds = _Feeds;
        if (feeds.Count == 0)
        {
            page.Append("<p>No feeds found.</p>");
        }
        else
        {
            page.Append("<ul>");
            foreach (LocalRepoFeed feed in feeds)
            {
                page.Append("<li><a href=\"").Append(WebUtility.HtmlEncode(feed.UrlPath)).Append("/Packages\">").Append(WebUtility.HtmlEncode(feed.UrlPath)).Append("</a> &mdash; ").Append(WebUtility.HtmlEncode(feed.Directory)).Append("</li>");
            }
            page.Append("</ul>");
        }
        page.Append("</body></html>");
        return page.ToString();
    }

    private static void SendText(Stream stream, int status, string reason, string text, bool head)
    {
        SendBytes(stream, status, reason, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text), head);
    }

    private static void SendBytes(Stream stream, int status, string reason, string contentType, byte[] body, bool head)
    {
        SendHeader(stream, status, reason, contentType, body.Length);
        if (!head)
        {
            stream.Write(body, 0, body.Length);
        }
    }

    private static void SendHeader(Stream stream, int status, string reason, string contentType, long length)
    {
        string header = "HTTP/1.1 " + status + " " + reason + "\r\n" +
            "Content-Type: " + contentType + "\r\n" +
            "Content-Length: " + length + "\r\n" +
            "Accept-Ranges: none\r\n" +
            "Connection: close\r\n\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(header);
        stream.Write(bytes, 0, bytes.Length);
    }
}
