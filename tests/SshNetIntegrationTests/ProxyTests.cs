using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Renci.SshNet.Common;

namespace SmarTTY.Ssh.Tests;

/// <summary>
/// SSH connections through proxies: SOCKS5 (the local inbound of xray, v2ray, sing-box), SOCKS4/4a and HTTPS (HTTP
/// CONNECT over TLS). A small proxy server runs in the test, with and without credentials, and passes the connection on
/// to the live server. It checks that the server name reaches the proxy as a name (resolved there), that a refused login
/// of the proxy is recognized as one, so SmarTTY asks for the proxy credentials, and that the certificate of an HTTPS
/// proxy is checked. SOCKS4 and HTTPS go through Ssh/ProxyRelay.cs; SOCKS5 and HTTP are SSH.NET's own.
/// </summary>
internal static class ProxyTests
{
    private enum Kind
    {
        Socks5,
        Socks4,
        Https
    }

    public static void Run()
    {
        Test.Section("Proxy credential errors");
        Test.Check(SshSession.IsProxyAuthenticationFailure(new ProxyException("HTTP: Status code 407, \"Proxy Authentication Required\"")), "an HTTP 407 answer asks for the proxy credentials");
        Test.Check(SshSession.IsProxyAuthenticationFailure(new ProxyException("SOCKS5: Username/Password authentication failed.")), "a SOCKS5 login failure asks for the proxy credentials");
        Test.Check(SshSession.IsProxyAuthenticationFailure(new ProxyException("SOCKS5: No acceptable authentication methods were offered.")), "a SOCKS5 proxy that wants a login asks for the proxy credentials");
        Test.Check(!SshSession.IsProxyAuthenticationFailure(new ProxyException("SOCKS5: Connection refused by destination host.")), "a refused destination is not a credential problem");
        Test.Check(!SshSession.IsProxyAuthenticationFailure(new IOException("407")), "only proxy errors count");
    }

    public static void RunLive(TestEnvironment environment)
    {
        Socks5(environment);
        Socks4(environment);
        Https(environment);
    }

    private static void Socks5(TestEnvironment environment)
    {
        Test.Section("SSH through a SOCKS5 proxy");
        using (TestProxyServer proxy = new TestProxyServer(Kind.Socks5, null, null))
        {
            SshConnectionParameters parameters = Through(environment, proxy, SshProxyType.Socks5, null, null);
            // A host name, not an address: the proxy has to get the name and resolve it itself.
            parameters.Host = "localhost";
            using (SshSession session = new SshSession(parameters))
            {
                Test.Check(session.Authenticate(environment.User, environment.Password), "a SOCKS5 proxy without a login carries the SSH connection");
                Test.Check(session.RunCommandChecked("echo through-socks", requireSuccess: true)?.Contains("through-socks") == true, "commands run over the proxied connection");
            }
            Test.Equal("localhost:" + environment.Port, proxy.LastDestination, "the server name is passed to the proxy, which resolves it (socks5h)");
        }

        using (TestProxyServer proxy = new TestProxyServer(Kind.Socks5, "proxy-user", "proxy-secret"))
        {
            using (SshSession session = new SshSession(Through(environment, proxy, SshProxyType.Socks5, "proxy-user", "proxy-secret")))
            {
                Test.Check(session.Authenticate(environment.User, environment.Password), "a SOCKS5 proxy with a user name and password carries the SSH connection");
            }
            ExpectCredentialFailure(Through(environment, proxy, SshProxyType.Socks5, "proxy-user", "wrong"), "SOCKS5: a wrong proxy password");
            ExpectCredentialFailure(Through(environment, proxy, SshProxyType.Socks5, null, null), "SOCKS5: missing proxy credentials");
        }
    }

