using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace SmarTTY.Ssh.Tests;

/// <summary>
/// Checks X11 forwarding (Ssh/X11Forwarding.cs): the parsing of the X11 connection setup with the fake cookie always runs;
/// the live checks open "x11" channels through a real OpenSSH server (setup-sshd.sh enables X11 forwarding on the main port
/// and disables it on a second one) and relay them to an in-process fake X server.
/// </summary>
internal static class X11ForwardingTests
{
    // Bytes the remote client sends after the setup and the fake X server sends back: larger than the 2 MiB window SmarTTY
    // grants and than OpenSSH's window, so both directions have to wait for window adjustments.
    private const int PayloadLength = 3 * 1024 * 1024;

    private const int ReplyLength = 4 * 1024 * 1024;

    // The remote X11 client: reads DISPLAY and the fake cookie that sshd stored with xauth, connects to the display, sends the
    // connection setup (with the right or an all-zero cookie) and the payload, then reports what came back.
    private const string RemoteClient = @"
import hashlib, os, socket, subprocess, sys
mode = sys.argv[1]
display = os.environ.get('DISPLAY', '')
print('DISPLAY=' + display)
host, _, number = display.rpartition(':')
number = int(number.split('.')[0])
entry = subprocess.run(['xauth', 'list', display], capture_output=True, text=True).stdout.split()
cookie = bytes.fromhex(entry[2]) if mode == 'good' else bytes(16)
name = b'MIT-MAGIC-COOKIE-1'
pad = lambda data: data + b'\0' * (-len(data) % 4)
setup = b'l\0' + (11).to_bytes(2, 'little') + (0).to_bytes(2, 'little') + len(name).to_bytes(2, 'little') + len(cookie).to_bytes(2, 'little') + b'\0\0' + pad(name) + pad(cookie)
payload = bytes(range(256)) * (3 * 1024 * 1024 // 256)
s = socket.create_connection((host or 'localhost', 6000 + number), timeout=60)
try:
    s.sendall(setup + payload)
    s.shutdown(socket.SHUT_WR)
except OSError:
    pass
received = bytearray()
try:
    while True:
        chunk = s.recv(65536)
        if not chunk:
            break
        received += chunk
except OSError:
    pass
print('RECEIVED=%d' % len(received))
print('SHA256=' + hashlib.sha256(received).hexdigest())
";

    private sealed class FakeXServer : IDisposable
    {
        private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);

        private readonly List<Thread> _threads = new List<Thread>();

        public readonly List<byte[]> Received = new List<byte[]>();

        public int ConnectCount;

        public FakeXServer()
        {
            _listener.Start();
        }

        public Socket Connect()
        {
            Interlocked.Increment(ref ConnectCount);
            TcpClient client = new TcpClient();
            client.Connect((IPEndPoint)_listener.LocalEndpoint);
            Socket server = _listener.AcceptSocket();
            Thread thread = new Thread(() => Serve(server)) { IsBackground = true };
            lock (_threads)
            {
                _threads.Add(thread);
            }
            thread.Start();
            return client.Client;
        }

        // Reads the whole client stream, then answers with the reply bytes (only if anything arrived) and closes.
        private void Serve(Socket socket)
        {
            MemoryStream received = new MemoryStream();
            byte[] buffer = new byte[65536];
            try
            {
                socket.ReceiveTimeout = 60000;
                int read;
                while ((read = socket.Receive(buffer)) > 0)
                {
                    received.Write(buffer, 0, read);
                }
                if (received.Length > 0)
                {
                    byte[] reply = Reply();
                    int offset = 0;
                    while (offset < reply.Length)
                    {
                        offset += socket.Send(reply, offset, Math.Min(65536, reply.Length - offset), SocketFlags.None);
                    }
                }
            }
            catch
            {
            }
            lock (Received)
            {
                Received.Add(received.ToArray());
            }
            socket.Dispose();
        }

        public void WaitIdle()
        {
            Thread[] threads;
            lock (_threads)
            {
                threads = _threads.ToArray();
            }
            foreach (Thread thread in threads)
            {
                thread.Join(TimeSpan.FromSeconds(60));
            }
        }

        public void Dispose()
        {
            _listener.Stop();
        }
    }

    public static void Run()
    {
        ConnectionSetup();
        RemoteStatus();
    }

    public static void RunLive(TestEnvironment environment)
    {
        Forwarding(environment);
        Refusal(environment);
    }

    private static byte[] Cookie()
    {
        return Enumerable.Range(1, 16).Select((int value) => (byte)(value * 7)).ToArray();
    }

