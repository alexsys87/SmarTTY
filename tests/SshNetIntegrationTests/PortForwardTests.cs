using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SmarTTY.Ssh.Tests;

/// <summary>
/// Tools > Port forwarding: local ("ssh -L") and remote ("ssh -R") forwardings of <see cref="SshSession"/> and the list of a
/// session in <see cref="PortForwardHost"/>, against the live server. A local forwarding is checked by logging in to the
/// server a second time through it (SSH inside SSH); a remote one by a bash on the server that connects to its port and
/// talks to a listener of the test. The second port of the server (AllowTcpForwarding no) checks the refusal.
/// </summary>
internal static class PortForwardTests
{
    public static void Run()
    {
        Test.Section("Port forwarding rules");
        PortForwardRule local = new PortForwardRule { Kind = PortForwardKind.Local, BindAddress = "127.0.0.1", BindPort = 8080, TargetHost = "db.internal", TargetPort = 5432 };
        Test.Equal("-L 127.0.0.1:8080:db.internal:5432", local.SshOption, "a local forwarding reads like ssh -L");
        Test.Check(local.Validate() == null, "a complete local forwarding is valid");
        PortForwardRule remote = new PortForwardRule { Kind = PortForwardKind.Remote, BindAddress = "127.0.0.1", BindPort = 9000, TargetHost = "::1", TargetPort = 3000 };
        Test.Equal("-R 127.0.0.1:9000:[::1]:3000", remote.SshOption, "a remote forwarding reads like ssh -R, an IPv6 address in brackets");
        Test.Check(new PortForwardRule { Kind = PortForwardKind.Local, BindPort = 0, TargetHost = "h", TargetPort = 1 }.Validate() == null, "local port 0 (any free port) is valid");
        Test.Check(new PortForwardRule { Kind = PortForwardKind.Remote, BindPort = 0, TargetHost = "h", TargetPort = 1 }.Validate() != null, "remote port 0 is refused: the port the server chose could not be shown");
        Test.Check(new PortForwardRule { Kind = PortForwardKind.Local, BindAddress = "my-pc", BindPort = 1, TargetHost = "h", TargetPort = 1 }.Validate() != null, "a local listening address must be an IP address");
        Test.Check(new PortForwardRule { Kind = PortForwardKind.Local, BindPort = 1, TargetHost = "", TargetPort = 1 }.Validate() != null, "a destination host is needed");
        Test.Check(new PortForwardRule { Kind = PortForwardKind.Local, BindPort = 1, TargetHost = "h", TargetPort = 70000 }.Validate() != null, "a destination port above 65535 is refused");
    }

    public static void RunLive(TestEnvironment environment)
    {
        Test.Section("Port forwarding (ssh -L / -R)");
        using (SshSession session = Connect(environment, environment.Port))
        {
            // -L: a free local port leads to the SSH server itself, as the server sees it.
            using (SshPortForward forward = session.OpenLocalForward("127.0.0.1", 0, "localhost", (uint)environment.Port))
            {
                Test.Check(forward.IsStarted && forward.BoundPort != 0, "a local forwarding on port 0 listens on the port the system chose (" + forward.BoundPort + ")");
                SshConnectionParameters through = environment.Parameters();
                through.Port = (int)forward.BoundPort;
                using (SshSession inner = new SshSession(through))
                {
                    Test.Check(inner.Authenticate(environment.User, environment.Password), "an SSH login through the local forwarding works (SSH inside SSH)");
                    List<string> output = inner.RunCommandChecked("head -c 300000 /dev/zero | base64 -w 0 | wc -c", requireSuccess: true);
                    Test.Equal("400000", output?.FirstOrDefault()?.Trim(), "bulk data passes the local forwarding");
                }
            }

            // -R: a port of the server leads to a listener of this computer.
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int localPort = ((IPEndPoint)listener.LocalEndpoint).Port;
                Task<string> served = Task.Run(() => AnswerOnce(listener));
                int remotePort = 20000 + new Random().Next(20000);
                using (SshPortForward forward = session.OpenRemoteForward("127.0.0.1", (uint)remotePort, "127.0.0.1", (uint)localPort))
                {
                    Test.Check(forward.IsStarted, "the server accepts the remote forwarding of port " + remotePort);
                    List<string> output = session.RunCommandChecked("exec 3<>/dev/tcp/127.0.0.1/" + remotePort + " && echo ping >&3 && read -r answer <&3 && echo \"got:$answer\"", requireSuccess: true);
                    Test.Equal("got:pong", output?.FirstOrDefault(), "a connection to the server's port reaches this computer and the answer comes back");
                    Test.Equal("ping", served.Wait(TimeSpan.FromSeconds(10)) ? served.Result : null, "the listener of this computer received what the server sent");
                }
                Test.Check(session.RunCommandChecked("exec 3<>/dev/tcp/127.0.0.1/" + remotePort, requireSuccess: true) == null, "after Dispose the server no longer listens on the port");
            }
            finally
            {
                listener.Stop();
            }
        }

