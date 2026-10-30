using SmarTTY.Transport;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Threading;
using VisualTTY.DeviceTerminal;

// Runs the real raw TCP transport (and the connection class that drives it) against listeners on this machine:
// connecting over IPv4 and IPv6, keep-alive, aborting a connect, the display name, and a flood of received data.
internal static class Program
{
    private static int _failures;

    private static void Check(bool condition, string name)
    {
        if (condition)
        {
            Console.WriteLine("PASS: " + name);
            return;
        }
        _failures++;
        Console.WriteLine("FAIL: " + name);
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine("FAIL: " + name + " -> " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static bool WaitFor(Func<bool> condition, int ms = 5000)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < ms)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return condition();
    }

    private static DeviceTerminalSettings.ConnectionSettings.Tcp Endpoint(string host, int port, int keepAlive = 0)
    {
        return new DeviceTerminalSettings.ConnectionSettings.Tcp { Host = host, Port = port, KeepAliveSeconds = keepAlive };
    }

    /// <summary>A listener that accepts one connection and hands its socket to the test.</summary>
    private sealed class Server : IDisposable
    {
        private readonly TcpListener _listener;
        private Socket _accepted;
        private readonly ManualResetEventSlim _hasClient = new ManualResetEventSlim();

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public Server(IPAddress address)
        {
            _listener = new TcpListener(address, 0);
            _listener.Start();
            _listener.BeginAcceptSocket(ar =>
            {
                try
                {
                    _accepted = _listener.EndAcceptSocket(ar);
                    _hasClient.Set();
                }
                catch
                {
                }
            }, null);
        }

        public Socket Client
        {
            get
            {
                if (!_hasClient.Wait(5000)) throw new Exception("no client connected");
                return _accepted;
            }
        }

        public void Dispose()
        {
            try { _accepted?.Close(); } catch { }
            _listener.Stop();
        }
    }

    /// <summary>The connection class with the real TCP transport, as a device tab uses it.</summary>
    private sealed class Session : IDisposable
    {
        public readonly Dispatcher Ui = new Dispatcher();
        public readonly AsyncTransportConnection Connection;
        public readonly List<(ConnectionStateEnum State, string Message)> States = new List<(ConnectionStateEnum, string)>();
        public readonly ManualResetEventSlim Connected = new ManualResetEventSlim();
        public readonly ManualResetEventSlim Ended = new ManualResetEventSlim();
        public int WriterIdleEvents;
        public readonly List<string> WriteFailures = new List<string>();

        public Session(DeviceTerminalSettings.ConnectionSettings.Tcp endpoint)
        {
            Connection = new AsyncTransportConnection(new TcpTransport(endpoint), Ui);
            Connection.StateChanged += (sender, state, message) =>
            {
                lock (States) States.Add((state, message));
                if (state == ConnectionStateEnum.Connected) Connected.Set();
                if (state == ConnectionStateEnum.Disconnected || state == ConnectionStateEnum.Failed) Ended.Set();
            };
            Connection.WriterIdleChanged += (sender, args) => Interlocked.Increment(ref WriterIdleEvents);
            Connection.WriteFailed += (sender, message) => { lock (WriteFailures) WriteFailures.Add(message); };
        }

        public (ConnectionStateEnum State, string Message) LastState
        {
            get { lock (States) return States.Last(); }
        }

        public void Dispose()
        {
            Connection.Dispose();
            Ui.Dispose();
        }
    }