    private static byte[] Setup(bool bigEndian, string protocol, byte[] cookie, byte[] tail = null)
    {
        byte[] name = Encoding.ASCII.GetBytes(protocol);
        MemoryStream setup = new MemoryStream();
        setup.WriteByte(bigEndian ? (byte)'B' : (byte)'l');
        setup.WriteByte(0);
        WriteUInt16(setup, 11, bigEndian);
        WriteUInt16(setup, 0, bigEndian);
        WriteUInt16(setup, name.Length, bigEndian);
        WriteUInt16(setup, cookie.Length, bigEndian);
        WriteUInt16(setup, 0, bigEndian);
        setup.Write(name, 0, name.Length);
        setup.Write(new byte[(4 - name.Length % 4) % 4], 0, (4 - name.Length % 4) % 4);
        setup.Write(cookie, 0, cookie.Length);
        setup.Write(new byte[(4 - cookie.Length % 4) % 4], 0, (4 - cookie.Length % 4) % 4);
        if (tail != null)
        {
            setup.Write(tail, 0, tail.Length);
        }
        return setup.ToArray();
    }

    private static void WriteUInt16(Stream stream, int value, bool bigEndian)
    {
        stream.WriteByte(bigEndian ? (byte)(value >> 8) : (byte)value);
        stream.WriteByte(bigEndian ? (byte)value : (byte)(value >> 8));
    }

    private static void ConnectionSetup()
    {
        Test.Section("X11 connection setup with the fake cookie");
        byte[] tail = Encoding.ASCII.GetBytes("request");
        foreach (bool bigEndian in new[] { false, true })
        {
            string order = bigEndian ? "MSB first" : "LSB first";
            X11ConnectionSetup setup = new X11ConnectionSetup(Cookie());
            byte[] data = Setup(bigEndian, X11Forwarding.AuthenticationProtocol, Cookie(), tail);
            Test.Equal(X11ConnectionSetup.Result.Accepted, setup.Add(data, 0, data.Length), "the right cookie is accepted (" + order + ")");
            byte[] output = setup.Output;
            Test.Equal(12 + tail.Length, output.Length, "the cookie is removed and the following bytes are kept (" + order + ")");
            Test.Check(output.Take(6).SequenceEqual(data.Take(6)), "byte order and protocol version are passed on (" + order + ")");
            Test.Check(output.Skip(6).Take(4).All((byte value) => value == 0), "the authorization name and data lengths are zero (" + order + ")");
            Test.Equal("request", Encoding.ASCII.GetString(output, 12, tail.Length), "the bytes after the setup follow it (" + order + ")");
        }

        X11ConnectionSetup split = new X11ConnectionSetup(Cookie());
        byte[] whole = Setup(false, X11Forwarding.AuthenticationProtocol, Cookie());
        X11ConnectionSetup.Result result = X11ConnectionSetup.Result.NeedMoreData;
        for (int i = 0; i < whole.Length; i++)
        {
            result = split.Add(whole, i, 1);
            if (i < whole.Length - 1 && result != X11ConnectionSetup.Result.NeedMoreData)
            {
                break;
            }
        }
        Test.Equal(X11ConnectionSetup.Result.Accepted, result, "a setup that arrives one byte at a time is assembled");
        Test.Equal(12, split.Output.Length, "the assembled setup has no authorization left");

        byte[] wrongCookie = Cookie();
        wrongCookie[15] ^= 1;
        byte[] wrong = Setup(false, X11Forwarding.AuthenticationProtocol, wrongCookie);
        X11ConnectionSetup rejected = new X11ConnectionSetup(Cookie());
        Test.Equal(X11ConnectionSetup.Result.Rejected, rejected.Add(wrong, 0, wrong.Length), "a wrong cookie is rejected");
        Test.Equal(X11ConnectionSetup.Result.Rejected, rejected.Add(new byte[1], 0, 1), "a rejected setup stays rejected");

        byte[] shortCookie = Setup(false, X11Forwarding.AuthenticationProtocol, Cookie().Take(8).ToArray());
        Test.Equal(X11ConnectionSetup.Result.Rejected, new X11ConnectionSetup(Cookie()).Add(shortCookie, 0, shortCookie.Length), "a cookie of another length is rejected");

        byte[] otherProtocol = Setup(false, "XDM-AUTHORIZATION-1", Cookie());
        Test.Equal(X11ConnectionSetup.Result.Rejected, new X11ConnectionSetup(Cookie()).Add(otherProtocol, 0, otherProtocol.Length), "another authorization protocol is rejected");

        byte[] noAuth = Setup(false, "", new byte[0]);
        Test.Equal(X11ConnectionSetup.Result.Rejected, new X11ConnectionSetup(Cookie()).Add(noAuth, 0, noAuth.Length), "a setup without authorization is rejected");

        byte[] badOrder = Setup(false, X11Forwarding.AuthenticationProtocol, Cookie());
        badOrder[0] = (byte)'x';
        Test.Equal(X11ConnectionSetup.Result.Rejected, new X11ConnectionSetup(Cookie()).Add(badOrder, 0, badOrder.Length), "an invalid byte order mark is rejected");

        Test.Equal("00ff10", X11Forwarding.ToHex(new byte[] { 0, 255, 16 }), "the cookie is sent to the server as lowercase hex");
    }