    private static void Socks4(TestEnvironment environment)
    {
        Test.Section("SSH through a SOCKS4/4a proxy");
        using (TestProxyServer proxy = new TestProxyServer(Kind.Socks4, null, null))
        {
            SshConnectionParameters byName = Through(environment, proxy, SshProxyType.Socks4, null, null);
            byName.Host = "localhost";
            using (SshSession session = new SshSession(byName))
            {
                Test.Check(session.Authenticate(environment.User, environment.Password), "SOCKS4a carries the SSH connection to a host name");
                // Enough data to fill the relay buffers many times, in the direction of the client.
                List<string> output = session.RunCommandChecked("head -c 600000 /dev/zero | base64 -w 0 | wc -c", requireSuccess: true);
                Test.Equal("800000", output?.FirstOrDefault()?.Trim(), "bulk data passes the proxy relay");
            }
            Test.Equal("localhost:" + environment.Port, proxy.LastDestination, "SOCKS4a passes the server name to the proxy");

            using (SshSession session = new SshSession(Through(environment, proxy, SshProxyType.Socks4, null, null)))
            {
                Test.Check(session.Authenticate(environment.User, environment.Password), "plain SOCKS4 carries the SSH connection to an IPv4 address");
            }
            Test.Equal("127.0.0.1:" + environment.Port, proxy.LastDestination, "an IPv4 address is sent as plain SOCKS4");

            SshConnectionParameters ipv6 = Through(environment, proxy, SshProxyType.Socks4, null, null);
            ipv6.Host = "::1";
            Test.Throws<ProxyException>(() => new SshSession(ipv6).Dispose(), "SOCKS4 refuses an IPv6 address with a message instead of sending a wrong request");
        }

        using (TestProxyServer proxy = new TestProxyServer(Kind.Socks4, "proxy-user", null))
        {
            using (SshSession session = new SshSession(Through(environment, proxy, SshProxyType.Socks4, "proxy-user", null)))
            {
                Test.Check(session.Authenticate(environment.User, environment.Password), "SOCKS4 sends the user name as its user ID");
            }
            ExpectCredentialFailure(Through(environment, proxy, SshProxyType.Socks4, "someone-else", null), "SOCKS4: a user ID the proxy rejects");
        }
    }

    private static void Https(TestEnvironment environment)
    {
        Test.Section("SSH through an HTTPS proxy (HTTP CONNECT over TLS)");
        using (TestProxyServer proxy = new TestProxyServer(Kind.Https, null, null))
        {
            SshConnectionParameters trusted = Through(environment, proxy, SshProxyType.Https, null, null);
            trusted.Host = "localhost";
            trusted.ProxyCertificateValidation = proxy.TrustTestCertificate;
            using (SshSession session = new SshSession(trusted))
            {
                Test.Check(session.Authenticate(environment.User, environment.Password), "an HTTPS proxy carries the SSH connection");
                Test.Check(session.RunCommandChecked("echo through-https", requireSuccess: true)?.Contains("through-https") == true, "commands run over the TLS tunnel");
            }
            Test.Equal("localhost:" + environment.Port, proxy.LastDestination, "the server name is passed in the CONNECT request");

            // The usual check: the self-signed certificate of the test proxy is not trusted by the system.
            Test.Throws<ProxyException>(() => new SshSession(Through(environment, proxy, SshProxyType.Https, null, null)).Dispose(), "an untrusted proxy certificate stops the connection",
                exception => Test.Check(exception.Message.Contains("TLS"), "the message says that the TLS connection to the proxy failed (" + exception.Message + ")"));
        }

        using (TestProxyServer proxy = new TestProxyServer(Kind.Https, "proxy-user", "proxy-secret"))
        {
            SshConnectionParameters withLogin = Through(environment, proxy, SshProxyType.Https, "proxy-user", "proxy-secret");
            withLogin.ProxyCertificateValidation = proxy.TrustTestCertificate;
            using (SshSession session = new SshSession(withLogin))
            {
                Test.Check(session.Authenticate(environment.User, environment.Password), "an HTTPS proxy accepts the Basic credentials");
            }
            SshConnectionParameters withoutLogin = Through(environment, proxy, SshProxyType.Https, null, null);
            withoutLogin.ProxyCertificateValidation = proxy.TrustTestCertificate;
            ExpectCredentialFailure(withoutLogin, "HTTPS: missing proxy credentials (407)");
        }
    }

