using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SmarTTY.Transport;
using VisualTTY.DeviceTerminal;

// The options added later: STARTTLS (on fake streams and against a TLS server on the loopback interface, with a client
// certificate), CHARSET, TIMING-MARK, the parameters in STATUS, NEW-ENVIRON without variables, the automatic values of
// the settings, and the output queue that keeps the reading thread going while a write is stuck.
internal static class NegotiationChecks
{
    private const byte IAC = TelnetStream.IAC;
    private const byte DO = TelnetStream.DO;
    private const byte DONT = TelnetStream.DONT;
    private const byte WILL = TelnetStream.WILL;
    private const byte WONT = TelnetStream.WONT;
    private const byte SB = TelnetStream.SB;
    private const byte SE = TelnetStream.SE;
    private const byte TLS = TelnetStream.OptStartTls;
    private const byte CS = TelnetStream.OptCharset;

    // What the "server" sends is read in the given chunks (a read waits while none is left and more may come);
    // what the client writes is kept. A write can be made to hang until it is released.
    private sealed class Wire : Stream
    {
        private readonly Queue<byte[]> _chunks = new Queue<byte[]>();
        private readonly MemoryStream _written = new MemoryStream();
        private bool _ended;
        public ManualResetEventSlim WriteGate = new ManualResetEventSlim(true);
        public int WritesStarted;

        public Wire(params byte[][] chunks)
        {
            foreach (byte[] chunk in chunks)
            {
                _chunks.Enqueue(chunk);
            }
            _ended = true;
        }

        public static Wire Open()
        {
            Wire wire = new Wire();
            wire._ended = false;
            return wire;
        }

        public void Send(byte[] chunk)
        {
            lock (_chunks)
            {
                _chunks.Enqueue(chunk);
                Monitor.PulseAll(_chunks);
            }
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
            lock (_chunks)
            {
                while (_chunks.Count == 0)
                {
                    if (_ended)
                    {
                        return 0;
                    }
                    Monitor.Wait(_chunks);
                }
                byte[] chunk = _chunks.Peek();
                int taken = Math.Min(count, chunk.Length);
                Array.Copy(chunk, 0, buffer, offset, taken);
                _chunks.Dequeue();
                if (taken < chunk.Length)
                {
                    // the rest stays first in line
                    Queue<byte[]> rest = new Queue<byte[]>();
                    rest.Enqueue(chunk.Skip(taken).ToArray());
                    while (_chunks.Count > 0)
                    {
                        rest.Enqueue(_chunks.Dequeue());
                    }
                    while (rest.Count > 0)
                    {
                        _chunks.Enqueue(rest.Dequeue());
                    }
                }
                return taken;
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Interlocked.Increment(ref WritesStarted);
            WriteGate.Wait();
            lock (_written)
            {
                _written.Write(buffer, offset, count);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Like a closed socket: a read that waits returns.
                lock (_chunks)
                {
                    _ended = true;
                    Monitor.PulseAll(_chunks);
                }
            }
            base.Dispose(disposing);
        }

        public byte[] TakeWritten()
        {
            lock (_written)
            {
                byte[] data = _written.ToArray();
                _written.SetLength(0);
                return data;
            }
        }
    }

    private static byte[] B(params byte[] bytes) => bytes;

    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();

    private static byte[] Text(string text) => Encoding.ASCII.GetBytes(text);

    private static string Show(byte[] data) => string.Join(" ", data.Select(b => b.ToString("X2")));

    private static string ReadText(Stream stream)
    {
        byte[] buffer = new byte[256];
        int count = stream.Read(buffer, 0, buffer.Length);
        return Encoding.ASCII.GetString(buffer, 0, count);
    }