        if (environment.NoX11Port > 0)
        {
            using SshSession refusing = Connect(environment, environment.NoX11Port);
            Test.Throws<Exception>(() => refusing.OpenRemoteForward("127.0.0.1", 20000 + (uint)new Random().Next(20000), "127.0.0.1", 1).Dispose(), "a server with AllowTcpForwarding no refuses a remote forwarding");
        }

        Host(environment);
    }

    private static void Host(TestEnvironment environment)
    {
        Test.Section("Port forwarding list of a session (PortForwardHost)");
        PortForwardHost host = new PortForwardHost();
        List<string> log = new List<string>();
        host.Log += line => { lock (log) { log.Add(line); } };
        host.Load(new[]
        {
            new PortForwardRule { Kind = PortForwardKind.Local, BindAddress = "127.0.0.1", BindPort = 0, TargetHost = "localhost", TargetPort = environment.Port, StartOnConnect = true, Description = "ssh again" },
            new PortForwardRule { Kind = PortForwardKind.Local, BindAddress = "127.0.0.1", BindPort = 0, TargetHost = "localhost", TargetPort = environment.Port },
        });
        host.Load(new[] { new PortForwardRule() });
        Test.Equal(2, host.Entries.Length, "the profile's forwardings are loaded once; a later connection keeps the list as edited");

        SshSession first = Connect(environment, environment.Port);
        host.OnConnected(first);
        PortForwardHost.Entry automatic = host.Entries[0];
        PortForwardHost.Entry manual = host.Entries[1];
        Test.Check(automatic.IsActive && automatic.StatusText.StartsWith("Active on port", StringComparison.Ordinal), "a forwarding marked 'start on connect' starts with the connection (" + automatic.StatusText + ")");
        Test.Check(!manual.IsActive && manual.StatusText == "Stopped", "the others wait to be started");

        host.Start(first, manual);
        Test.Check(manual.IsActive && host.HasActive, "Start opens a forwarding");
        host.Stop(automatic);
        Test.Check(!automatic.IsActive && !automatic.Wanted, "Stop closes it");

        // A reconnect: the old connection is gone, the forwarding that was running comes back on the new one, and the one
        // stopped by hand stays stopped although it is marked "start on connect".
        first.Dispose();
        Test.Check(!manual.IsActive && manual.StatusText == "Waiting for the connection", "a lost connection leaves the forwarding waiting (" + manual.StatusText + ")");
        using (SshSession second = Connect(environment, environment.Port))
        {
            host.OnConnected(second);
            Test.Check(manual.IsActive, "after the reconnect the running forwarding works again");
            Test.Check(!automatic.IsActive, "a forwarding stopped by hand is not started again by 'start on connect'");
            SshConnectionParameters through = environment.Parameters();
            through.Port = (int)manual.ActivePort;
            using (SshSession inner = new SshSession(through))
            {
                Test.Check(inner.Authenticate(environment.User, environment.Password), "the restored forwarding carries connections");
            }

            PortForwardRule changed = manual.Rule.Clone();
            changed.TargetPort = 1;
            host.Replace(manual, changed);
            Test.Check(!manual.IsActive && manual.Rule.TargetPort == 1, "editing a forwarding stops it and keeps the new rule");
            Test.Check(host.Rules.Count == 2 && host.Rules[1].TargetPort == 1, "the rules to save follow the edits");

            PortForwardHost.Entry taken = host.Add(new PortForwardRule { Kind = PortForwardKind.Local, BindAddress = "127.0.0.1", BindPort = (int)ListeningPort(out TcpListener blocker), TargetHost = "localhost", TargetPort = 22 });
            try
            {
                Test.Throws<Exception>(() => host.Start(second, taken), "a local port that is taken cannot be forwarded");
                Test.Check(taken.StatusText.StartsWith("Failed:", StringComparison.Ordinal), "the failure is shown in the list (" + taken.StatusText + ")");
            }
            finally
            {
                blocker.Stop();
            }
            host.Remove(taken);
            Test.Equal(2, host.Entries.Length, "Remove takes the forwarding out of the list");
            host.StopAll();
            Test.Check(!host.HasActive, "StopAll stops everything (the session ends)");
        }
        lock (log)
        {
            Test.Check(log.Any(line => line.Contains("Started -L")) && log.Any(line => line.Contains("works again")), "starts and the restore after reconnecting are logged");
        }
    }

    private static uint ListeningPort(out TcpListener listener)
    {
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return (uint)((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string AnswerOnce(TcpListener listener)
    {
        using TcpClient client = listener.AcceptTcpClient();
        NetworkStream stream = client.GetStream();
        StringBuilder line = new StringBuilder();
        int value;
        while ((value = stream.ReadByte()) >= 0 && value != '\n')
        {
            line.Append((char)value);
        }
        byte[] answer = Encoding.ASCII.GetBytes("pong\n");
        stream.Write(answer, 0, answer.Length);
        stream.Flush();
        Thread.Sleep(200);
        return line.ToString();
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
}
