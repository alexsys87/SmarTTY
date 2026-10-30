using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet.Common;

namespace SmarTTY.Ssh.Tests;

/// <summary>
/// SSH connections through a SOCKS5 proxy (the local inbound of xray, v2ray, sing-box): a small SOCKS5 server runs in the
/// test, with and without a user name and password (RFC 1929), and passes the connection on to the live server. It checks
/// that the server name reaches the proxy as a name (resolved there, as socks5h://) and that a refused login of the proxy
/// is recognized as one, so SmarTTY asks for the proxy credentials.
/// </summary>
internal static class Socks5ProxyTests
{
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
        Test.Section("SSH through a SOCKS5 proxy");
        using (TestSocks5Server proxy = new TestSocks5Server(null, null))
        {
            SshConnectionParameters parameters = Through(environment, proxy, null, null);
            // A host name, not an address: the proxy has to get the name and resolve it itself.
            parameters.Host = "localhost";
            using (SshSession session = new SshSession(parameters))
            {
                Test.Check(session.Authenticate(environment.User, environment.Password), "a SOCKS5 proxy without a login carries the SSH connection");
                Test.Check(session.RunCommandChecked("echo through-socks", requireSuccess: true)?.Contains("through-socks") == true, "commands run over the proxied connection");
            }
            Test.Equal("localhost:" + environment.Port, proxy.LastDestination, "the server name is passed to the proxy, which resolves it (socks5h)");
        }

        using (TestSocks5Server proxy = new TestSocks5Server("proxy-user", "proxy-secret"))
        {
            using (SshSession session = new SshSession(Through(environment, proxy, "proxy-user", "proxy-secret")))
            {
                Test.Check(session.Authenticate(environment.User, environment.Password), "a SOCKS5 proxy with a user name and password carries the SSH connection");
            }
            Test.Throws<ProxyException>(() => new SshSession(Through(environment, proxy, "proxy-user", "wrong")).Dispose(), "a wrong proxy password is refused",
                exception => Test.Check(SshSession.IsProxyAuthenticationFailure(exception), "a wrong proxy password is recognized, so SmarTTY asks for the credentials (" + exception.Message + ")"));
            Test.Throws<ProxyException>(() => new SshSession(Through(environment, proxy, null, null)).Dispose(), "a proxy that needs a login refuses a connection without one",
                exception => Test.Check(SshSession.IsProxyAuthenticationFailure(exception), "missing proxy credentials are recognized, so SmarTTY asks for them (" + exception.Message + ")"));
        }
    }

    private static SshConnectionParameters Through(TestEnvironment environment, TestSocks5Server proxy, string userName, string password)
    {
        SshConnectionParameters parameters = environment.Parameters();
        parameters.ProxyType = SshProxyType.Socks5;
        parameters.ProxyHost = "127.0.0.1";
        parameters.ProxyPort = proxy.Port;
        parameters.ProxyUserName = userName;
        parameters.ProxyPassword = password;
        return parameters;
    }

    /// <summary>A minimal SOCKS5 server (CONNECT only) that relays to the requested destination.</summary>
    private sealed class TestSocks5Server : IDisposable
    {
        private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);

        private readonly string _userName;

        private readonly string _password;

        private readonly List<TcpClient> _clients = new List<TcpClient>();

        public string LastDestination;

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public TestSocks5Server(string userName, string password)
        {
            _userName = userName;
            _password = password;
            _listener.Start();
            Task.Run(AcceptLoop);
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
                lock (_clients)
                {
                    _clients.Add(client);
                }
                _ = Task.Run(() => Serve(client));
            }
        }

        private void Serve(TcpClient client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                byte[] greeting = Read(stream, 2);
                byte[] methods = Read(stream, greeting[1]);
                byte wanted = (byte)(_userName == null ? 0x00 : 0x02);
                if (Array.IndexOf(methods, wanted) < 0)
                {
                    stream.Write(new byte[] { 5, 0xFF });
                    return;
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
                        return;
                    }
                }
                byte[] request = Read(stream, 4);
                string host;
                switch (request[3])
                {
                    case 1:
                        host = new IPAddress(Read(stream, 4)).ToString();
                        break;
                    case 3:
                        host = Encoding.ASCII.GetString(Read(stream, Read(stream, 1)[0]));
                        break;
                    default:
                        host = new IPAddress(Read(stream, 16)).ToString();
                        break;
                }
                byte[] portBytes = Read(stream, 2);
                int port = (portBytes[0] << 8) | portBytes[1];
                LastDestination = host + ":" + port;
                TcpClient target = new TcpClient();
                target.Connect(host == "localhost" ? "127.0.0.1" : host, port);
                lock (_clients)
                {
                    _clients.Add(target);
                }
                stream.Write(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 });
                NetworkStream targetStream = target.GetStream();
                Task.Run(() => Pump(targetStream, stream, client));
                Pump(stream, targetStream, target);
            }
            catch (Exception)
            {
                client.Close();
            }
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
            lock (_clients)
            {
                foreach (TcpClient client in _clients)
                {
                    client.Close();
                }
            }
        }
    }
}