    public static void Run(Action<bool, string> check)
    {
        void CheckBytes(byte[] actual, byte[] expected, string name)
        {
            check(actual.SequenceEqual(expected), name + (actual.SequenceEqual(expected) ? "" : "\n  expected: " + Show(expected) + "\n  actual:   " + Show(actual)));
        }

        // --- the reading thread goes on while a write is stuck (the deadlock of a large paste) ---
        {
            Wire wire = Wire.Open();
            TelnetStream stream = new TelnetStream(wire);
            wire.WriteGate.Reset();
            Task writer = Task.Run(() => stream.Write(Text("pasted text"), 0, 11));
            SpinWait.SpinUntil(() => wire.WritesStarted > 0, 5000);
            wire.Send(Cat(B(IAC, DO, TelnetStream.OptNaws, IAC, WILL, TelnetStream.OptEcho), Text("echo")));
            string read = null;
            Task reader = Task.Run(() => read = ReadText(stream));
            check(reader.Wait(5000) && read == "echo", "output queue: data and negotiation are read while a write hangs");
            check(!writer.IsCompleted, "output queue: ...the user's write still waits for the connection");
            wire.WriteGate.Set();
            check(writer.Wait(5000), "output queue: the write ends once the connection takes the data");
            CheckBytes(wire.TakeWritten(),
                Cat(Text("pasted text"), B(IAC, WILL, TelnetStream.OptNaws, IAC, SB, TelnetStream.OptNaws, 0, 80, 0, 24, IAC, SE, IAC, DO, TelnetStream.OptEcho)),
                "output queue: the replies follow the data that was being written, nothing is lost or mixed");
        }
        {
            Wire wire = Wire.Open();
            TelnetStream stream = new TelnetStream(wire);
            int writers = 4;
            Task[] tasks = Enumerable.Range(0, writers).Select(n => Task.Run(() =>
            {
                for (int i = 0; i < 200; i++)
                {
                    stream.Write(Text(n.ToString()), 0, 1);
                }
            })).ToArray();
            check(Task.WaitAll(tasks, 10000), "output queue: concurrent writers finish");
            byte[] all = wire.TakeWritten();
            check(all.Length == writers * 200 && Enumerable.Range(0, writers).All(n => all.Count(b => b == (byte)('0' + n)) == 200),
                "output queue: concurrent writers lose no byte");
        }

        // --- TIMING-MARK ---
        {
            Wire wire = new Wire(Cat(B(IAC, DO, TelnetStream.OptTimingMark), Text(".")), Cat(B(IAC, DO, TelnetStream.OptTimingMark), Text(".")));
            TelnetStream stream = new TelnetStream(wire);
            ReadText(stream);
            ReadText(stream);
            CheckBytes(wire.TakeWritten(), B(IAC, WILL, 6, IAC, WILL, 6), "timing-mark: every DO TIMING-MARK is answered with WILL");
        }

        // --- STATUS with parameters ---
        {
            Wire wire = new Wire(
                Cat(B(IAC, DO, TelnetStream.OptTerminalType, IAC, DO, TelnetStream.OptNaws, IAC, DO, TelnetStream.OptStatus), Text(".")),
                Cat(B(IAC, SB, TelnetStream.OptStatus, 1, IAC, SE), Text(".")));
            TelnetStream stream = new TelnetStream(wire, new TelnetOptions { TerminalType = "vt100", Columns = 255, Rows = 240 });
            ReadText(stream);
            wire.TakeWritten();
            ReadText(stream);
            CheckBytes(wire.TakeWritten(),
                Cat(B(IAC, SB, TelnetStream.OptStatus, 0, WILL, 5, WILL, 24, WILL, 31),
                    B(SB, 24, 0), Text("vt100"), B(SE),
                    B(SB, 31, 0, IAC, IAC, 0, SE, SE, SE),
                    B(IAC, SE)),
                "status: the terminal type and the window size are reported as parameters (IAC and SE in values doubled)");

            Wire asking = new Wire(Cat(B(IAC, WILL, TelnetStream.OptStatus), Text(".")),
                Cat(B(IAC, SB, TelnetStream.OptStatus, 0, DO, 31, SB, 31, 0, 132, 0, 50, SE, SB, 24, 0), Text("VT220"), B(SE, SB, 34, 1, 3, SE, IAC, SE), Text(".")));
            TelnetStream askingStream = new TelnetStream(asking);
            ReadText(askingStream);
            ReadText(askingStream);
            check(askingStream.RemoteStatus == "DO NAWS\r\nSB NAWS 132x50\r\nSB TERMINAL-TYPE IS VT220\r\nSB LINEMODE MODE EDIT TRAPSIG",
                "status: the parameters in the server's report are shown readably (" + askingStream.RemoteStatus + ")");
        }

        // --- NEW-ENVIRON without variables ---
        {
            Wire wire = new Wire(Cat(B(IAC, DO, TelnetStream.OptNewEnviron), Text(".")), Cat(B(IAC, SB, TelnetStream.OptNewEnviron, 1, 0), Text("USER"), B(IAC, SE), Text(".")));
            TelnetStream stream = new TelnetStream(wire, new TelnetOptions { AcceptEnvironmentRequests = true });
            ReadText(stream);
            CheckBytes(wire.TakeWritten(), B(IAC, WILL, TelnetStream.OptNewEnviron), "new-environ: agreed to without variables when asked to");
            ReadText(stream);
            CheckBytes(wire.TakeWritten(), Cat(B(IAC, SB, TelnetStream.OptNewEnviron, 0, 0), Text("USER"), B(IAC, SE)), "new-environ: ...and a requested variable is reported as not defined");
        }

        // --- CHARSET ---
        {
            Wire refused = new Wire(Cat(B(IAC, DO, CS, IAC, WILL, CS), Text(".")));
            ReadText(new TelnetStream(refused));
            CheckBytes(refused.TakeWritten(), B(IAC, WONT, CS, IAC, DONT, CS), "charset: refused without a character set");

            Wire wire = new Wire(
                Cat(B(IAC, DO, CS), Text(".")),
                Cat(B(IAC, SB, CS, 1), Text(";ISO-8859-1;utf8;UTF-8"), B(IAC, SE), Text(".")),
                Cat(B(IAC, SB, CS, 1), Text(" KOI8-R CP866"), B(IAC, SE), Text(".")),
                Cat(B(IAC, SB, CS, 1), Text("[TTABLE]"), B(1), Text(";UTF-8"), B(IAC, SE), Text(".")));
            TelnetStream stream = new TelnetStream(wire, new TelnetOptions { Charset = "utf-8" });
            ReadText(stream);
            CheckBytes(wire.TakeWritten(), B(IAC, WILL, CS), "charset: agreed to when the terminal has a character set");
            ReadText(stream);
            CheckBytes(wire.TakeWritten(), Cat(B(IAC, SB, CS, 2), Text("utf8"), B(IAC, SE)), "charset: the first name of the terminal's character set is accepted, in the server's spelling");
            check(stream.AcceptedCharset == "utf8" && stream.DescribeOptions().Contains("utf8"), "charset: the agreement is remembered and shown");
            ReadText(stream);
            CheckBytes(wire.TakeWritten(), B(IAC, SB, CS, 3, IAC, SE), "charset: a list without it is rejected");
            ReadText(stream);
            CheckBytes(wire.TakeWritten(), Cat(B(IAC, SB, CS, 2), Text("UTF-8"), B(IAC, SE)), "charset: a [TTABLE] request is understood");
            check(TelnetStream.NormalizeCharsetName("CP1251") == TelnetStream.NormalizeCharsetName("windows-1251")
                && TelnetStream.NormalizeCharsetName("IBM866") == TelnetStream.NormalizeCharsetName("cp866")
                && TelnetStream.NormalizeCharsetName("KOI8_R") == TelnetStream.NormalizeCharsetName("koi8-r")
                && TelnetStream.NormalizeCharsetName("US-ASCII") == TelnetStream.NormalizeCharsetName("ascii"),
                "charset: the usual aliases of the terminal's encodings are recognized");
        }

        // --- STARTTLS on fake streams ---
        {
            Wire secure = Wire.Open();
            byte[] leftover = null;
            Wire wire = new Wire(
                Cat(B(IAC, DO, TLS), Text("banner")),
                Cat(B(IAC, SB, TLS, 1, IAC, SE), Text("TLS!")));
            TelnetStream stream = new TelnetStream(wire, new TelnetOptions
            {
                StartTls = TelnetStartTlsMode.Accept,
                UpgradeToTls = raw =>
                {
                    byte[] buffer = new byte[4];
                    leftover = buffer.Take(raw.Read(buffer, 0, 4)).ToArray();
                    return secure;
                }
            });
            check(ReadText(stream) == "banner", "starttls: text before the negotiation is shown when TLS is not required");
            CheckBytes(wire.TakeWritten(), B(IAC, WILL, TLS, IAC, SB, TLS, 1, IAC, SE), "starttls: DO START_TLS is agreed to and FOLLOWS announced");
            stream.Write(Text("typed"), 0, 5);
            stream.SendCommand(TelnetStream.NOP);
            check(wire.TakeWritten().Length == 0, "starttls: nothing is sent between our FOLLOWS and the handshake");
            secure.Send(Cat(B(IAC, WILL, TelnetStream.OptEcho), Text("inside")));
            check(ReadText(stream) == "inside", "starttls: after the server's FOLLOWS the session continues in the new stream");
            check(leftover != null && Encoding.ASCII.GetString(leftover) == "TLS!", "starttls: the bytes after FOLLOWS go to the handshake");
            check(stream.StartTlsActive && stream.RemoteEcho, "starttls: the session is marked as encrypted, options are negotiated anew");
            CheckBytes(secure.TakeWritten(), Cat(Text("typed"), B(IAC, DO, TelnetStream.OptEcho)), "starttls: what was typed meanwhile goes out inside TLS");
            check(wire.TakeWritten().Length == 0, "starttls: ...and nothing more in clear text");
        }
        {
            // everything in one chunk: DO, FOLLOWS and the start of TLS
            Wire secure = Wire.Open();
            byte[] leftover = null;
            Wire wire = new Wire(Cat(B(IAC, DO, TLS, IAC, SB, TLS, 1, IAC, SE), Text("XY")));
            TelnetStream stream = new TelnetStream(wire, new TelnetOptions
            {
                StartTls = TelnetStartTlsMode.Accept,
                UpgradeToTls = raw =>
                {
                    byte[] buffer = new byte[8];
                    leftover = buffer.Take(raw.Read(buffer, 0, 8)).ToArray();
                    return secure;
                }
            });
            secure.Send(Text("ok"));
            check(ReadText(stream) == "ok" && leftover != null && Encoding.ASCII.GetString(leftover) == "XY", "starttls: DO, FOLLOWS and TLS data in one chunk");
            CheckBytes(wire.TakeWritten(), B(IAC, WILL, TLS, IAC, SB, TLS, 1, IAC, SE), "starttls: ...our FOLLOWS went out before the handshake");
        }
        {
            Wire wire = new Wire(Cat(B(IAC, DO, TLS), Text(".")));
            ReadText(new TelnetStream(wire, new TelnetOptions { StartTls = TelnetStartTlsMode.Accept }));
            CheckBytes(wire.TakeWritten(), B(IAC, WONT, TLS), "starttls: refused when there is no way to make the handshake");
            Wire off = new Wire(Cat(B(IAC, DO, TLS), Text(".")));
            ReadText(new TelnetStream(off, new TelnetOptions { UpgradeToTls = raw => raw }));
            CheckBytes(off.TakeWritten(), B(IAC, WONT, TLS), "starttls: refused unless switched on");
        }
        {
            // required, and the server says nothing: the connection is given up with the reason, and the typed text is bounded
            Wire silent = Wire.Open();
            TelnetStream stream = new TelnetStream(silent, new TelnetOptions { StartTls = TelnetStartTlsMode.Require, UpgradeToTls = raw => raw, StartTlsTimeoutMs = 300 });
            stream.Start();
            bool refused = false;
            try
            {
                stream.SendCommand(TelnetStream.NOP);
            }
            catch (InvalidOperationException)
            {
                refused = true;
            }
            check(refused, "starttls required: a command is not sent in clear text before the handshake");
            bool capped = false;
            try
            {
                byte[] block = new byte[16 * 1024];
                for (int i = 0; i < 8; i++)
                {
                    stream.Write(block, 0, block.Length);
                }
            }
            catch (IOException)
            {
                capped = true;
            }
            check(capped, "starttls required: what is typed while waiting is limited");
            string message = null;
            try
            {
                ReadText(stream);
            }
            catch (IOException exception)
            {
                message = exception.Message;
            }
            check(message != null && message.Contains("did not start STARTTLS"), "starttls required: a silent server ends the connection with a reason: " + message);
        }
        {
            // required: offered at once, a refusal ends the connection
            Wire wire = new Wire(Cat(B(IAC, DONT, TLS), Text(".")));
            TelnetStream stream = new TelnetStream(wire, new TelnetOptions { StartTls = TelnetStartTlsMode.Require, ActiveNegotiation = true, UpgradeToTls = raw => raw });
            stream.Start();
            CheckBytes(wire.TakeWritten(), B(IAC, WILL, TLS), "starttls required: only START_TLS is offered before the handshake");
            stream.Write(Text("secret"), 0, 6);
            check(wire.TakeWritten().Length == 0, "starttls required: nothing typed is sent before the handshake");
            string message = null;
            try
            {
                ReadText(stream);
            }
            catch (IOException exception)
            {
                message = exception.Message;
            }
            check(message != null && message.Contains("refused STARTTLS"), "starttls required: a refusal ends the connection");
            check(!Encoding.ASCII.GetString(wire.TakeWritten()).Contains("secret"), "starttls required: ...and what was typed is never sent");

            Wire talking = new Wire(Text("\r\n"), Text("login: "));
            TelnetStream talkingStream = new TelnetStream(talking, new TelnetOptions { StartTls = TelnetStartTlsMode.Require, UpgradeToTls = raw => raw });
            talkingStream.Start();
            message = null;
            try
            {
                ReadText(talkingStream);
            }
            catch (IOException exception)
            {
                message = exception.Message;
            }
            check(message != null && message.Contains("before the TLS handshake"), "starttls required: text before the handshake ends the connection (blank lines are tolerated)");

            Wire secure = Wire.Open();
            Wire agreeing = new Wire(Cat(B(IAC, DO, TLS, IAC, SB, TLS, 1, IAC, SE)));
            TelnetStream agreeingStream = new TelnetStream(agreeing, new TelnetOptions { StartTls = TelnetStartTlsMode.Require, ActiveNegotiation = true, UpgradeToTls = raw => secure });
            agreeingStream.Start();
            agreeing.TakeWritten();
            agreeingStream.Write(Text("user\r\n"), 0, 6);
            secure.Send(Text("$ "));
            check(ReadText(agreeingStream) == "$ ", "starttls required: the session starts once the handshake is made");
            CheckBytes(agreeing.TakeWritten(), B(IAC, SB, TLS, 1, IAC, SE), "starttls required: our WILL was accepted without a second WILL, FOLLOWS followed");
            byte[] inside = secure.TakeWritten();
            check(inside.Take(3).SequenceEqual(B(IAC, DO, TelnetStream.OptSuppressGoAhead)) && Encoding.ASCII.GetString(inside).EndsWith("user\r\n"),
                "starttls required: the active offers and the held text go out inside TLS (" + Show(inside) + ")");
        }

        // --- STARTTLS against a real TLS server, with a client certificate ---
        RunRealStartTls(check);

        // --- client certificates ---
        {
            check(TelnetTls.NormalizeThumbprint("\u200Ea9 09 50 2d d8 2a e4 14 33 e6 f8 38 86 b0 0d 42 77 a3 2a 7b") == "A909502DD82AE41433E6F83886B00D4277A32A7B",
                "client certificate: a thumbprint copied from the certificate dialog is understood");
            check(TelnetTls.NormalizeThumbprint("C:\\certs\\me.pfx") == null && TelnetTls.NormalizeThumbprint("abc") == null, "client certificate: a path is not a thumbprint");
            check(TelnetTls.LoadClientCertificate("  ") == null, "client certificate: none configured");
            string message = null;
            try
            {
                TelnetTls.LoadClientCertificate("00 11 22 33 44 55 66 77 88 99 aa bb cc dd ee ff 00 11 22 33");
            }
            catch (IOException exception)
            {
                message = exception.Message;
            }
            check(message != null && message.Contains("not in the personal certificate store"), "client certificate: an unknown thumbprint is reported");
            using X509Certificate2 certificate = CreateCertificate("CN=client");
            string publicOnly = Path.Combine(Path.GetTempPath(), "smartty-test-" + Guid.NewGuid().ToString("N") + ".cer");
            File.WriteAllBytes(publicOnly, certificate.Export(X509ContentType.Cert));
            message = null;
            try
            {
                TelnetTls.LoadClientCertificate(publicOnly);
            }
            catch (IOException exception)
            {
                message = exception.Message;
            }
            finally
            {
                File.Delete(publicOnly);
            }
            check(message != null && message.Contains("no private key"), "client certificate: a certificate without its private key is reported");
        }

        // --- settings: automatic values, STARTTLS and the client certificate ---
        {
            DeviceTerminalSettings.ConnectionSettings.Telnet telnet = new DeviceTerminalSettings.ConnectionSettings.Telnet { Host = "h", Port = 23 };
            check(telnet.SendAutomaticValues && telnet.StartTls == DeviceTerminalSettings.ConnectionSettings.Telnet.StartTlsMode.Off && !telnet.UsesTls,
                "automatic values: on by default, STARTTLS off");
            check(telnet.EffectiveTerminalSpeed() == "38400,38400", "automatic values: the default terminal speed when none is configured");
            check(telnet.EffectiveXDisplayLocation("pc:0.0") == "pc:0.0" && telnet.EffectiveXDisplayLocation(":0") == null && telnet.EffectiveXDisplayLocation(null) == null,
                "automatic values: the DISPLAY of this computer when it names a host");
            check(telnet.EffectiveEnvironmentVariables() == "", "automatic values: no variables without a login name");
            telnet.AutoLoginUserName = "admin";
            check(telnet.EffectiveEnvironmentVariables() == "USER=admin\n", "automatic values: the login name is sent as USER");
            telnet.EnvironmentVariables = "USER=other";
            check(string.Join("|", TelnetEnvironment.Parse(telnet.EffectiveEnvironmentVariables()).Select(v => v.Key + "=" + v.Value)) == "USER=admin|USER=other",
                "automatic values: a configured USER comes later and wins");
            telnet.EnvironmentVariables = null;
            telnet.AutoLoginUserName = "-froot";
            check(telnet.EffectiveEnvironmentVariables() == "", "automatic values: a login name like an option of login (-f) is never sent");
            telnet.AutoLoginUserName = "admin";
            telnet.TerminalSpeed = "9600";
            telnet.XDisplayLocation = "x:1";
            check(telnet.EffectiveTerminalSpeed() == "9600" && telnet.EffectiveXDisplayLocation("pc:0") == "x:1", "automatic values: configured values win");
            telnet.SendAutomaticValues = false;
            telnet.TerminalSpeed = null;
            telnet.XDisplayLocation = null;
            check(telnet.EffectiveTerminalSpeed() == null && telnet.EffectiveXDisplayLocation("pc:0") == null && telnet.EffectiveEnvironmentVariables() == "",
                "automatic values: switched off, only what is configured is handed over");

            DeviceTerminalSettings.ConnectionSettings.Telnet secure = new DeviceTerminalSettings.ConnectionSettings.Telnet
            {
                Host = "h",
                Port = 23,
                StartTls = DeviceTerminalSettings.ConnectionSettings.Telnet.StartTlsMode.Required,
                TlsClientCertificate = "A909502DD82AE41433E6F83886B00D4277A32A7B",
                SendAutomaticValues = false
            };
            System.Xml.Serialization.XmlSerializer serializer = new System.Xml.Serialization.XmlSerializer(typeof(RawConnectionSettings));
            StringWriter writer = new StringWriter();
            serializer.Serialize(writer, new RawConnectionSettings { Settings = new DeviceTerminalSettings { Connection = secure } });
            RawConnectionSettings loaded = (RawConnectionSettings)serializer.Deserialize(new StringReader(writer.ToString()));
            DeviceTerminalSettings.ConnectionSettings.Telnet back = (DeviceTerminalSettings.ConnectionSettings.Telnet)loaded.Settings.Connection;
            check(back.StartTls == DeviceTerminalSettings.ConnectionSettings.Telnet.StartTlsMode.Required && back.TlsClientCertificate == secure.TlsClientCertificate
                && !back.SendAutomaticValues && back.UsesTls, "settings: STARTTLS, the client certificate and the automatic values survive the round trip");
            string old = writer.ToString();
            int start = old.IndexOf("<StartTls>", StringComparison.Ordinal);
            int end = old.IndexOf("</SendAutomaticValues>", StringComparison.Ordinal) + "</SendAutomaticValues>".Length;
            DeviceTerminalSettings.ConnectionSettings.Telnet older = (DeviceTerminalSettings.ConnectionSettings.Telnet)((RawConnectionSettings)serializer.Deserialize(
                new StringReader(old.Remove(start, end - start)))).Settings.Connection;
            check(start > 0 && older.StartTls == DeviceTerminalSettings.ConnectionSettings.Telnet.StartTlsMode.Off && older.SendAutomaticValues && older.TlsClientCertificate == null,
                "settings: a profile saved before these settings existed gets STARTTLS off and the automatic values");
        }
    }