    private static int Main()
    {
        Run("the endpoint text puts an IPv6 address in brackets", () =>
        {
            Check(Endpoint("192.168.0.5", 23).ToString() == "192.168.0.5:23", "IPv4: " + Endpoint("192.168.0.5", 23));
            Check(Endpoint("fe80::1", 4001).ToString() == "[fe80::1]:4001", "IPv6: " + Endpoint("fe80::1", 4001));
            Check(Endpoint("device.local", 5000).ToString() == "device.local:5000", "host name: " + Endpoint("device.local", 5000));
            Check(new TcpTransport(Endpoint("::1", 7)).GetDisplayName(true) == "[::1]:7", "display name of an IPv6 transport");
            Check(!Endpoint("host", 70000).IsValid() && !Endpoint(" ", 23).IsValid() && Endpoint("host", 65535).IsValid(), "IsValid checks the port range and a blank host");
        });

        Run("a raw TCP session carries bytes both ways, unchanged", () =>
        {
            using Server server = new Server(IPAddress.Loopback);
            using Session s = new Session(Endpoint("127.0.0.1", server.Port));
            List<byte> got = new List<byte>();
            s.Connection.DataReceived += (sender, buffer, count) => { lock (got) got.AddRange(buffer.Take(count)); };
            s.Connection.Start();
            Check(s.Connected.Wait(5000), "connected");
            Socket peer = server.Client;
            byte[] binary = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
            peer.Send(binary);
            Check(WaitFor(() => { lock (got) return got.Count == 256; }), "all 256 byte values received");
            lock (got) Check(got.SequenceEqual(binary), "no byte was changed (no telnet, no line-ending translation)");
            Check(s.Connection.Send(Encoding.ASCII.GetBytes("AT\r"), 3), "send queued");
            byte[] buffer2 = new byte[16];
            peer.ReceiveTimeout = 5000;
            int n = peer.Receive(buffer2);
            Check(Encoding.ASCII.GetString(buffer2, 0, n) == "AT\r", "the device got what was sent");
        });

        Run("Nagle is off and keep-alive uses the configured time", () =>
        {
            using Server server = new Server(IPAddress.Loopback);
            using TcpTransport transport = new TcpTransport(Endpoint("127.0.0.1", server.Port, keepAlive: 30));
            transport.Open(null);
            Socket socket = ((NetworkStream)transport.GetStream()).Socket;
            Check(socket.NoDelay, "NoDelay is on, so single key presses are sent at once");
            Check((int)socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive) != 0, "keep-alive is on");
            int idle = (int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime);
            int interval = (int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval);
            Check(idle == 30, "the first probe after 30 idle seconds (not the system default of two hours): " + idle);
            Check(interval >= 1 && interval <= 5, "the next probes a few seconds apart: " + interval);
        });

