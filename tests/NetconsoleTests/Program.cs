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
using System.Xml.Serialization;
using VisualTTY.DeviceTerminal;

using Netconsole = VisualTTY.DeviceTerminal.DeviceTerminalSettings.ConnectionSettings.Netconsole;

// Regression harness for the netconsole (UDP) connection. Run with: dotnet run --project tests/NetconsoleTests
// Covers the decoding of plain and extended Linux netconsole messages (timestamps, levels, escapes, fragments), how
// line mode cuts typed input into datagrams, the settings (parsing, saving), and the real UDP transport over loopback.
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

    private static string Show(string text)
    {
        return text.Replace("\u001b", "<ESC>").Replace("\r", "\\r").Replace("\n", "\\n");
    }

    private static string Decode(NetconsoleDecoder decoder, string datagram, string sender = "10.0.0.2")
    {
        byte[] bytes = Encoding.UTF8.GetBytes(datagram);
        return Encoding.UTF8.GetString(decoder.Decode(bytes, 0, bytes.Length, sender));
    }

    private static int Main()
    {
        Run("decoder", DecoderChecks);
        Run("framing", FramingChecks);
        Run("settings", SettingsChecks);
        Run("transport", TransportChecks);
        Run("transport filter", FilterChecks);
        Run("transport errors", ErrorChecks);
        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "All netconsole checks passed." : _failures + " netconsole check(s) FAILED.");
        return _failures == 0 ? 0 : 1;
    }

    private static void DecoderChecks()
    {
        NetconsoleDecoder plain = new NetconsoleDecoder();
        Check(Decode(plain, "eth0: link up\n") == "eth0: link up\n", "plain netconsole output is passed on unchanged");
        Check(Decode(plain, "partial ") == "partial " && Decode(plain, "line\n") == "line\n", "a line split over datagrams is joined");
        Check(Decode(plain, "a: b; c\n") == "a: b; c\n", "a ';' in plain text is not a header");
        Check(Decode(plain, "1,2,3 x;y\n") == "1,2,3 x;y\n", "a space before ';' is not a header");

        NetconsoleDecoder extended = new NetconsoleDecoder { ColorizeLevels = false };
        string line = Decode(extended, "6,416,1758426,-;netconsole: network logging started\n");
        Check(line == "[    1.758426] netconsole: network logging started\r\n", "extended message shown like dmesg: " + Show(line));
        line = Decode(extended, "6.4.0,6,444,501151268,-;netconsole: network logging started\n");
        Check(line == "[  501.151268] netconsole: network logging started\r\n", "kernel release in front is skipped: " + Show(line));
        line = Decode(extended, "4,10,5000000,-;caf\\xc3\\xa9 \\x5c path\n SUBSYSTEM=net\n DEVICE=n2\n");
        Check(line == "[    5.000000] caf\u00e9 \\ path\r\n", "escapes are restored and dictionary lines dropped: " + Show(line));
        line = Decode(extended, "6,11,0,-;first\\x0asecond\n");
        Check(line == "[    0.000000] first\r\n               second\r\n", "a record with a line break indents its second line: " + Show(line));
        line = Decode(extended, "6,12,1,c;continued");
        Check(line == "[    0.000001] continued\r\n", "a continuation flag and no final newline still make a line: " + Show(line));

        // Plain output that stopped in the middle of a line is ended before an extended message.
        NetconsoleDecoder mixed = new NetconsoleDecoder { ColorizeLevels = false };
        string first = Decode(mixed, "booting");
        string second = Decode(mixed, "6,1,2000000,-;hello\n");
        Check(first == "booting" && second == "\r\n[    2.000000] hello\r\n", "half line before a record is ended: " + Show(second));

        NetconsoleDecoder colors = new NetconsoleDecoder();
        line = Decode(colors, "3,1,1000000,-;disk failure\n");
        Check(line == "\u001b[32m[    1.000000] \u001b[0m\u001b[31mdisk failure\u001b[0m\r\n", "an error is red: " + Show(line));
        line = Decode(colors, "6,2,1000000,-;info\n");
        Check(line == "\u001b[32m[    1.000000] \u001b[0minfo\r\n", "info keeps the normal colour: " + Show(line));
        line = Decode(colors, "12,3,1000000,-;user warning\n");
        Check(line.Contains("\u001b[33muser warning"), "the level is taken from the priority without the facility (12 = user.warning)");
        Check(NetconsoleDecoder.LevelColor(7) == "\u001b[90m" && NetconsoleDecoder.LevelColor(0) == "\u001b[1;31m", "debug grey, emergency bold red");

        NetconsoleDecoder raw = new NetconsoleDecoder { DecodeExtended = false };
        Check(Decode(raw, "6,416,1758426,-;text\n") == "6,416,1758426,-;text\n", "raw format shows extended messages as they are");

        // Fragments: "ncfrag=offset/total", put together before the record is shown.
        NetconsoleDecoder fragments = new NetconsoleDecoder { ColorizeLevels = false };
        string body = "0123456789abcdefghij\n";
        string part1 = Decode(fragments, "6,77,3000000,-,ncfrag=0/" + body.Length + ";" + body.Substring(0, 10));
        string part2 = Decode(fragments, "6,77,3000000,-,ncfrag=10/" + body.Length + ";" + body.Substring(10));
        Check(part1 == "" && part2 == "[    3.000000] 0123456789abcdefghij\r\n", "fragments are joined: " + Show(part1 + "|" + part2));
        part2 = Decode(fragments, "6,78,3000000,-,ncfrag=10/" + body.Length + ";" + body.Substring(10));
        part1 = Decode(fragments, "6,78,3000000,-,ncfrag=0/" + body.Length + ";" + body.Substring(0, 10));
        Check(part2 == "" && part1 == "[    3.000000] 0123456789abcdefghij\r\n", "fragments in reverse order are joined");
        string lost = Decode(fragments, "6,79,4000000,-,ncfrag=0/30;only the start");
        string next = Decode(fragments, "6,80,5000000,-;next\n");
        Check(lost == "" && next == "[    4.000000] only the start\r\n[    5.000000] next\r\n", "a record with a lost piece is shown when the next one comes: " + Show(next));

        // The sender in front of every line.
        NetconsoleDecoder senders = new NetconsoleDecoder { ShowSender = true, ColorizeLevels = false };
        string a = Decode(senders, "one\ntwo\n", "10.0.0.2");
        Check(a == "[10.0.0.2] one\n[10.0.0.2] two\n", "sender in front of each line: " + Show(a));
        string b = Decode(senders, "half", "10.0.0.2") + Decode(senders, "other\n", "10.0.0.3");
        Check(b == "[10.0.0.2] half\r\n[10.0.0.3] other\n", "another sender starts its own line: " + Show(b));
        string c = Decode(senders, "6,1,0,-;rec\n", "10.0.0.4");
        Check(c == "[10.0.0.4] [    0.000000] rec\r\n", "sender in front of an extended message: " + Show(c));

        Check(!NetconsoleDecoder.TryParseHeader(Encoding.ASCII.GetBytes("6,1,2,-,ncfrag=5/3;x"), 0, 19, out _), "a fragment offset past the total is not a header");
        Check(NetconsoleDecoder.Unescape(Encoding.ASCII.GetBytes("\\x4"), 0, 3).Length == 3, "a cut escape at the end stays as it is");
    }

    private static void FramingChecks()
    {
        Check(NetconsoleFraming.FindLineDatagramEnd(Encoding.ASCII.GetBytes("hel").ToList()) == 0, "an unfinished line waits");
        Check(NetconsoleFraming.FindLineDatagramEnd(Encoding.ASCII.GetBytes("help\r\n").ToList()) == 6, "CR LF ends the datagram");
        Check(NetconsoleFraming.FindLineDatagramEnd(Encoding.ASCII.GetBytes("a\nb").ToList()) == 2, "text after the line break waits for the next one");
        Check(NetconsoleFraming.FindLineDatagramEnd(Encoding.ASCII.GetBytes("a\tb").ToList()) == 0, "Tab is part of the line");
        Check(NetconsoleFraming.FindLineDatagramEnd(new List<byte> { (byte)'x', 3 }) == 2, "Ctrl+C goes out at once");
        Check(NetconsoleFraming.FindLineDatagramEnd(Encoding.ASCII.GetBytes("\u001b[A").ToList()) == 3, "an escape sequence goes out at once");
    }

    private static void SettingsChecks()
    {
        Netconsole empty = Netconsole.Parse("");
        Check(empty.LocalPort == 6666 && empty.RemoteHost == null && empty.RemotePort == 6666, "empty text: port 6666, answer the last sender");
        Netconsole port = Netconsole.Parse("udp://7777");
        Check(port.LocalPort == 7777 && port.RemoteHost == null, "a number is the local port");
        Netconsole host = Netconsole.Parse("192.168.1.20");
        Check(host.LocalPort == 6666 && host.RemoteHost == "192.168.1.20" && host.RemotePort == 6666, "a host is the device");
        Netconsole both = Netconsole.Parse("netconsole://6000@[fe80::1]:7000");
        Check(both.LocalPort == 6000 && both.RemoteHost == "fe80::1" && both.RemotePort == 7000, "port@[ipv6]:port");
        Netconsole bareV6 = Netconsole.Parse("fe80::2");
        Check(bareV6.RemoteHost == "fe80::2" && bareV6.RemotePort == 6666, "a bare IPv6 address has no port");
        bool threw = false;
        try { Netconsole.Parse("host:99999"); } catch (FormatException) { threw = true; }
        Check(threw, "a port out of range is rejected");

        Check(new Netconsole().ToString() == "netconsole :6666", "display name without a device");
        Check(new Netconsole { LocalAddress = "10.0.0.1", RemoteHost = "10.0.0.2", RemotePort = 6665 }.ToString() == "netconsole 10.0.0.1:6666 -> 10.0.0.2:6665", "display name with both ends");
        Check(new Netconsole().IsValid() && !new Netconsole { LocalPort = 0 }.IsValid(), "the port must be valid");
        Check(!new Netconsole { AcceptOnlyFromRemoteHost = true }.IsValid(), "filtering needs the device address");
        Check(new Netconsole().FileNameBase != new Netconsole { LocalPort = 6667 }.FileNameBase, "profiles of two ports are saved apart");
        Check(DeviceSessionKey.ForUdpPort(6666) == "udp:6666" && DeviceSessionKey.ForUdpPort(0) == null, "one session per UDP port");

        DeviceTerminalSettings settings = new DeviceTerminalSettings
        {
            Connection = new Netconsole
            {
                LocalPort = 6000,
                RemoteHost = "board",
                Input = Netconsole.InputMode.Character,
                Format = Netconsole.MessageFormat.Raw,
                ShowSender = true,
                LocalEcho = false
            }
        };
        XmlSerializer serializer = new XmlSerializer(typeof(DeviceTerminalSettings));
        StringWriter writer = new StringWriter();
        serializer.Serialize(writer, settings);
        DeviceTerminalSettings loaded = (DeviceTerminalSettings)serializer.Deserialize(new StringReader(writer.ToString()));
        Netconsole back = loaded.Connection as Netconsole;
        Check(back != null && back.LocalPort == 6000 && back.RemoteHost == "board" && back.Input == Netconsole.InputMode.Character
            && back.Format == Netconsole.MessageFormat.Raw && back.ShowSender && !back.LocalEcho && back.ColorizeLevels,
            "a netconsole profile is saved and read back");
    }

    private static NetconsoleTransport OpenTransport(Netconsole settings)
    {
        NetconsoleTransport transport = new NetconsoleTransport(settings);
        transport.Open(null);
        return transport;
    }

    /// <summary>Reads from the stream on a worker thread until <paramref name="expected"/> bytes came or the time is up.</summary>
    private static string ReadText(Stream stream, int expected, int timeoutMs = 3000)
    {
        List<byte> received = new List<byte>();
        Thread reader = new Thread(() =>
        {
            byte[] buffer = new byte[7];
            try
            {
                while (received.Count < expected)
                {
                    int count = stream.Read(buffer, 0, buffer.Length);
                    if (count <= 0) break;
                    lock (received) received.AddRange(buffer.Take(count));
                }
            }
            catch
            {
            }
        }) { IsBackground = true };
        reader.Start();
        reader.Join(timeoutMs);
        lock (received) return Encoding.UTF8.GetString(received.ToArray());
    }

    private static byte[] ReceiveWithTimeout(UdpClient client, out IPEndPoint from, int timeoutMs = 3000)
    {
        client.Client.ReceiveTimeout = timeoutMs;
        from = new IPEndPoint(IPAddress.Any, 0);
        return client.Receive(ref from);
    }

    private static void TransportChecks()
    {
        // The "device": a UDP socket that sends console output to the receiver and reads what is typed.
        using UdpClient device = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int devicePort = ((IPEndPoint)device.Client.LocalEndPoint).Port;
        Netconsole settings = new Netconsole { LocalAddress = "127.0.0.1", LocalPort = 0, RemotePort = devicePort, ColorizeLevels = false };
        using NetconsoleTransport transport = OpenTransport(settings);
        int port = transport.LocalEndPoint.Port;
        Check(port > 0, "the UDP port is open");
        Stream stream = transport.GetStream();
        Check(stream != null, "the stream is there after Open");

        IPEndPoint receiver = new IPEndPoint(IPAddress.Loopback, port);
        Check(transport.InputTarget == null, "no input target before the first datagram");
        bool threw = false;
        try { stream.Write(new byte[] { (byte)'x' }, 0, 1); stream.Write(new byte[] { (byte)'\n' }, 0, 1); }
        catch (IOException) { threw = true; }
        Check(threw, "input before anything arrived says there is nowhere to send it");

        byte[] message = Encoding.ASCII.GetBytes("6,1,1500000,-;hello from the kernel\n");
        device.Send(message, message.Length, receiver);
        string text = ReadText(stream, "[    1.500000] hello from the kernel\r\n".Length);
        Check(text == "[    1.500000] hello from the kernel\r\n", "an extended message arrives decoded: " + Show(text));
        Check(transport.LastSender != null && transport.LastSender.Port == devicePort, "the sender is remembered");
        Check(transport.InputTarget != null && transport.InputTarget.Port == devicePort, "input goes back to the sender's address and the device port");

        byte[] plain = Encoding.ASCII.GetBytes("plain output\n");
        device.Send(plain, plain.Length, receiver);
        text = ReadText(stream, plain.Length);
        Check(text == "plain output\n", "a datagram longer than the read buffer is read in pieces");

        // Line mode: one datagram per line, whatever pieces it was written in.
        transport.InputMode = Netconsole.InputMode.Line;
        stream.Write(Encoding.ASCII.GetBytes("print"), 0, 5);
        stream.Write(Encoding.ASCII.GetBytes("env\n"), 0, 4);
        byte[] got = ReceiveWithTimeout(device, out IPEndPoint from);
        Check(Encoding.ASCII.GetString(got) == "printenv\n", "line mode sends the line as one datagram");
        Check(from.Port == port, "input is sent from the listening port (what U-Boot expects)");

        // Character mode: every write is a datagram of its own.
        transport.InputMode = Netconsole.InputMode.Character;
        stream.Write(new[] { (byte)'a' }, 0, 1);
        stream.Write(new[] { (byte)'b' }, 0, 1);
        string first = Encoding.ASCII.GetString(ReceiveWithTimeout(device, out _));
        string second = Encoding.ASCII.GetString(ReceiveWithTimeout(device, out _));
        Check(first == "a" && second == "b", "character mode sends every key at once");

        // An unfinished line goes first when the mode is switched.
        transport.InputMode = Netconsole.InputMode.Line;
        stream.Write(Encoding.ASCII.GetBytes("ver"), 0, 3);
        transport.InputMode = Netconsole.InputMode.Character;
        stream.Write(new[] { (byte)'\r' }, 0, 1);
        first = Encoding.ASCII.GetString(ReceiveWithTimeout(device, out _));
        second = Encoding.ASCII.GetString(ReceiveWithTimeout(device, out _));
        Check(first == "ver" && second == "\r", "a pending line is sent before the first key of character mode");

        // A long write is split into datagrams U-Boot can read.
        byte[] longLine = Encoding.ASCII.GetBytes(new string('x', NetconsoleTransport.MaxSendDatagram + 10));
        stream.Write(longLine, 0, longLine.Length);
        int part1 = ReceiveWithTimeout(device, out _).Length;
        int part2 = ReceiveWithTimeout(device, out _).Length;
        Check(part1 == NetconsoleTransport.MaxSendDatagram && part2 == 10, "a long write is split");

        // The ICMP "port unreachable" of input sent to a closed port must not end the session (Windows reports it).
        using (UdpClient closed = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            int closedPort = ((IPEndPoint)closed.Client.LocalEndPoint).Port;
            closed.Send(plain, plain.Length, receiver);
            ReadText(stream, plain.Length);
        }
        transport.InputMode = Netconsole.InputMode.Character;
        try
        {
            stream.Write(new[] { (byte)'z' }, 0, 1);
        }
        catch (SocketException)
        {
            // Some systems report the unreachable port on the send itself; the reader must still go on.
        }
        device.Send(plain, plain.Length, receiver);
        text = ReadText(stream, plain.Length);
        Check(text == "plain output\n", "the reader goes on after input went to a closed port");

        // Closing wakes the reader.
        Stopwatch timer = Stopwatch.StartNew();
        Exception readEnd = null;
        Thread blocked = new Thread(() =>
        {
            try { stream.Read(new byte[16], 0, 16); }
            catch (Exception ex) { readEnd = ex; }
        }) { IsBackground = true };
        blocked.Start();
        Thread.Sleep(100);
        transport.Close();
        Check(blocked.Join(2000), "Close ends a waiting read at once (" + (readEnd?.GetType().Name ?? "returned") + ")");
        Check(transport.GetStream() == null, "no stream after Close");
        threw = false;
        try { transport.Open(null); } catch (ObjectDisposedException) { threw = true; }
        Check(threw, "a closed transport is not opened again");
    }

    private static void FilterChecks()
    {
        using UdpClient device = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using UdpClient other = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int devicePort = ((IPEndPoint)device.Client.LocalEndPoint).Port;
        // Every datagram comes from 127.0.0.1 here, so the filter is checked with an address nobody sends from.
        Netconsole settings = new Netconsole { LocalAddress = "127.0.0.1", LocalPort = 0, RemoteHost = "127.0.0.2", RemotePort = devicePort, AcceptOnlyFromRemoteHost = true };
        using (NetconsoleTransport transport = OpenTransport(settings))
        {
            IPEndPoint receiver = new IPEndPoint(IPAddress.Loopback, transport.LocalEndPoint.Port);
            byte[] data = Encoding.ASCII.GetBytes("not from the device\n");
            other.Send(data, data.Length, receiver);
            string text = ReadText(transport.GetStream(), 1, 500);
            Check(text == "", "datagrams from other senders are dropped");
            Check(transport.InputTarget.Address.Equals(IPAddress.Parse("127.0.0.2")), "the configured device gets the input");
        }
        settings = new Netconsole { LocalAddress = "127.0.0.1", LocalPort = 0, RemoteHost = "127.0.0.1", RemotePort = devicePort, AcceptOnlyFromRemoteHost = true };
        using (NetconsoleTransport transport = OpenTransport(settings))
        {
            IPEndPoint receiver = new IPEndPoint(IPAddress.Loopback, transport.LocalEndPoint.Port);
            byte[] data = Encoding.ASCII.GetBytes("from the device\n");
            device.Send(data, data.Length, receiver);
            Check(ReadText(transport.GetStream(), data.Length) == "from the device\n", "datagrams of the device are shown");
            // Input before anything arrived goes to the configured device.
            transport.InputMode = Netconsole.InputMode.Line;
            transport.GetStream().Write(Encoding.ASCII.GetBytes("reset\n"), 0, 6);
            Check(Encoding.ASCII.GetString(ReceiveWithTimeout(device, out _)) == "reset\n", "input reaches the configured device");
        }

        // All interfaces (dual-mode IPv6 where available) receive IPv4 datagrams, and the sender is shown as IPv4.
        Netconsole any = new Netconsole { LocalPort = 0, ShowSender = true, ColorizeLevels = false, RemotePort = devicePort };
        using (NetconsoleTransport transport = OpenTransport(any))
        {
            IPEndPoint receiver = new IPEndPoint(IPAddress.Loopback, transport.LocalEndPoint.Port);
            byte[] data = Encoding.ASCII.GetBytes("hi\n");
            device.Send(data, data.Length, receiver);
            string text = ReadText(transport.GetStream(), "[127.0.0.1] hi\n".Length);
            Check(text == "[127.0.0.1] hi\n", "listening on every interface: IPv4 sender shown as IPv4: " + Show(text));
            transport.InputMode = Netconsole.InputMode.Line;
            transport.GetStream().Write(Encoding.ASCII.GetBytes("ok\n"), 0, 3);
            Check(Encoding.ASCII.GetString(ReceiveWithTimeout(device, out _)) == "ok\n", "input to an IPv4 device from a dual-mode socket");
        }
    }

    private static void ErrorChecks()
    {
        using NetconsoleTransport first = OpenTransport(new Netconsole { LocalAddress = "127.0.0.1", LocalPort = 0 });
        int port = first.LocalEndPoint.Port;
        NetconsoleTransport second = new NetconsoleTransport(new Netconsole { LocalAddress = "127.0.0.1", LocalPort = port });
        string message = null;
        try { second.Open(null); } catch (IOException ex) { message = ex.Message; }
        Check(message != null && message.Contains("already in use"), "a port in use is reported as such: " + message);
        second.Dispose();

        NetconsoleTransport noDevice = new NetconsoleTransport(new Netconsole { LocalPort = 0, AcceptOnlyFromRemoteHost = true });
        bool threw = false;
        try { noDevice.Open(null); } catch (ArgumentException) { threw = true; }
        Check(threw, "filtering without a device address is refused (not retried)");
        noDevice.Dispose();

        NetconsoleTransport unknown = new NetconsoleTransport(new Netconsole { LocalPort = 0, RemoteHost = "no-such-host.invalid" });
        message = null;
        try { unknown.Open(null); } catch (IOException ex) { message = ex.Message; }
        Check(message != null && message.Contains("no-such-host.invalid"), "an unknown device name is reported: " + message);
        unknown.Dispose();
    }
}