    private static void RunRealStartTls(Action<bool, string> check)
    {
        using X509Certificate2 serverCertificate = CreateCertificate("CN=localhost");
        using X509Certificate2 clientCertificate = CreateCertificate("CN=telnet-client");
        string pfx = Path.Combine(Path.GetTempPath(), "smartty-test-" + Guid.NewGuid().ToString("N") + ".pfx");
        File.WriteAllBytes(pfx, clientCertificate.Export(X509ContentType.Pfx));
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string clientSubject = null;
        byte[] plainFromClient = null;
        byte[] secureFromClient = null;
        Task server = Task.Run(() =>
        {
            using TcpClient client = listener.AcceptTcpClient();
            NetworkStream plain = client.GetStream();
            plain.Write(new byte[] { 255, 253, 46 }, 0, 3);
            plainFromClient = ReadExactly(plain, 9);
            plain.Write(new byte[] { 255, 250, 46, 1, 255, 240 }, 0, 6);
            using SslStream ssl = new SslStream(plain, false, (sender, certificate, chain, errors) => true);
            ssl.AuthenticateAsServer(serverCertificate, clientCertificateRequired: true, checkCertificateRevocation: false);
            clientSubject = ssl.RemoteCertificate?.Subject;
            byte[] hello = Concat(new byte[] { 255, 253, 31 }, Encoding.ASCII.GetBytes("secure login: "));
            ssl.Write(hello, 0, hello.Length);
            ssl.Flush();
            secureFromClient = ReadExactly(ssl, 18);
        });
        try
        {
            using TcpClient client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            X509Certificate2 loaded = TelnetTls.LoadClientCertificate(pfx);
            check(loaded != null && loaded.HasPrivateKey, "client certificate: a .pfx file without a password is loaded with its key");
            using TelnetStream telnet = new TelnetStream(client.GetStream(), new TelnetOptions
            {
                StartTls = TelnetStartTlsMode.Accept,
                UpgradeToTls = raw => TelnetTls.Authenticate(raw, "localhost", allowInvalidCertificate: true, clientCertificate: loaded)
            });
            string text = ReadText(telnet);
            check(text == "secure login: ", "starttls (real TLS): the text after the handshake is decrypted (" + text + ")");
            check(telnet.StartTlsActive && telnet.InnerStream is SslStream ssl && ssl.IsEncrypted, "starttls (real TLS): the session runs in TLS");
            check(plainFromClient != null && plainFromClient.SequenceEqual(new byte[] { 255, 251, 46, 255, 250, 46, 1, 255, 240 }), "starttls (real TLS): WILL and FOLLOWS were sent in clear text, nothing more");
            telnet.Write(Text("user\r"), 0, 5);
            check(server.Wait(10000), "starttls (real TLS): the server got its data");
            check(clientSubject == "CN=telnet-client", "starttls (real TLS): the client certificate was presented (" + clientSubject + ")");
            check(secureFromClient != null && secureFromClient.SequenceEqual(Cat(B(255, 251, 31, 255, 250, 31, 0, 80, 0, 24, 255, 240), Text("user\r\0"))),
                "starttls (real TLS): the answers and the typed text travel encrypted");
        }
        finally
        {
            listener.Stop();
            File.Delete(pfx);
        }
    }

    private static byte[] ReadExactly(Stream stream, int count)
    {
        byte[] data = new byte[count];
        int done = 0;
        while (done < count)
        {
            int read = stream.Read(data, done, count - done);
            if (read <= 0)
            {
                break;
            }
            done += read;
        }
        return data.Take(done).ToArray();
    }

    private static X509Certificate2 CreateCertificate(string subject)
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        SubjectAlternativeNameBuilder names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return new X509Certificate2(certificate.Export(X509ContentType.Pfx));
    }

    private static byte[] Concat(byte[] first, byte[] second)
    {
        byte[] result = new byte[first.Length + second.Length];
        Buffer.BlockCopy(first, 0, result, 0, first.Length);
        Buffer.BlockCopy(second, 0, result, first.Length, second.Length);
        return result;
    }
}