    private static void RemoteStatus()
    {
        Test.Section("Remote X11 check");
        X11RemoteStatus status = X11RemoteStatus.Parse(new[] { "xauth=/usr/bin/xauth", "libX11=/usr/lib/x86_64-linux-gnu/libX11.so.6\r", "sshd=X11Forwarding no", "display=" });
        Test.Equal("/usr/bin/xauth", status.XauthPath, "the xauth path is parsed");
        Test.Equal("/usr/lib/x86_64-linux-gnu/libX11.so.6", status.LibX11Path, "the libX11 path is parsed");
        Test.Check(status.SshdDisablesX11, "\"X11Forwarding no\" in sshd_config is recognized");
        string text = status.DescribeRefusal("server");
        Test.Check(text.Contains("X11Forwarding yes") && !text.Contains("xauth program was not found"), "the explanation names the sshd setting, not xauth");

        X11RemoteStatus bare = X11RemoteStatus.Parse(new[] { "display=" });
        bare.ServerVersion = "dropbear_2022.83";
        Test.Check(!bare.HasXauth && !bare.HasLibX11 && bare.IsDropbear, "a host without xauth and libX11 is recognized");
        string bareText = bare.DescribeRefusal("router");
        Test.Check(bareText.Contains("xauth program was not found") && bareText.Contains("Dropbear") && bareText.Contains("libX11"), "the explanation names xauth, Dropbear and the missing library");
        Test.Check(!X11RemoteStatus.Parse(null).ProbeSucceeded, "a probe that could not run is reported as such");
    }

    private static SshSession Connect(TestEnvironment environment, int port)
    {
        SshConnectionParameters parameters = environment.Parameters();
        parameters.Port = port;
        SshSession session = new SshSession(parameters);
        if (!session.Authenticate(environment.User, environment.Password))
        {
            session.Dispose();
            throw new InvalidOperationException("Could not authenticate against the test server.");
        }
        return session;
    }

    private static string RunPty(SshSession session, string command)
    {
        using SshShellChannel channel = session.OpenShellChannel(command, "xterm", ExtendedDataMode.Merge);
        string output = new StreamReader(channel.Output).ReadToEnd();
        channel.WaitClosed();
        return output;
    }

    private static string RunClient(SshSession session, string mode)
    {
        return RunPty(session, "python3 - " + mode + " <<'SMARTTY_X11_EOF'\n" + RemoteClient + "\nSMARTTY_X11_EOF");
    }

