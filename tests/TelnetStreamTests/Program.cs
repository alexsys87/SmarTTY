using System;
using System.IO;
using System.Linq;
using System.Text;
using SmarTTY.Transport;

// Regression harness for telnet connections: the telnet layer of the stream (Transport/TelnetStream.cs: removal
// of commands from the received data, answers to the server's option negotiation, TERMINAL-TYPE, NAWS, escaping
// of outgoing data) and the saved connection settings. Run with: dotnet run --project tests/TelnetStreamTests
internal static class Program
{
    private const byte IAC = TelnetStream.IAC;
    private const byte DO = TelnetStream.DO;
    private const byte DONT = TelnetStream.DONT;
    private const byte WILL = TelnetStream.WILL;
    private const byte WONT = TelnetStream.WONT;
    private const byte SB = TelnetStream.SB;
    private const byte SE = TelnetStream.SE;

    private static int _passed;

    // A connection stand-in: what the "server" sends is read in the given chunks, what the client writes is kept.
    private sealed class FakeConnection : Stream
    {
        private readonly byte[][] _chunks;
        private int _next;
        public readonly MemoryStream Written = new MemoryStream();

        public FakeConnection(params byte[][] chunks)
        {
            _chunks = chunks;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_next >= _chunks.Length)
            {
                return 0;
            }
            byte[] chunk = _chunks[_next];
            if (chunk.Length > count)
            {
                // Hand out what fits and keep the rest as the next chunk.
                Array.Copy(chunk, 0, buffer, offset, count);
                _chunks[_next] = chunk.Skip(count).ToArray();
                return count;
            }
            _next++;
            Array.Copy(chunk, 0, buffer, offset, chunk.Length);
            return chunk.Length;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Written.Write(buffer, offset, count);
        }