    private static void ExpectCredentialFailure(SshConnectionParameters parameters, string name)
    {
        Test.Throws<ProxyException>(() => new SshSession(parameters).Dispose(), name + " is refused",
            exception => Test.Check(SshSession.IsProxyAuthenticationFailure(exception), name + " is recognized, so SmarTTY asks for the credentials (" + exception.Message + ")"));
    }

    private static SshConnectionParameters Through(TestEnvironment environment, TestProxyServer proxy, SshProxyType proxyType, string userName, string password)
    {
        SshConnectionParameters parameters = environment.Parameters();
        parameters.ProxyType = proxyType;
        parameters.ProxyHost = "localhost";
        parameters.ProxyPort = proxy.Port;
        parameters.ProxyUserName = userName;
        parameters.ProxyPassword = password;
        return parameters;
    }

    /// <summary>A minimal proxy server (CONNECT only) that relays to the requested destination.</summary>
    private sealed class TestProxyServer : IDisposable
    {
        private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);

        private readonly Kind _kind;

        private readonly string _userName;

        private readonly string _password;

        private readonly X509Certificate2 _certificate;

        private readonly List<IDisposable> _connections = new List<IDisposable>();

        public string LastDestination;

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public TestProxyServer(Kind kind, string userName, string password)
        {
            _kind = kind;
            _userName = userName;
            _password = password;
            if (kind == Kind.Https)
            {
                using RSA key = RSA.Create(2048);
                CertificateRequest request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using X509Certificate2 selfSigned = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
                // Through PKCS#12, so that the TLS server can use the private key on every platform.
                _certificate = new X509Certificate2(selfSigned.Export(X509ContentType.Pkcs12));
            }
            _listener.Start();
            Task.Run(AcceptLoop);
        }