    private static string Value(string output, string name)
    {
        foreach (string line in output.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith(name + "=", StringComparison.Ordinal))
            {
                return trimmed.Substring(name.Length + 1);
            }
        }
        return null;
    }

    private static byte[] Payload()
    {
        byte[] payload = new byte[PayloadLength];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)i;
        }
        return payload;
    }

    private static byte[] Reply()
    {
        byte[] reply = new byte[ReplyLength];
        for (int i = 0; i < reply.Length; i++)
        {
            reply[i] = (byte)(i * 31 + 7);
        }
        return reply;
    }

    private static void Forwarding(TestEnvironment environment)
    {
        Test.Section("X11 forwarding through OpenSSH");
        using SshSession session = Connect(environment, environment.Port);
        X11RemoteStatus remote = X11RemoteStatus.Check(session);
        bool hasPython = session.RunCommand("command -v python3", out int? pythonStatus) != null && pythonStatus == 0;
        if (!remote.HasXauth || !hasPython)
        {
            string reason = !remote.HasXauth ? "xauth" : "python3";
            if (Environment.GetEnvironmentVariable("SMARTTY_SSH_TEST_REQUIRED") == "1")
            {
                Test.Check(false, reason + " is installed on the test server (needed for the X11 forwarding checks)");
            }
            else
            {
                Console.WriteLine("SKIP: " + reason + " is not installed on the test server.");
            }
            return;
        }
        Test.Check(remote.Describe().Contains(remote.XauthPath), "the remote check reports where xauth is");

        using FakeXServer xServer = new FakeXServer();
        X11Forwarding x11 = session.EnableX11Forwarding(xServer.Connect);
        Test.Check(ReferenceEquals(x11, session.X11), "the session keeps its X11 forwarding");
        Test.Check(!x11.IsAccepted && !x11.IsRefused, "nothing is requested before a PTY channel is opened");

        string display = Value(RunPty(session, "echo DISPLAY=$DISPLAY"), "DISPLAY");
        Test.Check(!string.IsNullOrEmpty(display) && display.Contains(":"), "a PTY command gets DISPLAY from the server (actual: " + display + ")");
        Test.Check(x11.IsAccepted, "the server accepted the X11 forwarding request");
        string plainDisplay = Value(string.Join("\n", session.RunCommand("echo DISPLAY=$DISPLAY", out int? _)), "DISPLAY");
        Test.Check(string.IsNullOrEmpty(plainDisplay), "a helper command without a PTY gets no DISPLAY");

        string output = RunClient(session, "good");
        xServer.WaitIdle();
        Test.Equal(1, xServer.ConnectCount, "the remote X11 connection reached the local X server");
        byte[] received = xServer.Received.FirstOrDefault() ?? new byte[0];
        Test.Equal(12 + PayloadLength, received.Length, "the setup without the cookie and the whole payload arrived");
        Test.Check(received.Length >= 12 && received[0] == (byte)'l' && received.Skip(6).Take(4).All((byte value) => value == 0), "the X server got the setup without authorization data");
        Test.Check(received.Length == 12 + PayloadLength && received.Skip(12).SequenceEqual(Payload()), "the payload arrived unchanged (window adjustments towards the X server)");
        Test.Equal(ReplyLength.ToString(), Value(output, "RECEIVED"), "the remote client received the whole reply");
        Test.Equal(Convert.ToHexString(SHA256.HashData(Reply())).ToLowerInvariant(), Value(output, "SHA256"), "the reply arrived unchanged (waiting for the server's window)");
        Test.Equal(1, x11.ConnectionCount, "one X11 connection was counted");
        Test.WaitUntil(() => x11.ActiveConnectionCount == 0, TimeSpan.FromSeconds(10), "the finished X11 channel is released");

        string wrongOutput = RunClient(session, "bad");
        xServer.WaitIdle();
        Test.Equal("0", Value(wrongOutput, "RECEIVED"), "a connection with a wrong cookie is closed without an answer");
        Test.Check(xServer.Received.Count == 2 && xServer.Received[1].Length == 0, "nothing of a connection with a wrong cookie reaches the X server");
        Test.WaitUntil(() => x11.ActiveConnectionCount == 0, TimeSpan.FromSeconds(10), "the rejected X11 channel is released");

        Test.Check(RunPty(session, "echo still-working").Contains("still-working"), "the session keeps working after the X11 connections");
    }

    private static void Refusal(TestEnvironment environment)
    {
        Test.Section("X11 forwarding refused by the server");
        if (environment.NoX11Port == 0)
        {
            Console.WriteLine("SKIP: setup-sshd.sh did not provide a port without X11 forwarding.");
            return;
        }
        using SshSession session = Connect(environment, environment.NoX11Port);
        using ManualResetEvent refused = new ManualResetEvent(false);
        X11Forwarding x11 = session.EnableX11Forwarding(() => null);
        x11.Refused += (object sender, EventArgs e) => refused.Set();
        string output = RunPty(session, "echo DISPLAY=$DISPLAY; echo done");
        Test.Check(output.Contains("done"), "the command runs although X11 forwarding was refused");
        Test.Check(string.IsNullOrEmpty(Value(output, "DISPLAY")), "no DISPLAY is set when the server refuses X11 forwarding");
        Test.Check(refused.WaitOne(TimeSpan.FromSeconds(10)), "the refusal is reported");
        Test.Check(x11.IsRefused && !x11.IsAccepted, "the forwarding is marked as refused");
        Test.Check(RunPty(session, "echo again").Contains("again"), "later PTY channels open without asking again");
        X11RemoteStatus status = X11RemoteStatus.Check(session);
        Test.Check(status.ProbeSucceeded, "the remote X11 check runs");
        Test.Check(status.ServerVersion != null && status.ServerVersion.Contains("OpenSSH"), "the server version is known (actual: " + status.ServerVersion + ")");
        Test.Check(!string.IsNullOrEmpty(status.DescribeRefusal(environment.Host)), "the refusal is explained");
    }
}