        public byte[] TakeWritten()
        {
            byte[] data = Written.ToArray();
            Written.SetLength(0);
            return data;
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static byte[] B(params byte[] bytes)
    {
        return bytes;
    }

    private static byte[] Cat(params byte[][] parts)
    {
        return parts.SelectMany(part => part).ToArray();
    }

    private static byte[] Text(string text)
    {
        return Encoding.ASCII.GetBytes(text);
    }

    private static string Show(byte[] data)
    {
        return string.Join(" ", data.Select(b => b.ToString("X2")));
    }

    // A connection where every command chunk is followed by one character of text ("."), so that every Read() of the
    // stream returns after exactly one command chunk (a read of nothing but commands waits for more).
    private static FakeConnection Marked(params byte[][] commandChunks)
    {
        return new FakeConnection(commandChunks.Select(chunk => Cat(chunk, Text("."))).ToArray());
    }

    // Reads one chunk of what the server sent per call.
    private static void ReadAllChunks(TelnetStream stream, int chunks)
    {
        byte[] buffer = new byte[1024];
        for (int i = 0; i < chunks; i++)
        {
            stream.Read(buffer, 0, buffer.Length);
        }
    }

    // Reads the stream to its end, the way the connection reader does.
    private static byte[] ReadAll(TelnetStream stream, int bufferSize = 1024)
    {
        MemoryStream result = new MemoryStream();
        byte[] buffer = new byte[bufferSize];
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }

    private static void CheckBytes(byte[] actual, byte[] expected, string name)
    {
        if (!actual.SequenceEqual(expected))
        {
            throw new Exception("FAIL: " + name + "\n  expected: " + Show(expected) + "\n  actual:   " + Show(actual));
        }
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static int Main()
    {
        // --- plain data passes through untouched ---
        {
            FakeConnection connection = new FakeConnection(Text("Hello, world\r\nlogin: "));
            TelnetStream stream = new TelnetStream(connection);
            CheckBytes(ReadAll(stream), Text("Hello, world\r\nlogin: "), "plain text is not changed");
            Check(connection.Written.Length == 0, "plain text provokes no answer");
        }

        // --- IAC IAC is a literal 0xFF ---
        {
            TelnetStream stream = new TelnetStream(new FakeConnection(Cat(Text("a"), B(IAC, IAC), Text("b"))));
            CheckBytes(ReadAll(stream), Cat(Text("a"), B(0xFF), Text("b")), "IAC IAC becomes one 0xFF data byte");
        }

        // --- commands are removed, the text around them stays ---
        {
            // NOP, Go Ahead, Data Mark between the text
            TelnetStream stream = new TelnetStream(new FakeConnection(Cat(Text("x"), B(IAC, 241), Text("y"), B(IAC, 249), Text("z"), B(IAC, 242))));
            CheckBytes(ReadAll(stream), Text("xyz"), "NOP, GA and Data Mark are dropped");
        }

        // --- commands split between reads, one byte at a time ---
        {
            byte[] wire = Cat(Text("ab"), B(IAC, WILL, TelnetStream.OptEcho), Text("cd"), B(IAC, SB, 99, 1, 2, 3, IAC, SE), Text("ef"));
            FakeConnection connection = new FakeConnection(wire.Select(b => new[] { b }).ToArray());
            TelnetStream stream = new TelnetStream(connection);
            CheckBytes(ReadAll(stream), Text("abcdef"), "commands split into single bytes are still removed");
            CheckBytes(connection.TakeWritten(), B(IAC, DO, TelnetStream.OptEcho), "...and WILL ECHO is answered exactly once");
        }

        // --- a chunk with only commands does not look like the end of the stream ---
        {
            FakeConnection connection = new FakeConnection(B(IAC, WILL, TelnetStream.OptEcho), Text("after"));
            TelnetStream stream = new TelnetStream(connection);
            byte[] buffer = new byte[64];
            int count = stream.Read(buffer, 0, buffer.Length);
            Check(count == 5 && Encoding.ASCII.GetString(buffer, 0, count) == "after", "a command-only chunk is skipped, not reported as end of stream");
        }

        // --- a small caller buffer never gets more than it can take ---
        {
            byte[] wire = Cat(B(IAC, IAC, IAC, IAC), Text("12345678"));
            TelnetStream stream = new TelnetStream(new FakeConnection(wire));
            byte[] expected = Cat(B(0xFF, 0xFF), Text("12345678"));
            CheckBytes(ReadAll(stream, 3), expected, "reading with a 3 byte buffer loses nothing");
        }

        // --- option negotiation: accepted options ---
        {
            FakeConnection connection = new FakeConnection(B(IAC, WILL, TelnetStream.OptEcho, IAC, WILL, TelnetStream.OptSuppressGoAhead, IAC, DO, TelnetStream.OptSuppressGoAhead));
            ReadAll(new TelnetStream(connection));
            CheckBytes(connection.TakeWritten(),
                B(IAC, DO, TelnetStream.OptEcho, IAC, DO, TelnetStream.OptSuppressGoAhead, IAC, WILL, TelnetStream.OptSuppressGoAhead),
                "ECHO and SUPPRESS-GO-AHEAD are accepted in both directions");
        }

        // --- option negotiation: refused options ---
        {
            FakeConnection connection = new FakeConnection(B(IAC, DO, 39, IAC, WILL, 5, IAC, DO, TelnetStream.OptEcho));
            ReadAll(new TelnetStream(connection));
            CheckBytes(connection.TakeWritten(), B(IAC, WONT, 39, IAC, DONT, 5, IAC, WONT, TelnetStream.OptEcho),
                "unknown options and 'DO ECHO' are refused");
        }

        // --- no negotiation loops: a repeated request is not answered twice ---
        {
            FakeConnection connection = new FakeConnection(B(IAC, WILL, TelnetStream.OptEcho, IAC, WILL, TelnetStream.OptEcho, IAC, DO, TelnetStream.OptTerminalType, IAC, DO, TelnetStream.OptTerminalType));
            ReadAll(new TelnetStream(connection));
            CheckBytes(connection.TakeWritten(), B(IAC, DO, TelnetStream.OptEcho, IAC, WILL, TelnetStream.OptTerminalType),
                "an option that is already on is not acknowledged again");
        }

        // --- WONT / DONT of an enabled option is acknowledged, of a disabled one is not ---
        {
            FakeConnection connection = new FakeConnection(B(IAC, WONT, TelnetStream.OptEcho, IAC, WILL, TelnetStream.OptEcho, IAC, WONT, TelnetStream.OptEcho, IAC, DONT, TelnetStream.OptNaws));
            ReadAll(new TelnetStream(connection));
            CheckBytes(connection.TakeWritten(), B(IAC, DO, TelnetStream.OptEcho, IAC, DONT, TelnetStream.OptEcho),
                "WONT is answered only for an option that was on; a stray DONT is ignored");
        }

        // --- TERMINAL-TYPE ---
        {
            FakeConnection connection = new FakeConnection(B(IAC, DO, TelnetStream.OptTerminalType, IAC, SB, TelnetStream.OptTerminalType, 1, IAC, SE));
            ReadAll(new TelnetStream(connection, "xterm"));
            CheckBytes(connection.TakeWritten(),
                Cat(B(IAC, WILL, TelnetStream.OptTerminalType), B(IAC, SB, TelnetStream.OptTerminalType, 0), Text("xterm"), B(IAC, SE)),
                "TERMINAL-TYPE SEND is answered with the terminal type");
        }
        {
            // Without having agreed to the option, a request is not answered.
            FakeConnection connection = new FakeConnection(B(IAC, SB, TelnetStream.OptTerminalType, 1, IAC, SE));
            ReadAll(new TelnetStream(connection));
            Check(connection.Written.Length == 0, "TERMINAL-TYPE SEND without agreement is ignored");
        }

        // --- NAWS: size sent right after agreeing, and again on every change ---
        {
            FakeConnection connection = new FakeConnection(B(IAC, DO, TelnetStream.OptNaws));
            TelnetStream stream = new TelnetStream(connection, "xterm", 120, 40);
            ReadAll(stream);
            CheckBytes(connection.TakeWritten(),
                B(IAC, WILL, TelnetStream.OptNaws, IAC, SB, TelnetStream.OptNaws, 0, 120, 0, 40, IAC, SE),
                "NAWS: agreement is followed by the terminal size");
            Check(stream.NawsEnabled, "NAWS is reported as enabled");
            stream.SetWindowSize(200, 50);
            CheckBytes(connection.TakeWritten(), B(IAC, SB, TelnetStream.OptNaws, 0, 200, 0, 50, IAC, SE), "NAWS: a new size is sent");
        }
        {
            // 255 in the size must be doubled, 256 columns are 01 00
            FakeConnection connection = new FakeConnection(B(IAC, DO, TelnetStream.OptNaws));
            TelnetStream stream = new TelnetStream(connection, "xterm", 255, 256);
            ReadAll(stream);
            CheckBytes(connection.TakeWritten(),
                B(IAC, WILL, TelnetStream.OptNaws, IAC, SB, TelnetStream.OptNaws, 0, IAC, IAC, 1, 0, IAC, SE),
                "NAWS: a 0xFF byte inside the size is doubled");
        }
        {
            // The size is not sent to a server that did not ask for it.
            FakeConnection connection = new FakeConnection();
            TelnetStream stream = new TelnetStream(connection);
            stream.SetWindowSize(100, 30);
            Check(connection.Written.Length == 0 && !stream.NawsEnabled, "NAWS: nothing is sent before the server asks");
            // ...but the size is remembered for when it does.
            FakeConnection connection2 = new FakeConnection(B(IAC, DO, TelnetStream.OptNaws));
            TelnetStream stream2 = new TelnetStream(connection2);
            stream2.SetWindowSize(100, 30);
            ReadAll(stream2);
            CheckBytes(connection2.TakeWritten(), B(IAC, WILL, TelnetStream.OptNaws, IAC, SB, TelnetStream.OptNaws, 0, 100, 0, 30, IAC, SE),
                "NAWS: the size set before the request is the one reported");
        }

        // --- CR NUL is a bare CR ---
        {
            TelnetStream stream = new TelnetStream(new FakeConnection(Cat(Text("a"), B(13, 0), Text("b"), B(13, 10), Text("c"))));
            CheckBytes(ReadAll(stream), Cat(Text("a"), B(13), Text("b"), B(13, 10), Text("c")), "CR NUL is received as CR, CR LF is kept");
        }
        {
            // ...also when CR and NUL arrive in different reads
            TelnetStream stream = new TelnetStream(new FakeConnection(Text("a\r"), B(0), Text("b")));
            CheckBytes(ReadAll(stream), Cat(Text("a"), B(13), Text("b")), "CR NUL split between reads");
        }
        {
            // a NUL that does not follow a CR is not touched
            TelnetStream stream = new TelnetStream(new FakeConnection(Cat(Text("a"), B(0), Text("b"))));
            CheckBytes(ReadAll(stream), Cat(Text("a"), B(0), Text("b")), "a lone NUL is kept");
        }

        // --- malformed subnegotiation does not swallow the text ---
        {
            // IAC WILL inside a subnegotiation ends it and is handled as a command
            FakeConnection connection = new FakeConnection(Cat(B(IAC, SB, 99, 1, IAC, WILL, TelnetStream.OptEcho), Text("ok")));
            TelnetStream stream = new TelnetStream(connection);
            CheckBytes(ReadAll(stream), Text("ok"), "a command inside a subnegotiation ends it");
            CheckBytes(connection.TakeWritten(), B(IAC, DO, TelnetStream.OptEcho), "...and is still answered");
        }
        {
            // an endless subnegotiation is bounded, the text after it is not lost
            byte[] longData = Enumerable.Repeat((byte)7, 5000).ToArray();
            TelnetStream stream = new TelnetStream(new FakeConnection(Cat(B(IAC, SB, 99), longData, B(IAC, SE), Text("tail"))));
            CheckBytes(ReadAll(stream), Text("tail"), "an oversized subnegotiation is dropped without harm");
        }

        // --- outgoing data ---
        {
            FakeConnection connection = new FakeConnection();
            TelnetStream stream = new TelnetStream(connection);
            stream.Write(Cat(Text("a"), B(0xFF), Text("b")), 0, 3);
            CheckBytes(connection.TakeWritten(), Cat(Text("a"), B(0xFF, 0xFF), Text("b")), "an outgoing 0xFF is doubled");
            stream.Write(B(13, 10), 0, 2);
            CheckBytes(connection.TakeWritten(), B(13, 10), "CR LF is sent as it is");
            stream.Write(B(13), 0, 1);
            CheckBytes(connection.TakeWritten(), B(13, 0), "a bare CR is sent as CR NUL");
            stream.Write(Text("x\ry"), 0, 3);
            CheckBytes(connection.TakeWritten(), Cat(Text("x"), B(13, 0), Text("y")), "a CR inside the text gets its NUL");
            stream.Write(B(10), 0, 1);
            CheckBytes(connection.TakeWritten(), B(10), "a bare LF is sent as it is");
            stream.Write(Text("0123456789"), 2, 3);
            CheckBytes(connection.TakeWritten(), Text("234"), "the offset and count of a write are honored");
        }

        // --- answers and user data do not get mixed up on the wire (every message is written whole) ---
        {
            FakeConnection connection = new FakeConnection(B(IAC, DO, TelnetStream.OptTerminalType));
            TelnetStream stream = new TelnetStream(connection);
            ReadAll(stream);
            connection.TakeWritten();
            stream.Write(Text("ls\r\n"), 0, 4);
            CheckBytes(connection.TakeWritten(), Text("ls\r\n"), "user data after a negotiation is sent unchanged");
        }

        // --- end of stream and argument checks ---
        {
            TelnetStream stream = new TelnetStream(new FakeConnection());
            Check(stream.Read(new byte[8], 0, 8) == 0, "end of the connection is end of the stream");
            bool threw = false;
            try
            {
                stream.Read(new byte[4], 2, 4);
            }
            catch (ArgumentOutOfRangeException)
            {
                threw = true;
            }
            Check(threw, "an invalid buffer range is rejected");
        }

        // --- active negotiation: our offers, and the server's answers to them are not answered again ---
        {
            FakeConnection connection = new FakeConnection(
                B(IAC, WILL, TelnetStream.OptSuppressGoAhead, IAC, DO, TelnetStream.OptSuppressGoAhead, IAC, DO, TelnetStream.OptTerminalType, IAC, DO, TelnetStream.OptNaws));
            TelnetStream stream = new TelnetStream(connection, new TelnetOptions { ActiveNegotiation = true, Columns = 100, Rows = 30 });
            stream.Start();
            CheckBytes(connection.TakeWritten(),
                B(IAC, DO, TelnetStream.OptSuppressGoAhead, IAC, WILL, TelnetStream.OptSuppressGoAhead, IAC, WILL, TelnetStream.OptTerminalType, IAC, WILL, TelnetStream.OptNaws),
                "active negotiation: SGA, terminal type and window size are offered at start");
            ReadAll(stream);
            CheckBytes(connection.TakeWritten(), B(IAC, SB, TelnetStream.OptNaws, 0, 100, 0, 30, IAC, SE),
                "active negotiation: the server's agreement is not answered again, the window size follows");
            Check(stream.NawsEnabled, "active negotiation: NAWS is on after the server agreed");
        }
        {
            // the answer-only mode says nothing at start
            FakeConnection connection = new FakeConnection();
            new TelnetStream(connection, new TelnetOptions()).Start();
            Check(connection.Written.Length == 0, "default mode: nothing is sent at start");
        }
        {
            // an offer that the server refuses is dropped without a word, and can be asked for again later
            FakeConnection connection = new FakeConnection(B(IAC, DONT, TelnetStream.OptTerminalType, IAC, WONT, TelnetStream.OptSuppressGoAhead));
            TelnetStream stream = new TelnetStream(connection, new TelnetOptions { ActiveNegotiation = true });
            stream.Start();
            connection.TakeWritten();
            ReadAll(stream);
            Check(connection.Written.Length == 0 && !stream.NawsEnabled, "active negotiation: a refusal of our offer is not answered");
        }
        {
            // the terminal type is the configured one
            FakeConnection connection = new FakeConnection(B(IAC, DO, TelnetStream.OptTerminalType, IAC, SB, TelnetStream.OptTerminalType, 1, IAC, SE));
            ReadAll(new TelnetStream(connection, new TelnetOptions { TerminalType = "vt100" }));
            CheckBytes(connection.TakeWritten(),
                Cat(B(IAC, WILL, TelnetStream.OptTerminalType), B(IAC, SB, TelnetStream.OptTerminalType, 0), Text("vt100"), B(IAC, SE)),
                "the configured terminal type is reported");
        }

        // --- remote echo: reported once for every change ---
        {
            FakeConnection connection = new FakeConnection(B(IAC, WILL, TelnetStream.OptEcho), Text("login: "), B(IAC, WONT, TelnetStream.OptEcho), Text("x"));
            TelnetStream stream = new TelnetStream(connection);
            int changes = 0;
            bool lastValue = false;
            stream.RemoteEchoChanged += (sender, args) =>
            {
                changes++;
                lastValue = stream.RemoteEcho;
            };
            Check(!stream.RemoteEcho, "remote echo: off at the start");
            byte[] buffer = new byte[64];
            int count = stream.Read(buffer, 0, buffer.Length);
            Check(count == 7 && changes == 1 && lastValue && stream.RemoteEcho, "remote echo: WILL ECHO is reported as on, before the data that follows");
            count = stream.Read(buffer, 0, buffer.Length);
            Check(count == 1 && changes == 2 && !lastValue && !stream.RemoteEcho, "remote echo: WONT ECHO is reported as off");
        }
        {
            // a repeated WILL ECHO is not a change
            FakeConnection connection = new FakeConnection(B(IAC, WILL, TelnetStream.OptEcho, IAC, WILL, TelnetStream.OptEcho), Text("a"));
            TelnetStream stream = new TelnetStream(connection);
            int changes = 0;
            stream.RemoteEchoChanged += (sender, args) => changes++;
            ReadAll(stream);
            Check(changes == 1, "remote echo: a repeated WILL ECHO is reported once");
        }

        // --- BINARY: CR is just a byte when both sides agreed ---
        {
            FakeConnection connection = new FakeConnection(B(IAC, WILL, TelnetStream.OptBinary, IAC, DO, TelnetStream.OptBinary), Cat(Text("a"), B(13, 0), Text("b")));
            TelnetStream stream = new TelnetStream(connection);
            CheckBytes(ReadAll(stream), Cat(Text("a"), B(13, 0), Text("b")), "binary: CR NUL is data when the server sends binary");
            CheckBytes(connection.TakeWritten(), B(IAC, DO, TelnetStream.OptBinary, IAC, WILL, TelnetStream.OptBinary), "binary: accepted in both directions");
            stream.Write(B(13), 0, 1);
            CheckBytes(connection.TakeWritten(), B(13), "binary: a bare CR is sent as it is");
            stream.Write(B(0xFF), 0, 1);
            CheckBytes(connection.TakeWritten(), B(0xFF, 0xFF), "binary: 0xFF is still doubled");
        }
        {
            // when only one direction is binary the other one keeps the NVT rules
            FakeConnection connection = new FakeConnection(B(IAC, DO, TelnetStream.OptBinary), Cat(Text("a"), B(13, 0), Text("b")));
            TelnetStream stream = new TelnetStream(connection);
            CheckBytes(ReadAll(stream), Cat(Text("a"), B(13), Text("b")), "binary: sending binary does not change how CR NUL is received");
            connection.TakeWritten();
            stream.Write(B(13), 0, 1);
            CheckBytes(connection.TakeWritten(), B(13), "binary: our direction is binary after DO BINARY");
        }

        // --- commands sent by the user ---
        {
            FakeConnection connection = new FakeConnection();
            TelnetStream stream = new TelnetStream(connection);
            stream.SendCommand(TelnetStream.AreYouThere);
            stream.SendCommand(TelnetStream.Break);
            stream.SendCommand(TelnetStream.NOP);
            CheckBytes(connection.TakeWritten(), B(IAC, 246, IAC, 243, IAC, 241), "AYT, Break and NOP are sent as IAC commands");
        }

        // --- automatic login ---
        {
            // the server's side of a login: prompt, our line, prompt, our line
            FakeConnection connection = new FakeConnection(Text("Welcome\r\nSwitch login: "), Text("alice\r\nPassword: "), Text("\r\nLast login: Mon Oct 19\r\n$ "));
            TelnetStream stream = new TelnetStream(connection, new TelnetOptions { AutoLogin = new TelnetAutoLogin("alice", "s3cret", null, null) });
            byte[] buffer = new byte[256];
            stream.Read(buffer, 0, buffer.Length);
            CheckBytes(connection.TakeWritten(), Text("alice\r\n"), "auto login: the user name answers the login prompt");
            stream.Read(buffer, 0, buffer.Length);
            CheckBytes(connection.TakeWritten(), Text("s3cret\r\n"), "auto login: the password answers the password prompt");
            stream.Read(buffer, 0, buffer.Length);
            Check(connection.Written.Length == 0, "auto login: 'Last login:' in the banner is not a prompt, and nothing else is sent");
        }
        {
            // the prompt is recognized when it arrives in pieces, with colors and trailing blanks
            FakeConnection connection = new FakeConnection(Text("Us"), Text("er na"), Text("me"), B(0x1B), Text("[0m: "), Text("\r\n\x1B[1mPass"), Text("word: "));
            TelnetStream stream = new TelnetStream(connection, new TelnetOptions { AutoLogin = new TelnetAutoLogin("bob", "pw", null, null) });
            ReadAll(stream);
            CheckBytes(connection.TakeWritten(), Text("bob\r\npw\r\n"), "auto login: prompts split between reads and wrapped in escape sequences");
        }
        {
            // a rejected password: the server asks again, nothing is sent a second time
            FakeConnection connection = new FakeConnection(Text("login: "), Text("Password: "), Text("\r\nLogin incorrect\r\nlogin: "), Text("Password: "));
            TelnetStream stream = new TelnetStream(connection, new TelnetOptions { AutoLogin = new TelnetAutoLogin("alice", "wrong", null, null) });
            ReadAll(stream);
            CheckBytes(connection.TakeWritten(), Text("alice\r\nwrong\r\n"), "auto login: a rejected password is not tried again");
        }
        {
            // a device with only a password prompt
            FakeConnection connection = new FakeConnection(Text("User Access Verification\r\n\r\nPassword: "));
            TelnetStream stream = new TelnetStream(connection, new TelnetOptions { AutoLogin = new TelnetAutoLogin("", "cisco", null, null) });
            ReadAll(stream);
            CheckBytes(connection.TakeWritten(), Text("cisco\r\n"), "auto login: an empty user name skips the login prompt");
        }
        {
            // only a user name
            FakeConnection connection = new FakeConnection(Text("login: "), Text("Password: "));
            TelnetStream stream = new TelnetStream(connection, new TelnetOptions { AutoLogin = new TelnetAutoLogin("root", "", null, null) });
            ReadAll(stream);
            CheckBytes(connection.TakeWritten(), Text("root\r\n"), "auto login: without a password only the user name is sent");
        }
        {
            // the password prompt does not get the user name and the other way round
            FakeConnection connection = new FakeConnection(Text("Password: "), Text("login: "));
            TelnetStream stream = new TelnetStream(connection, new TelnetOptions { AutoLogin = new TelnetAutoLogin("u", "p", null, null) });
            ReadAll(stream);
            CheckBytes(connection.TakeWritten(), Text("u\r\n"), "auto login: the order is login first; a password prompt before it is ignored");
        }
        {
            // custom prompts, case does not matter
            FakeConnection connection = new FakeConnection(Text("Enter ID> "), Text("Secret code ? "));
            TelnetStream stream = new TelnetStream(connection, new TelnetOptions { AutoLogin = new TelnetAutoLogin("7", "1234", "id>; badge>", "code ?", 0) });
            ReadAll(stream);
            CheckBytes(connection.TakeWritten(), Text("7\r\n1234\r\n"), "auto login: configured prompts");
        }
        {
            // telnet commands in the middle do not hide the prompt, and the answer is escaped like any other output
            FakeConnection connection = new FakeConnection(Cat(Text("log"), B(IAC, 241), Text("in: ")));
            TelnetStream stream = new TelnetStream(connection, new TelnetOptions { AutoLogin = new TelnetAutoLogin("a\u00ffb", "", null, null) });
            ReadAll(stream);
            CheckBytes(connection.TakeWritten(), Cat(Text("a"), B(0xC3, 0xBF), Text("b\r\n")), "auto login: the answer is UTF-8");
        }
        {
            // too late: after the timeout nothing is typed any more
            long now = 0;
            TelnetAutoLogin login = new TelnetAutoLogin("u", "p", null, null, 10, () => now);
            Check(login.Feed(Text("login: "), 0, 7).Count == 1, "auto login: answers inside the timeout");
            now = 11000;
            Check(login.Feed(Text("Password: "), 0, 10).Count == 0 && login.IsFinished, "auto login: stops after the timeout");
            Check(new TelnetAutoLogin("", "", null, null).IsFinished, "auto login: nothing to send is finished at once");
        }

        // --- LINEMODE ---
        {
            // not allowed (the default): the server is refused and keeps sending characters
            FakeConnection connection = new FakeConnection(B(IAC, DO, TelnetStream.OptLineMode));
            ReadAll(new TelnetStream(connection));
            CheckBytes(connection.TakeWritten(), B(IAC, WONT, TelnetStream.OptLineMode), "linemode: refused unless allowed");
        }
        {
            byte LM = TelnetStream.OptLineMode;
            // allowed: WILL, MODE is acknowledged, a repeated MODE is not
            FakeConnection connection = Marked(B(IAC, DO, LM), B(IAC, SB, LM, 1, 1, IAC, SE), B(IAC, SB, LM, 1, 1, IAC, SE));
            TelnetStream stream = new TelnetStream(connection, new TelnetOptions { AllowLineMode = true });
            byte[] scratch = new byte[64];
            stream.Read(new byte[1], 0, 0);
            ReadAllChunks(stream, 1);
            CheckBytes(connection.TakeWritten(), B(IAC, WILL, LM), "linemode: DO LINEMODE is answered with WILL when allowed");
            Check(!stream.LineEditing, "linemode: no editing before the server sets a mode");
            ReadAllChunks(stream, 1);
            CheckBytes(connection.TakeWritten(), B(IAC, SB, LM, 1, 1 | 4, IAC, SE), "linemode: MODE EDIT is acknowledged with the ACK bit");
            Check(stream.LineEditing, "linemode: editing after MODE EDIT");
            ReadAllChunks(stream, 1);
            Check(connection.Written.Length == 0, "linemode: the same MODE again is not acknowledged again");
            // typing: nothing goes out before the line ends
            stream.Write(Text("l"), 0, 1);
            stream.Write(Text("s"), 0, 1);
            stream.Write(Text("x"), 0, 1);
            stream.Write(B(127), 0, 1);
            Check(connection.Written.Length == 0, "linemode: typed characters are held back");
            stream.Write(B(13, 10), 0, 2);
            CheckBytes(connection.TakeWritten(), Text("ls\r\n"), "linemode: Backspace/Delete edits the line, CR LF sends it once");
            stream.Write(B(13), 0, 1);
            stream.Write(B(10), 0, 1);
            CheckBytes(connection.TakeWritten(), B(13, 10), "linemode: CR then LF in separate writes is one line end");
            stream.Write(B(10), 0, 1);
            CheckBytes(connection.TakeWritten(), B(13, 10), "linemode: a lone LF ends the line, too");
            stream.Write(Text("abc"), 0, 3);
            stream.Write(B(0x15), 0, 1);
            stream.Write(Text("z\r"), 0, 2);
            CheckBytes(connection.TakeWritten(), Text("z\r\n"), "linemode: Ctrl+U erases the line");
            stream.Write(Text("one two  "), 0, 9);
            stream.Write(B(0x17), 0, 1);
            stream.Write(Text("x\r"), 0, 2);
            CheckBytes(connection.TakeWritten(), Text("one x\r\n"), "linemode: Ctrl+W erases the last word");
            stream.Write(Cat(Text("a"), B(0xC3, 0xA9)), 0, 3);
            stream.Write(B(8), 0, 1);
            stream.Write(Text("b\r"), 0, 2);
            CheckBytes(connection.TakeWritten(), Text("ab\r\n"), "linemode: Backspace removes a whole UTF-8 character");
            byte[] cursorKey = Cat(Text("a"), B(0x1B, (byte)'[', (byte)'1', (byte)';', (byte)'5', (byte)'A'), Text("b\r"));
            stream.Write(cursorKey, 0, cursorKey.Length);
            CheckBytes(connection.TakeWritten(), Text("ab\r\n"), "linemode: the escape sequence of a cursor key is dropped");
            stream.Write(Cat(Text("a"), B(0x1B)), 0, 2);
            stream.Write(Text("b\r"), 0, 2);
            CheckBytes(connection.TakeWritten(), Cat(Text("ab\r\n")), "linemode: a lone Escape does not swallow the next character");
            stream.Write(Cat(Text("x"), B(0xFF), Text("\r")), 0, 3);
            CheckBytes(connection.TakeWritten(), Cat(Text("x"), B(0xFF, 0xFF), Text("\r\n")), "linemode: 0xFF in a line is doubled");
            stream.Write(Text("abc"), 0, 3);
            stream.Write(B(3), 0, 1);
            Check(connection.Written.Length == 0, "linemode: without TRAPSIG Ctrl+C is only a character");
        }
        {
            byte LM = TelnetStream.OptLineMode;
            // TRAPSIG: signals are sent as commands, the line is dropped
            FakeConnection connection = Marked(B(IAC, DO, LM), B(IAC, SB, LM, 1, 3, IAC, SE));
            TelnetStream stream = new TelnetStream(connection, new TelnetOptions { AllowLineMode = true });
            ReadAllChunks(stream, 2);
            connection.TakeWritten();
            stream.Write(Text("abc"), 0, 3);
            stream.Write(B(3), 0, 1);
            CheckBytes(connection.TakeWritten(), B(IAC, 244), "linemode: Ctrl+C is sent as IP");
            stream.Write(Text("\r"), 0, 1);
            CheckBytes(connection.TakeWritten(), B(13, 10), "linemode: the line typed before the signal is gone");
            stream.Write(B(28), 0, 1);
            stream.Write(B(26), 0, 1);
            CheckBytes(connection.TakeWritten(), B(IAC, 238, IAC, 237), "linemode: Ctrl+\\ is ABORT and Ctrl+Z is SUSP");
        }
        {
            byte LM = TelnetStream.OptLineMode;
            // TRAPSIG without EDIT: characters go on at once, the signal characters become commands
            FakeConnection connection = Marked(B(IAC, DO, LM), B(IAC, SB, LM, 1, 2, IAC, SE));
            TelnetStream stream = new TelnetStream(connection, new TelnetOptions { AllowLineMode = true });
            ReadAllChunks(stream, 2);
            connection.TakeWritten();
            stream.Write(Cat(Text("a"), B(3), Text("b")), 0, 3);
            CheckBytes(connection.TakeWritten(), Cat(Text("a"), B(IAC, 244), Text("b")), "linemode: TRAPSIG alone sends characters at once and traps the signals");
            Check(!stream.LineEditing, "linemode: TRAPSIG alone is not line editing");
        }
        {
            byte LM = TelnetStream.OptLineMode;
            // leaving line editing sends what was typed; DONT LINEMODE does the same
            FakeConnection connection = Marked(B(IAC, DO, LM), B(IAC, SB, LM, 1, 1, IAC, SE), B(IAC, SB, LM, 1, 0, IAC, SE));
            TelnetStream stream = new TelnetStream(connection, new TelnetOptions { AllowLineMode = true });
            ReadAllChunks(stream, 2);
            connection.TakeWritten();
            stream.Write(Text("abc"), 0, 3);
            ReadAllChunks(stream, 1);
            CheckBytes(connection.TakeWritten(), Cat(B(IAC, SB, LM, 1, 4, IAC, SE), Text("abc")), "linemode: a MODE without EDIT acknowledges and sends the pending text");
            stream.Write(Text("d"), 0, 1);
            CheckBytes(connection.TakeWritten(), Text("d"), "linemode: characters go out at once again");

            FakeConnection connection2 = Marked(B(IAC, DO, LM), B(IAC, SB, LM, 1, 1, IAC, SE), B(IAC, DONT, LM));
            TelnetStream stream2 = new TelnetStream(connection2, new TelnetOptions { AllowLineMode = true });
            ReadAllChunks(stream2, 2);
            connection2.TakeWritten();
            stream2.Write(Text("xy"), 0, 2);
            ReadAllChunks(stream2, 1);
            CheckBytes(connection2.TakeWritten(), Cat(B(IAC, WONT, LM), Text("xy")), "linemode: DONT LINEMODE is answered and sends the pending text");
            Check(!stream2.LineEditing, "linemode: no editing after DONT LINEMODE");
        }
        {
            byte LM = TelnetStream.OptLineMode;
            // forward mask is declined
            FakeConnection connection = Marked(B(IAC, DO, LM), B(IAC, SB, LM, 253, 2, 0, 0, 0, IAC, SE));
            ReadAllChunks(new TelnetStream(connection, new TelnetOptions { AllowLineMode = true }), 2);
            CheckBytes(connection.TakeWritten(), B(IAC, WILL, LM, IAC, SB, LM, 252, 2, IAC, SE), "linemode: DO FORWARDMASK is declined");
        }
        {
            byte LM = TelnetStream.OptLineMode;
            // special characters: taken over for the functions we know, declined for the others
            byte[] slc = B(IAC, SB, LM, 3,
                3, 2, 0x18,      // IP -> Ctrl+X
                10, 2, 0x08,     // EC -> Backspace
                8, 2, 4,         // EOF: not supported here
                5, 0, 0,         // AYT: no support on the server's side either, nothing to answer
                IAC, SE);
            FakeConnection connection = Marked(B(IAC, DO, LM), B(IAC, SB, LM, 1, 3, IAC, SE), slc);
            TelnetStream stream = new TelnetStream(connection, new TelnetOptions { AllowLineMode = true });
            ReadAllChunks(stream, 3);
            byte[] sent = connection.TakeWritten();
            byte[] expectedTail = B(IAC, SB, LM, 3, 3, 2 | 0x80, 0x18, 10, 2 | 0x80, 0x08, 8, 0, 0, IAC, SE);
            Check(sent.Skip(sent.Length - expectedTail.Length).SequenceEqual(expectedTail), "linemode: SLC: known functions are acknowledged, unknown ones declined (got " + Show(sent) + ")");
            stream.Write(B(0x18), 0, 1);
            CheckBytes(connection.TakeWritten(), B(IAC, 244), "linemode: the server's interrupt character is trapped");
            stream.Write(B(3), 0, 1);
            stream.Write(Text("\r"), 0, 1);
            CheckBytes(connection.TakeWritten(), Cat(B(3), B(13, 10)), "linemode: the default Ctrl+C is an ordinary character once the server chose another one");
        }
        {
            byte LM = TelnetStream.OptLineMode;
            // a request for our defaults gets all of them
            FakeConnection connection = Marked(B(IAC, DO, LM), B(IAC, SB, LM, 3, 0, 3, 0, IAC, SE));
            ReadAllChunks(new TelnetStream(connection, new TelnetOptions { AllowLineMode = true }), 2);
            byte[] sent = connection.TakeWritten();
            byte[] expected = B(IAC, WILL, LM, IAC, SB, LM, 3, 3, 2, 3, 7, 2, 28, 9, 2, 26, 10, 2, 127, 11, 2, 21, 12, 2, 23, IAC, SE);
            CheckBytes(sent, expected, "linemode: SLC: the defaults are sent on request");
        }
        {
            // a long line is sent in pieces, nothing is lost
            byte LM = TelnetStream.OptLineMode;
            FakeConnection connection = Marked(B(IAC, DO, LM), B(IAC, SB, LM, 1, 1, IAC, SE));
            TelnetStream stream = new TelnetStream(connection, new TelnetOptions { AllowLineMode = true });
            ReadAllChunks(stream, 2);
            connection.TakeWritten();
            byte[] long1 = Enumerable.Repeat((byte)'a', 9000).ToArray();
            stream.Write(long1, 0, long1.Length);
            byte[] first = connection.TakeWritten();
            stream.Write(Text("\r"), 0, 1);
            byte[] rest = connection.TakeWritten();
            Check(first.Length == 8192 && rest.Length == (9000 - 8192) + 2, "linemode: an overlong line goes out in pieces, nothing is lost");
        }

        // --- saved telnet connections (settings classes) ---
        SettingsChecks.Run(Check);

        Console.WriteLine();
        Console.WriteLine(_passed + " checks passed.");
        return 0;
    }
}
