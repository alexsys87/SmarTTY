using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SmarTTY.Transport;

// Telnet over TLS against a server on the loopback interface: a TLS server with a self-signed certificate, and a plain
// server that does not speak TLS at all.
internal static class TlsChecks
{
    private static X509Certificate2 CreateCertificate()
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        SubjectAlternativeNameBuilder names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return new X509Certificate2(certificate.Export(X509ContentType.Pfx));
    }

    public static void Run(Action<bool, string> check)
    {
        using X509Certificate2 certificate = CreateCertificate();

        // A TLS server: sends "IAC DO NAWS" and a prompt, then keeps what the client sends.
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        byte[] received = new byte[0];
        Task server = Task.Run(() =>
        {
            for (int connection = 0; connection < 2; connection++)
            {
                using TcpClient client = listener.AcceptTcpClient();
                try
                {
                    using SslStream ssl = new SslStream(client.GetStream(), false);
                    ssl.AuthenticateAsServer(certificate);
                    byte[] hello = Concat(new byte[] { 255, 253, 31 }, Encoding.ASCII.GetBytes("login: "));
                    ssl.Write(hello, 0, hello.Length);
                    ssl.Flush();
                    byte[] buffer = new byte[256];
                    int count = ssl.Read(buffer, 0, buffer.Length);
                    received = Slice(buffer, count);
                }
                catch (Exception)
                {
                    // The client refused the certificate: nothing to do on this side.
                }
            }
        });

        // 1. Any certificate accepted: the Telnet layer works inside the encrypted stream.
        using (TcpClient client = new TcpClient())
        {
            client.Connect(IPAddress.Loopback, port);
            Stream wire = TelnetTls.Authenticate(client.GetStream(), "localhost", allowInvalidCertificate: true);
            check(wire is SslStream && ((SslStream)wire).IsEncrypted, "tls: the stream is encrypted after the handshake");
            using TelnetStream telnet = new TelnetStream(wire, new TelnetOptions { Columns = 100, Rows = 30 });
            byte[] buffer = new byte[64];
            int count = telnet.Read(buffer, 0, buffer.Length);
            check(Encoding.ASCII.GetString(buffer, 0, count) == "login: ", "tls: Telnet commands are removed from the decrypted data");
            Thread.Sleep(100);
            // the NAWS answer travelled back through TLS
            check(WaitFor(() => received.Length >= 3 && received[0] == 255 && received[1] == 251 && received[2] == 31), "tls: the server received the answer to its negotiation, decrypted");
        }

        // 2. The default is to check the certificate: a self-signed one is rejected.
        using (TcpClient client = new TcpClient())
        {
            client.Connect(IPAddress.Loopback, port);
            string message = null;
            try
            {
                TelnetTls.Authenticate(client.GetStream(), "localhost", allowInvalidCertificate: false).Dispose();
            }
            catch (IOException exception)
            {
                message = exception.Message;
            }
            check(message != null && message.Contains("TLS handshake") && message.Contains("localhost"), "tls: a self-signed certificate is rejected unless it is accepted explicitly");
        }
        server.Wait(5000);
        listener.Stop();

        // 3. A server that does not speak TLS (its banner is plain text) fails at once, not after a timeout.
        TcpListener plainListener = new TcpListener(IPAddress.Loopback, 0);
        plainListener.Start();
        int plainPort = ((IPEndPoint)plainListener.LocalEndpoint).Port;
        Task plainServer = Task.Run(() =>
        {
            using TcpClient client = plainListener.AcceptTcpClient();
            byte[] banner = Encoding.ASCII.GetBytes("Welcome to a plain telnet server\r\nlogin: \r\n\r\n\r\n\r\n\r\n\r\n\r\n\r\n\r\n\r\n\r\n\r\n\r\n\r\n\r\n\r\n\r\n");
            client.GetStream().Write(banner, 0, banner.Length);
            Thread.Sleep(3000);
        });
        using (TcpClient client = new TcpClient())
        {
            client.Connect(IPAddress.Loopback, plainPort);
            DateTime started = DateTime.UtcNow;
            bool failed = false;
            try
            {
                TelnetTls.Authenticate(client.GetStream(), "localhost", allowInvalidCertificate: true).Dispose();
            }
            catch (IOException)
            {
                failed = true;
            }
            check(failed && DateTime.UtcNow - started < TimeSpan.FromSeconds(5), "tls: a server that does not speak TLS makes the handshake fail quickly");
        }
        plainServer.Wait(6000);
        plainListener.Stop();

        // 4. A server that accepts the connection and says nothing: the handshake gives up after the timeout.
        TcpListener silentListener = new TcpListener(IPAddress.Loopback, 0);
        silentListener.Start();
        int silentPort = ((IPEndPoint)silentListener.LocalEndpoint).Port;
        Task silentServer = Task.Run(() =>
        {
            using TcpClient client = silentListener.AcceptTcpClient();
            Thread.Sleep(3000);
        });
        using (TcpClient client = new TcpClient())
        {
            client.Connect(IPAddress.Loopback, silentPort);
            DateTime started = DateTime.UtcNow;
            bool failed = false;
            try
            {
                TelnetTls.Authenticate(client.GetStream(), "localhost", allowInvalidCertificate: true, handshakeTimeoutMilliseconds: 500).Dispose();
            }
            catch (IOException)
            {
                failed = true;
            }
            TimeSpan waited = DateTime.UtcNow - started;
            check(failed && waited < TimeSpan.FromSeconds(2.5), "tls: a silent server does not block the handshake for ever");
        }
        silentServer.Wait(6000);
        silentListener.Stop();

        bool threw = false;
        try
        {
            TelnetTls.Authenticate(new MemoryStream(), "", false);
        }
        catch (ArgumentException)
        {
            threw = true;
        }
        check(threw, "tls: a host name is required to check the certificate");
    }

    private static bool WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 50; i++)
        {
            if (condition())
            {
                return true;
            }
            Thread.Sleep(100);
        }
        return false;
    }

    private static byte[] Concat(byte[] first, byte[] second)
    {
        byte[] result = new byte[first.Length + second.Length];
        Buffer.BlockCopy(first, 0, result, 0, first.Length);
        Buffer.BlockCopy(second, 0, result, first.Length, second.Length);
        return result;
    }

    private static byte[] Slice(byte[] data, int count)
    {
        byte[] result = new byte[count];
        Buffer.BlockCopy(data, 0, result, 0, count);
        return result;
    }
}
