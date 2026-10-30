using Renci.SshNet.Common;
using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SmarTTY.Ssh;

/// <summary>
/// The proxies SSH.NET does not offer (SOCKS4/4a and HTTPS): SmarTTY opens the connection through the proxy itself and
/// hands it to SSH.NET through a loopback socket that accepts a single connection, relaying the data in both directions.
/// <see cref="Open"/> runs the proxy handshake before SSH.NET connects, so a refusal of the proxy is reported as a
/// <see cref="ProxyException"/> (a 407 answer makes SmarTTY ask for the proxy credentials, as with the built-in proxies).
/// </summary>
internal sealed class ProxyRelay : IDisposable
{
    // The answer headers of an HTTP proxy are short; this only stops a broken proxy from feeding us forever.
    private const int MaxResponseHeaderBytes = 16 * 1024;

    private readonly SshConnectionParameters _parameters;

    private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);

    private Stream _proxyStream;

    private Socket _localSocket;

    private int _disposed;

    public static bool IsNeeded(SshProxyType proxyType)
    {
        return proxyType == SshProxyType.Socks4 || proxyType == SshProxyType.Https;
    }

    public ProxyRelay(SshConnectionParameters parameters)
    {
        _parameters = parameters;
        _listener.Start(1);
    }

    /// <summary>The loopback port SSH.NET connects to.</summary>
    public int LocalPort => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Connects to the proxy and asks it for the SSH server; then waits for SSH.NET on <see cref="LocalPort"/>.</summary>
    public void Open()
    {
        TimeSpan timeout = _parameters.ConnectTimeout;
        Socket socket = ConnectSocket(_parameters.ProxyHost, _parameters.ProxyPort, timeout);
        int timeoutMs = (int)Math.Min(int.MaxValue, timeout.TotalMilliseconds);
        socket.ReceiveTimeout = timeoutMs;
        socket.SendTimeout = timeoutMs;
        Stream stream = new NetworkStream(socket, ownsSocket: true);
        _proxyStream = stream;
        if (_parameters.ProxyType == SshProxyType.Https)
        {
            SslStream tls = new SslStream(stream, leaveInnerStreamOpen: false, _parameters.ProxyCertificateValidation);
            _proxyStream = tls;
            try
            {
                tls.AuthenticateAsClient(_parameters.ProxyHost);
            }
            catch (System.Security.Authentication.AuthenticationException exception)
            {
                throw new ProxyException("HTTPS: The TLS connection to the proxy failed: " + exception.Message, exception);
            }
            HttpConnect(tls);
        }
        else
        {
            Socks4Connect(stream);
        }
        // The tunnel stays open as long as the SSH connection: no timeouts for the relayed data.
        socket.ReceiveTimeout = 0;
        socket.SendTimeout = 0;
        new Thread(AcceptAndRelay) { IsBackground = true, Name = "Proxy relay: " + _parameters.Host }.Start();
    }

    private void AcceptAndRelay()
    {
        try
        {
            _localSocket = _listener.AcceptSocket();
        }
        catch (Exception)
        {
            Dispose();
            return;
        }
        // Only the one connection of SSH.NET is relayed.
        _listener.Stop();
        NetworkStream local = new NetworkStream(_localSocket, ownsSocket: false);
        new Thread(() => Pump(_proxyStream, local)) { IsBackground = true, Name = "Proxy relay in: " + _parameters.Host }.Start();
        Pump(local, _proxyStream);
    }

    // Copies until either side closes, then closes both, so SSH.NET sees the end of the proxy connection and the other way round.
    private void Pump(Stream from, Stream to)
    {
        byte[] buffer = new byte[64 * 1024];
        try
        {
            int read;
            while ((read = from.Read(buffer, 0, buffer.Length)) > 0)
            {
                to.Write(buffer, 0, read);
                to.Flush();
            }
        }
        catch (Exception)
        {
        }
        Dispose();
    }

    // HTTP CONNECT (here over TLS): "CONNECT host:port", optionally with Basic credentials; any 2xx answer opens the tunnel.
    private void HttpConnect(Stream stream)
    {
        string target = FormatHost(_parameters.Host) + ":" + _parameters.Port.ToString(CultureInfo.InvariantCulture);
        StringBuilder request = new StringBuilder();
        request.Append("CONNECT ").Append(target).Append(" HTTP/1.1\r\n");
        request.Append("Host: ").Append(target).Append("\r\n");
        if (!string.IsNullOrEmpty(_parameters.ProxyUserName))
        {
            string credentials = _parameters.ProxyUserName + ":" + (_parameters.ProxyPassword ?? "");
            request.Append("Proxy-Authorization: Basic ").Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials))).Append("\r\n");
        }
        request.Append("\r\n");
        byte[] data = Encoding.ASCII.GetBytes(request.ToString());
        stream.Write(data, 0, data.Length);
        stream.Flush();

        string statusLine = ReadResponseHeaders(stream);
        string[] parts = statusLine.Split(new[] { ' ' }, 3);
        if (parts.Length < 2 || !parts[0].StartsWith("HTTP/", StringComparison.Ordinal) || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int status))
        {
            throw new ProxyException("HTTPS: The proxy did not answer with HTTP (\"" + statusLine + "\").");
        }
        if (status < 200 || status > 299)
        {
            // The same wording as the HTTP proxy of SSH.NET, which SshSession.IsProxyAuthenticationFailure recognizes for 407.
            throw new ProxyException("HTTPS: Status code " + status + ", \"" + (parts.Length > 2 ? parts[2] : "") + "\"");
        }
    }

    // Reads the answer up to the empty line (byte by byte: what follows already belongs to the SSH connection) and returns its first line.
    private static string ReadResponseHeaders(Stream stream)
    {
        StringBuilder headers = new StringBuilder();
        while (!headers.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && !headers.ToString().EndsWith("\n\n", StringComparison.Ordinal))
        {
            int value = stream.ReadByte();
            if (value < 0)
            {
                throw new ProxyException("HTTPS: The proxy closed the connection without an answer.");
            }
            if (headers.Length >= MaxResponseHeaderBytes)
            {
                throw new ProxyException("HTTPS: The answer of the proxy is too long.");
            }
            headers.Append((char)value);
        }
        string text = headers.ToString();
        int lineEnd = text.IndexOf('\n');
        return text.Substring(0, lineEnd).TrimEnd('\r');
    }

    // SOCKS4, or SOCKS4a for a host name: then the proxy resolves the name (an address 0.0.0.x and the name after the user ID).
    private void Socks4Connect(Stream stream)
    {
        byte[] address;
        string hostName = null;
        if (IPAddress.TryParse(_parameters.Host, out IPAddress ip))
        {
            if (ip.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new ProxyException("SOCKS4: The proxy cannot connect to the IPv6 address " + _parameters.Host + "; use SOCKS5.");
            }
            address = ip.GetAddressBytes();
        }
        else
        {
            address = new byte[4] { 0, 0, 0, 1 };
            hostName = _parameters.Host;
        }
        MemoryStream request = new MemoryStream();
        request.WriteByte(4);
        request.WriteByte(1);
        request.WriteByte((byte)(_parameters.Port >> 8));
        request.WriteByte((byte)_parameters.Port);
        request.Write(address, 0, 4);
        byte[] userId = Encoding.UTF8.GetBytes(_parameters.ProxyUserName ?? "");
        request.Write(userId, 0, userId.Length);
        request.WriteByte(0);
        if (hostName != null)
        {
            byte[] name = Encoding.ASCII.GetBytes(hostName);
            request.Write(name, 0, name.Length);
            request.WriteByte(0);
        }
        byte[] data = request.ToArray();
        stream.Write(data, 0, data.Length);
        stream.Flush();

        byte[] answer = new byte[8];
        int offset = 0;
        while (offset < answer.Length)
        {
            int read = stream.Read(answer, offset, answer.Length - offset);
            if (read == 0)
            {
                throw new ProxyException("SOCKS4: The proxy closed the connection without an answer.");
            }
            offset += read;
        }
        switch (answer[1])
        {
            case 0x5A:
                return;
            case 0x5B:
                throw new ProxyException("SOCKS4: The proxy rejected the connection to " + _parameters.Host + ":" + _parameters.Port + ".");
            case 0x5C:
                throw new ProxyException("SOCKS4: The proxy could not reach identd on this computer to check the user ID.");
            case 0x5D:
                throw new ProxyException("SOCKS4: The proxy did not accept the user ID (authentication failed).");
            default:
                throw new ProxyException("SOCKS4: Not a valid answer of a SOCKS4 proxy.");
        }
    }

    private static string FormatHost(string host)
    {
        return host.IndexOf(':') >= 0 ? "[" + host + "]" : host;
    }

    // A dual-stack socket, so that a proxy given by name or address works over IPv4 and IPv6.
    private static Socket ConnectSocket(string host, int port, TimeSpan timeout)
    {
        Socket socket;
        try
        {
            socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = true };
        }
        catch (SocketException)
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        }
        socket.NoDelay = true;
        try
        {
            IAsyncResult pending = socket.BeginConnect(host, port, null, null);
            if (!pending.AsyncWaitHandle.WaitOne(timeout))
            {
                throw new SshOperationTimeoutException("Connection to the proxy " + host + ":" + port + " timed out.");
            }
            socket.EndConnect(pending);
            return socket;
        }
        catch
        {
            socket.Close();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        try
        {
            _listener.Stop();
        }
        catch (Exception)
        {
        }
        try
        {
            _localSocket?.Close();
        }
        catch (Exception)
        {
        }
        try
        {
            _proxyStream?.Dispose();
        }
        catch (Exception)
        {
        }
    }
}