        /// <summary>Trusts exactly the certificate of this test proxy.</summary>
        public bool TrustTestCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors)
        {
            return certificate != null && certificate.GetCertHashString() == _certificate.GetCertHashString();
        }

        private async Task AcceptLoop()
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync();
                }
                catch (Exception)
                {
                    return;
                }
                Track(client);
                _ = Task.Run(() => Serve(client));
            }
        }

        private void Track(IDisposable connection)
        {
            lock (_connections)
            {
                _connections.Add(connection);
            }
        }

        private void Serve(TcpClient client)
        {
            try
            {
                Stream stream = client.GetStream();
                string destination = _kind switch
                {
                    Kind.Socks5 => Socks5Handshake(stream),
                    Kind.Socks4 => Socks4Handshake(stream),
                    _ => HttpsHandshake(ref stream),
                };
                if (destination == null)
                {
                    return;
                }
                LastDestination = destination;
                int colon = destination.LastIndexOf(':');
                string host = destination.Substring(0, colon).Trim('[', ']');
                TcpClient target = new TcpClient();
                target.Connect(host == "localhost" ? "127.0.0.1" : host, int.Parse(destination.Substring(colon + 1), CultureInfo.InvariantCulture));
                Track(target);
                ConfirmConnected(stream);
                NetworkStream targetStream = target.GetStream();
                Task.Run(() => Pump(targetStream, stream, client));
                Pump(stream, targetStream, target);
            }
            catch (Exception)
            {
                client.Close();
            }
        }

        private string Socks5Handshake(Stream stream)
        {
            byte[] greeting = Read(stream, 2);
            byte[] methods = Read(stream, greeting[1]);
            byte wanted = (byte)(_userName == null ? 0x00 : 0x02);
            if (Array.IndexOf(methods, wanted) < 0)
            {
                stream.Write(new byte[] { 5, 0xFF });
                return null;
            }
            stream.Write(new byte[] { 5, wanted });
            if (wanted == 0x02)
            {
                Read(stream, 1);
                string user = Encoding.UTF8.GetString(Read(stream, Read(stream, 1)[0]));
                string password = Encoding.UTF8.GetString(Read(stream, Read(stream, 1)[0]));
                bool accepted = user == _userName && password == _password;
                stream.Write(new byte[] { 1, (byte)(accepted ? 0 : 1) });
                if (!accepted)
                {
                    return null;
                }
            }
            byte[] request = Read(stream, 4);
            string host = request[3] switch
            {
                1 => new IPAddress(Read(stream, 4)).ToString(),
                3 => Encoding.ASCII.GetString(Read(stream, Read(stream, 1)[0])),
                _ => "[" + new IPAddress(Read(stream, 16)) + "]",
            };
            byte[] port = Read(stream, 2);
            return host + ":" + ((port[0] << 8) | port[1]);
        }

        private string Socks4Handshake(Stream stream)
        {
            byte[] request = Read(stream, 8);
            string userId = ReadNullTerminated(stream);
            string host = new IPAddress(new[] { request[4], request[5], request[6], request[7] }).ToString();
            if (request[4] == 0 && request[5] == 0 && request[6] == 0 && request[7] != 0)
            {
                // SOCKS4a: the host name follows the user ID.
                host = ReadNullTerminated(stream);
            }
            if (_userName != null && userId != _userName)
            {
                stream.Write(new byte[] { 0, 0x5D, 0, 0, 0, 0, 0, 0 });
                return null;
            }
            return host + ":" + ((request[2] << 8) | request[3]);
        }

        private string HttpsHandshake(ref Stream stream)
        {
            SslStream tls = new SslStream(stream, leaveInnerStreamOpen: false);
            tls.AuthenticateAsServer(_certificate);
            stream = tls;
            List<string> lines = new List<string>();
            string line;
            while ((line = ReadLine(tls)) != "")
            {
                lines.Add(line);
            }
            string[] parts = lines[0].Split(' ');
            if (parts[0] != "CONNECT")
            {
                Write(tls, "HTTP/1.1 405 Method Not Allowed\r\n\r\n");
                return null;
            }
            if (_userName != null)
            {
                string expected = "Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(_userName + ":" + _password));
                if (!lines.Contains(expected))
                {
                    Write(tls, "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"test\"\r\nContent-Length: 0\r\n\r\n");
                    return null;
                }
            }
            return parts[1];
        }

        private void ConfirmConnected(Stream stream)
        {
            switch (_kind)
            {
                case Kind.Socks5:
                    stream.Write(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 });
                    break;
                case Kind.Socks4:
                    stream.Write(new byte[] { 0, 0x5A, 0, 0, 0, 0, 0, 0 });
                    break;
                default:
                    Write(stream, "HTTP/1.1 200 Connection established\r\nProxy-Agent: smartty-test\r\n\r\n");
                    break;
            }
        }

        private static void Write(Stream stream, string text)
        {
            byte[] data = Encoding.ASCII.GetBytes(text);
            stream.Write(data, 0, data.Length);
            stream.Flush();
        }

        private static string ReadLine(Stream stream)
        {
            StringBuilder line = new StringBuilder();
            int value;
            while ((value = stream.ReadByte()) != '\n')
            {
                if (value < 0)
                {
                    throw new EndOfStreamException();
                }
                line.Append((char)value);
            }
            return line.ToString().TrimEnd('\r');
        }

        private static string ReadNullTerminated(Stream stream)
        {
            StringBuilder text = new StringBuilder();
            int value;
            while ((value = stream.ReadByte()) != 0)
            {
                if (value < 0)
                {
                    throw new EndOfStreamException();
                }
                text.Append((char)value);
            }
            return text.ToString();
        }

        private static void Pump(Stream from, Stream to, TcpClient closeWhenDone)
        {
            try
            {
                from.CopyTo(to);
            }
            catch (Exception)
            {
            }
            closeWhenDone.Close();
        }

        private static byte[] Read(Stream stream, int count)
        {
            byte[] data = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(data, offset, count - offset);
                if (read == 0)
                {
                    throw new EndOfStreamException();
                }
                offset += read;
            }
            return data;
        }

        public void Dispose()
        {
            _listener.Stop();
            lock (_connections)
            {
                foreach (IDisposable connection in _connections)
                {
                    connection.Dispose();
                }
            }
            _certificate?.Dispose();
        }
    }
}