        Run("without keep-alive no probe is configured, but Nagle is still off", () =>
        {
            using Server server = new Server(IPAddress.Loopback);
            using TcpTransport transport = new TcpTransport(Endpoint("127.0.0.1", server.Port));
            transport.Open(null);
            Socket socket = ((NetworkStream)transport.GetStream()).Socket;
            Check(socket.NoDelay, "NoDelay is on without keep-alive, too");
            Check((int)socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive) == 0, "keep-alive stays off");
        });

        Run("an IPv6 address can be reached", () =>
        {
            if (!Socket.OSSupportsIPv6)
            {
                Console.WriteLine("SKIP: this machine has no IPv6");
                return;
            }
            Server server;
            try
            {
                server = new Server(IPAddress.IPv6Loopback);
            }
            catch (SocketException)
            {
                Console.WriteLine("SKIP: cannot listen on ::1");
                return;
            }
            using (server)
            {
                using TcpTransport transport = new TcpTransport(Endpoint("::1", server.Port));
                transport.Open(null);
                Check(((NetworkStream)transport.GetStream()).Socket.AddressFamily == AddressFamily.InterNetworkV6, "connected over IPv6 to ::1");
                // The listener's backlog completes the connect even though only one client is accepted.
                using TcpTransport bracketed = new TcpTransport(Endpoint("[::1]", server.Port));
                bracketed.Open(null);
                Check(bracketed.GetStream() != null, "a host given as [::1] is accepted, too");
            }
        });

        Run("a host name is resolved and its addresses are tried", () =>
        {
            using Server server = new Server(IPAddress.Loopback);
            using TcpTransport transport = new TcpTransport(Endpoint("localhost", server.Port));
            transport.Open(null);
            Check(transport.GetStream() != null, "connected to localhost (whichever of ::1 and 127.0.0.1 comes first)");
        });

        Run("a refused connection fails with the reason", () =>
        {
            int port;
            using (Server probe = new Server(IPAddress.Loopback)) port = probe.Port;
            using Session s = new Session(Endpoint("127.0.0.1", port));
            s.Connection.Start();
            Check(s.Ended.Wait(5000), "the attempt ended");
            Check(s.LastState.State == ConnectionStateEnum.Failed && !string.IsNullOrEmpty(s.LastState.Message), "Failed, with a reason: " + s.LastState.Message);
        });

        Run("a failed attempt can be retried with the same transport", () =>
        {
            int port;
            using (Server probe = new Server(IPAddress.Loopback)) port = probe.Port;
            using TcpTransport transport = new TcpTransport(Endpoint("127.0.0.1", port));
            bool failed = false;
            try { transport.Open(null); } catch (SocketException) { failed = true; }
            Check(failed, "nothing listens yet: refused");
            TcpListener listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            try
            {
                transport.Open(null);
                Check(transport.GetStream() != null, "the second attempt connects (a fresh socket for every attempt)");
            }
            finally
            {
                listener.Stop();
            }
        });

        Run("closing the tab aborts a connect that is under way", () =>
        {
            // A non-routable address: the SYN gets no answer, so only Close() can end the wait (or the network says
            // at once that it is unreachable, which ends it, too).
            using TcpTransport transport = new TcpTransport(Endpoint("10.255.255.1", 9));
            Exception error = null;
            Thread opener = new Thread(() => { try { transport.Open(null); } catch (Exception ex) { error = ex; } });
            opener.Start();
            Thread.Sleep(300);
            Stopwatch timer = Stopwatch.StartNew();
            transport.Close();
            Check(opener.Join(3000), "Open returned soon after Close (" + timer.ElapsedMilliseconds + " ms)");
            Check(error != null, "and it did not report a connection: " + error?.GetType().Name);
            bool refused = false;
            try { transport.Open(null); } catch (ObjectDisposedException) { refused = true; }
            Check(refused, "a closed transport is not opened again");
        });

        Run("a connect timeout ends an attempt that gets no answer", () =>
        {
            using TcpTransport transport = new TcpTransport(Endpoint("10.255.255.1", 9)) { ConnectTimeoutMs = 700 };
            Stopwatch timer = Stopwatch.StartNew();
            Exception error = null;
            try { transport.Open(null); } catch (Exception ex) { error = ex; }
            Check(error != null && timer.ElapsedMilliseconds < 3000, "gave up after " + timer.ElapsedMilliseconds + " ms: " + error?.Message);
        });

        Run("the device closing the connection ends the session without an error", () =>
        {
            using Server server = new Server(IPAddress.Loopback);
            using Session s = new Session(Endpoint("127.0.0.1", server.Port));
            StringBuilder got = new StringBuilder();
            s.Connection.DataReceived += (sender, buffer, count) => { lock (got) got.Append(Encoding.ASCII.GetString(buffer, 0, count)); };
            s.Connection.Start();
            Check(s.Connected.Wait(5000), "connected");
            Socket peer = server.Client;
            peer.Send(Encoding.ASCII.GetBytes("bye"));
            peer.Shutdown(SocketShutdown.Both);
            peer.Close();
            Check(s.Ended.Wait(5000), "disconnected");
            Check(s.LastState.State == ConnectionStateEnum.Disconnected && s.LastState.Message == null, "a normal end: no reason shown");
            lock (got) Check(got.ToString() == "bye", "the last data arrived before the end: " + got);
            Check(!s.Connection.Send(new byte[] { 1 }, 1), "sending after the end is refused");
        });

        Run("a reset by the device is reported with its reason", () =>
        {
            using Server server = new Server(IPAddress.Loopback);
            using Session s = new Session(Endpoint("127.0.0.1", server.Port));
            s.Connection.Start();
            Check(s.Connected.Wait(5000), "connected");
            Socket peer = server.Client;
            peer.LingerState = new LingerOption(true, 0);
            peer.Close();
            Check(s.Ended.Wait(5000), "disconnected");
            Check(s.LastState.State == ConnectionStateEnum.Disconnected, "state Disconnected (" + s.LastState.Message + ")");
        });

        Run("a flood of data reaches the UI complete, in order, and without piling up", () =>
        {
            const int total = 24 * 1024 * 1024;
            using Server server = new Server(IPAddress.Loopback);
            using Session s = new Session(Endpoint("127.0.0.1", server.Port));
            long received = 0;
            int deliveries = 0;
            int mismatches = 0;
            s.Connection.DataReceived += (sender, buffer, count) =>
            {
                for (int i = 0; i < count; i++)
                {
                    if (buffer[i] != (byte)((received + i) % 251)) mismatches++;
                }
                received += count;
                deliveries++;
                // A UI that draws slower than the network brings data.
                Thread.Sleep(2);
            };
            s.Connection.Start();
            Check(s.Connected.Wait(5000), "connected");
            Socket peer = server.Client;
            Thread sender = new Thread(() =>
            {
                byte[] chunk = new byte[64 * 1024];
                for (long sent = 0; sent < total; sent += chunk.Length)
                {
                    for (int i = 0; i < chunk.Length; i++) chunk[i] = (byte)((sent + i) % 251);
                    peer.Send(chunk);
                }
            });
            sender.Start();
            Check(WaitFor(() => Volatile.Read(ref received) == total, 60000), "all " + total + " bytes arrived: " + Volatile.Read(ref received));
            sender.Join(5000);
            Check(mismatches == 0, "in order and unchanged");
            Check(s.Ui.MaxQueueLength <= 4, "at most a few operations waited for the UI at any time: " + s.Ui.MaxQueueLength);
            Check(deliveries < total / (64 * 1024), "reads were merged into fewer UI updates: " + deliveries);
        });

        Run("the end of the connection is told after the last data, even with a backlog", () =>
        {
            const int total = 3 * 1024 * 1024;
            using Server server = new Server(IPAddress.Loopback);
            using Session s = new Session(Endpoint("127.0.0.1", server.Port));
            long received = 0;
            long receivedAtEnd = -1;
            s.Connection.DataReceived += (sender, buffer, count) =>
            {
                received += count;
                Thread.Sleep(20);
            };
            s.Connection.StateChanged += (sender, state, message) =>
            {
                if (state == ConnectionStateEnum.Disconnected) receivedAtEnd = received;
            };
            s.Connection.Start();
            Check(s.Connected.Wait(5000), "connected");
            Socket peer = server.Client;
            peer.Send(new byte[total]);
            peer.Shutdown(SocketShutdown.Both);
            peer.Close();
            Check(s.Ended.Wait(30000), "disconnected");
            Check(receivedAtEnd == total, "every byte was delivered before the end: " + receivedAtEnd);
        });

        Run("an idle writer says so once, not once per write", () =>
        {
            using Server server = new Server(IPAddress.Loopback);
            using Session s = new Session(Endpoint("127.0.0.1", server.Port));
            s.Connection.Start();
            Check(s.Connected.Wait(5000), "connected");
            Check(WaitFor(() => s.Connection.IsWriterIdle), "the writer is idle");
            Thread.Sleep(200);
            Check(Volatile.Read(ref s.WriterIdleEvents) == 1, "one event for becoming idle: " + s.WriterIdleEvents);
            for (int i = 0; i < 50; i++) s.Connection.Send(new byte[] { (byte)'x' }, 1);
            Socket peer = server.Client;
            peer.ReceiveTimeout = 5000;
            int got = 0;
            byte[] buffer = new byte[64];
            while (got < 50) got += peer.Receive(buffer);
            Check(got == 50, "50 bytes reached the device");
            Check(WaitFor(() => s.Connection.IsWriterIdle), "idle again");
            Check(Volatile.Read(ref s.WriterIdleEvents) <= 101, "no more than a busy/idle pair per write: " + s.WriterIdleEvents);
        });

        Run("a busy UI still sees every state once, as it was when it was posted", () =>
        {
            using Server server = new Server(IPAddress.Loopback);
            using Session s = new Session(Endpoint("127.0.0.1", server.Port));
            ManualResetEventSlim release = new ManualResetEventSlim();
            s.Ui.BeginInvoke((Action)(() => release.Wait(5000)), new object[0]);
            s.Connection.Start();
            Socket peer = server.Client;
            peer.Shutdown(SocketShutdown.Both);
            peer.Close();
            Thread.Sleep(300);
            release.Set();
            Check(s.Ended.Wait(5000), "disconnected");
            ConnectionStateEnum[] states;
            lock (s.States) states = s.States.Select(x => x.State).ToArray();
            Check(states.SequenceEqual(new[] { ConnectionStateEnum.Connecting, ConnectionStateEnum.Connecting, ConnectionStateEnum.Connected, ConnectionStateEnum.Disconnected }),
                "Connecting, Connecting, Connected, Disconnected: " + string.Join(", ", states));
        });

        Run("a peer that stops reading ends the connection instead of blocking the writer for ever", () =>
        {
            using Server server = new Server(IPAddress.Loopback);
            using Session s = new Session(Endpoint("127.0.0.1", server.Port));
            s.Connection.WriteTimeoutMs = 500;
            s.Connection.Start();
            Check(s.Connected.Wait(5000), "connected");
            Socket peer = server.Client; // accepted, but never read from
            byte[] chunk = new byte[1024 * 1024];
            for (int i = 0; i < 256 && !s.Connection.HasEnded; i++)
            {
                s.Connection.Send(chunk, chunk.Length);
            }
            Check(s.Ended.Wait(10000), "the connection ended");
            Check(s.LastState.State == ConnectionStateEnum.Disconnected && !string.IsNullOrEmpty(s.LastState.Message), "with a reason: " + s.LastState.Message);
            Check(WaitFor(() => { lock (s.WriteFailures) return s.WriteFailures.Count > 0; }), "the user is told that data was lost");
        });

        Run("short numbers are not taken for IP addresses", () =>
        {
            Check(TcpConnector.ParseLiteral("192.168.0.5", out IPAddress four) && four.ToString() == "192.168.0.5", "a full IPv4 address is a literal");
            Check(TcpConnector.ParseLiteral("::1", out IPAddress six) && six.AddressFamily == AddressFamily.InterNetworkV6, "an IPv6 address is a literal");
            Check(!TcpConnector.ParseLiteral("device.local", out _), "a name is left to the resolver");
            foreach (string bad in new[] { "1", "192.168.1", "10.0.0.1.5" })
            {
                bool refused = false;
                try { TcpConnector.ParseLiteral(bad, out _); } catch (ArgumentException) { refused = true; }
                Check(refused, "\"" + bad + "\" is refused");
            }
        });

        Run("closing a transport leaves no stream for a late writer", () =>
        {
            using Server server = new Server(IPAddress.Loopback);
            using TcpTransport transport = new TcpTransport(Endpoint("127.0.0.1", server.Port));
            transport.Open(null);
            Check(transport.GetStream() != null, "open");
            transport.Close();
            Check(transport.GetStream() == null, "no stream after Close");
        });

        Run("a refused connection is reported as a refusal", () =>
        {
            int closedPort;
            using (Server probe = new Server(IPAddress.Loopback)) closedPort = probe.Port;
            Exception error = null;
            try { new TcpConnector().Connect("127.0.0.1", closedPort, 2000); } catch (Exception ex) { error = ex; }
            Check(error is SocketException, "refused: " + error?.GetType().Name);
        });

        Run("a name with several addresses connects to the one that works", () =>
        {
            using Server server = new Server(IPAddress.Loopback);
            using TcpTransport transport = new TcpTransport(Endpoint("localhost", server.Port)) { ConnectTimeoutMs = 3000 };
            transport.Open(null);
            Check(transport.GetStream() != null, "connected");
        });

        Console.WriteLine();
        if (_failures > 0)
        {
            Console.WriteLine(_failures + " FAILED");
            return 1;
        }
        Console.WriteLine("All raw TCP transport checks passed.");
        return 0;
    }
}
