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

        // --- saved telnet connections (settings classes) ---
        SettingsChecks.Run(Check);

        Console.WriteLine();
        Console.WriteLine(_passed + " checks passed.");
        return 0;
    }
}
