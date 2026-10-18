using System;
using System.IO;
using System.Linq;
using System.Text;
using SmarTTY.Terminal;

// Regression harness for the continuous COM/TCP log: the three file formats, appending, encoding changes
// and failure reporting. Run with: dotnet run --project tests/TerminalLogWriterTests
internal static class Program
{
    private static int _passed;
    private static string _root;

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static string NewPath(string name)
    {
        return Path.Combine(_root, name + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
    }

    private static byte[] Hex(string hex)
    {
        return hex.Split(' ').Select(part => Convert.ToByte(part, 16)).ToArray();
    }

    // Reads the file while the writer still has it open, which is what "tail" or a text editor does.
    private static byte[] ReadWhileOpen(string path)
    {
        using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using MemoryStream memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static void Write(TerminalLogWriter writer, byte[] bytes)
    {
        writer.Write(bytes, 0, bytes.Length);
    }

    private static int Main()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _root = Path.Combine(Path.GetTempPath(), "smartty-log-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_root);
        try
        {
            Run();
        }
        finally
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch
            {
            }
        }
        Console.WriteLine();
        Console.WriteLine(_passed + " checks passed");
        return 0;
    }

    private static void Run()
    {
        // --- raw bytes ---
        string rawPath = NewPath("raw");
        byte[] binary = Hex("00 01 FF FE 0D 0A 7F 80");
        using (TerminalLogWriter raw = new TerminalLogWriter(rawPath, TerminalLogFormat.RawBytes, Encoding.UTF8, append: false))
        {
            Write(raw, binary.Take(3).ToArray());
            Write(raw, binary.Skip(3).ToArray());
            Check(raw.BytesWritten == binary.Length, "raw: BytesWritten counts the file bytes");
        }
        Check(File.ReadAllBytes(rawPath).SequenceEqual(binary), "raw: bytes are stored exactly as received, in order");

        // --- text: UTF-8 characters split between reads ---
        const string hello = "Привет, мир!\r\nline 2\r\n";
        byte[] utf8 = Encoding.UTF8.GetBytes(hello);
        string textPath = NewPath("text");
        using (TerminalLogWriter text = new TerminalLogWriter(textPath, TerminalLogFormat.Text, Encoding.UTF8, append: false))
        {
            for (int i = 0; i < utf8.Length; i++)
            {
                text.Write(utf8, i, 1);
            }
        }
        byte[] textBytes = File.ReadAllBytes(textPath);
        Check(textBytes.SequenceEqual(utf8), "text: one byte per read gives the original UTF-8 text");
        Check(!(textBytes.Length >= 3 && textBytes[0] == 0xEF && textBytes[1] == 0xBB && textBytes[2] == 0xBF), "text: no BOM is written");

        // --- text: legacy code page is converted to UTF-8 in the file ---
        string cpPath = NewPath("cp1251");
        using (TerminalLogWriter cp = new TerminalLogWriter(cpPath, TerminalLogFormat.Text, Encoding.GetEncoding("windows-1251"), append: false))
        {
            Write(cp, Hex("CF F0 E8 E2 E5 F2"));
        }
        Check(File.ReadAllBytes(cpPath).SequenceEqual(Hex("D0 9F D1 80 D0 B8 D0 B2 D0 B5 D1 82")), "text: Windows-1251 input is stored as UTF-8");

        // --- text: encoding changed while recording ---
        string switchPath = NewPath("switch");
        using (TerminalLogWriter sw = new TerminalLogWriter(switchPath, TerminalLogFormat.Text, Encoding.UTF8, append: false))
        {
            Write(sw, Hex("D0 9F"));                              // "П" in UTF-8
            sw.SetEncoding(Encoding.GetEncoding("windows-1251"));
            Write(sw, Hex("F0"));                                 // "р" in Windows-1251
        }
        Check(File.ReadAllText(switchPath, Encoding.UTF8) == "Пр", "text: SetEncoding applies to the data that follows");

        // --- text: a half-received character is dropped after a reconnect ---
        string resetPath = NewPath("reset");
        using (TerminalLogWriter reset = new TerminalLogWriter(resetPath, TerminalLogFormat.Text, Encoding.UTF8, append: false))
        {
            Write(reset, Hex("D0"));                              // first byte of "П", the rest never arrives
            reset.ResetDecoder();
            Write(reset, Hex("41"));
        }
        Check(File.ReadAllText(resetPath, Encoding.UTF8) == "A", "text: ResetDecoder drops a half-received character");

        // --- hex dump ---
        string hexPath = NewPath("hex");
        using (TerminalLogWriter dump = new TerminalLogWriter(hexPath, TerminalLogFormat.HexDump, Encoding.UTF8, append: false))
        {
            Write(dump, Encoding.ASCII.GetBytes("ABCDEFGHIJKLMNOPQR"));
            Write(dump, Hex("00 FF 0A"));
        }
        string expectedDump =
            "00000000  41 42 43 44 45 46 47 48  49 4A 4B 4C 4D 4E 4F 50  |ABCDEFGHIJKLMNOP|\r\n" +
            "00000010  51 52                                             |QR|\r\n" +
            "00000012  00 FF 0A                                          |...|\r\n";
        Check(File.ReadAllText(hexPath, Encoding.ASCII) == expectedDump, "hex dump: layout, padding and offsets across chunks");

        // --- appending versus replacing ---
        string appendPath = NewPath("append");
        File.WriteAllBytes(appendPath, Encoding.ASCII.GetBytes("OLD\n"));
        using (TerminalLogWriter appended = new TerminalLogWriter(appendPath, TerminalLogFormat.Text, Encoding.UTF8, append: true))
        {
            Write(appended, Encoding.ASCII.GetBytes("new\n"));
        }
        Check(File.ReadAllText(appendPath) == "OLD\nnew\n", "append: existing content is kept");
        using (TerminalLogWriter replaced = new TerminalLogWriter(appendPath, TerminalLogFormat.Text, Encoding.UTF8, append: false))
        {
            Write(replaced, Encoding.ASCII.GetBytes("fresh\n"));
        }
        Check(File.ReadAllText(appendPath) == "fresh\n", "replace: existing content is overwritten");

        // --- the file is up to date while recording (flush on every write) ---
        string livePath = NewPath("live");
        using (TerminalLogWriter live = new TerminalLogWriter(livePath, TerminalLogFormat.RawBytes, Encoding.UTF8, append: false))
        {
            Write(live, Hex("41 42"));
            Check(ReadWhileOpen(livePath).SequenceEqual(Hex("41 42")), "live: data is readable by other programs right after each write");
            Write(live, Hex("43"));
            Check(ReadWhileOpen(livePath).SequenceEqual(Hex("41 42 43")), "live: the next chunk is visible as well");
        }

        // --- empty reads and use after Dispose ---
        string idlePath = NewPath("idle");
        TerminalLogWriter idle = new TerminalLogWriter(idlePath, TerminalLogFormat.RawBytes, Encoding.UTF8, append: false);
        idle.Write(new byte[4], 0, 0);
        Check(idle.BytesWritten == 0 && idle.IsActive, "an empty read writes nothing");
        idle.Dispose();
        idle.Write(new byte[] { 1 }, 0, 1);
        idle.Dispose();
        Check(!idle.IsActive && idle.Error == null && File.ReadAllBytes(idlePath).Length == 0, "Write after Dispose is ignored and Dispose is repeatable");

        // --- failure is reported once and stops the recording ---
        string failPath = NewPath("fail");
        TerminalLogWriter failing = new TerminalLogWriter(failPath, TerminalLogFormat.RawBytes, Encoding.UTF8, append: false);
        int failures = 0;
        Exception reported = null;
        failing.Failed += error =>
        {
            failures++;
            reported = error;
        };
        failing.Write(new byte[2], 5, 3);   // an invalid range makes the file write throw
        Check(failures == 1 && reported != null && ReferenceEquals(failing.Error, reported), "failure: Failed is raised with the error");
        Check(!failing.IsActive, "failure: recording stops");
        failing.Write(new byte[] { 1 }, 0, 1);
        Check(failures == 1, "failure: later writes are ignored and do not raise Failed again");
        failing.Dispose();

        // --- the file name is validated ---
        bool threw = false;
        try
        {
            new TerminalLogWriter("  ", TerminalLogFormat.Text, Encoding.UTF8, append: true).Dispose();
        }
        catch (ArgumentException)
        {
            threw = true;
        }
        Check(threw, "an empty file name is rejected");
    }
}
