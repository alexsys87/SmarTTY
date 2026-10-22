using System;
using System.Linq;
using System.Text;
using SmarTTY.Terminal;
using VisualTTY.DeviceTerminal;

// Regression harness for the COM/TCP text path: chunked decoding of Cyrillic in several encodings and the
// encoding name resolver. Run with: dotnet run --project tests/TextStreamDecoderTests
internal static class Program
{
    private static int _passed;

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static byte[] Hex(string hex)
    {
        return hex.Split(' ').Select(part => Convert.ToByte(part, 16)).ToArray();
    }

    private static string DecodeInChunks(TextStreamDecoder decoder, byte[] data, int chunkSize)
    {
        StringBuilder result = new StringBuilder();
        for (int offset = 0; offset < data.Length; offset += chunkSize)
        {
            int count = Math.Min(chunkSize, data.Length - offset);
            result.Append(decoder.Decode(data, offset, count));
        }
        return result.ToString();
    }

    private static int Main()
    {
        // The application targets .NET Framework, where these code pages are built in; modern .NET needs the provider.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        const string hello = "Привет, мир! Hello, world.";
        byte[] utf8 = Encoding.UTF8.GetBytes(hello);

        // --- UTF-8: characters split between reads ---
        Check(new TextStreamDecoder(Encoding.UTF8).Decode(utf8, 0, utf8.Length) == hello, "UTF-8: whole buffer");
        Check(DecodeInChunks(new TextStreamDecoder(Encoding.UTF8), utf8, 1) == hello, "UTF-8: one byte per read");
        Check(DecodeInChunks(new TextStreamDecoder(Encoding.UTF8), utf8, 3) == hello, "UTF-8: three bytes per read");
        bool everySplit = true;
        for (int split = 1; split < utf8.Length; split++)
        {
            TextStreamDecoder decoder = new TextStreamDecoder(Encoding.UTF8);
            string text = decoder.Decode(utf8, 0, split) + decoder.Decode(utf8, split, utf8.Length - split);
            everySplit &= text == hello;
        }
        Check(everySplit, "UTF-8: split at every possible byte position");

        TextStreamDecoder partial = new TextStreamDecoder(Encoding.UTF8);
        Check(partial.Decode(new byte[] { 0xD0 }, 0, 1) == "", "UTF-8: a lone lead byte is held back");
        Check(partial.Decode(new byte[] { 0x9F }, 0, 1) == "П", "UTF-8: the continuation byte completes the character");

        byte[] withOffset = new byte[] { 0xFF, 0xFF }.Concat(utf8).ToArray();
        Check(new TextStreamDecoder(Encoding.UTF8).Decode(withOffset, 2, utf8.Length) == hello, "UTF-8: non-zero offset");
        Check(new TextStreamDecoder(Encoding.UTF8).Decode(utf8, 0, 0) == "", "UTF-8: empty read");

        // --- byte mode (a device shell counting UTF-8 bytes, BusyBox on OpenWrt): stray bytes are kept, escaped ---
        TextStreamDecoder escaping = new TextStreamDecoder(Encoding.UTF8) { EscapeInvalidBytes = true };
        Check(escaping.Decode(new byte[] { 0xD0, 0xD0, 0xB5, 0x96 }, 0, 4) == "\uDCD0\u0435\uDC96", "byte mode: each stray byte arrives escaped, the letter between them intact");
        Check(DecodeInChunks(new TextStreamDecoder(Encoding.UTF8) { EscapeInvalidBytes = true }, utf8, 1) == hello, "byte mode: valid text split byte by byte still decodes");
        Check(new TextStreamDecoder(Encoding.UTF8).Decode(new byte[] { 0xD0, 0x41 }, 0, 2) == "\uFFFDA", "without byte mode a stray byte is U+FFFD");
        TextStreamDecoder cp1251Escaping = new TextStreamDecoder(Encoding.GetEncoding(1251)) { EscapeInvalidBytes = true };
        Check(cp1251Escaping.Decode(Encoding.GetEncoding(1251).GetBytes("Привет"), 0, 6) == "Привет", "byte mode does not change a single-byte code page");

        // --- invalid data and reset ---
        Check(new TextStreamDecoder(Encoding.UTF8).Decode(new byte[] { 0x61, 0xFF, 0x62 }, 0, 3) == "a�b", "UTF-8: invalid byte is replaced, not thrown");

        TextStreamDecoder reset = new TextStreamDecoder(Encoding.UTF8);
        reset.Decode(new byte[] { 0xD0 }, 0, 1);
        reset.Reset();
        Check(reset.Decode(new byte[] { 0x41 }, 0, 1) == "A", "Reset drops a half-received character");

        // --- legacy Cyrillic code pages ("Привет") ---
        const string word = "Привет";
        Check(new TextStreamDecoder(Encoding.GetEncoding("windows-1251")).Decode(Hex("CF F0 E8 E2 E5 F2"), 0, 6) == word, "Windows-1251 decodes Cyrillic");
        Check(new TextStreamDecoder(Encoding.GetEncoding("cp866")).Decode(Hex("8F E0 A8 A2 A5 E2"), 0, 6) == word, "CP866 decodes Cyrillic");
        Check(new TextStreamDecoder(Encoding.GetEncoding("koi8-r")).Decode(Hex("F0 D2 C9 D7 C5 D4"), 0, 6) == word, "KOI8-R decodes Cyrillic");
        Check(new TextStreamDecoder(Encoding.GetEncoding("iso-8859-5")).Decode(Hex("BF E0 D8 D2 D5 E2"), 0, 6) == word, "ISO 8859-5 decodes Cyrillic");
        Check(Encoding.UTF8.GetBytes(word).SequenceEqual(Hex("D0 9F D1 80 D0 B8 D0 B2 D0 B5 D1 82")), "UTF-8 reference bytes");

        // 7-bit ASCII is what the COM terminal used before: high bytes become '?'
        Check(new TextStreamDecoder(Encoding.ASCII).Decode(new byte[] { 0xD0, 0x9F }, 0, 2) == "??", "ASCII: high bytes become '?'");

        // --- the names offered in the UI ---
        Check(TerminalEncodings.Resolve(null).CodePage == 65001, "Resolve(null) falls back to UTF-8");
        Check(TerminalEncodings.Resolve("").CodePage == 65001, "Resolve(empty) falls back to UTF-8");
        Check(TerminalEncodings.Resolve("no-such-encoding").CodePage == 65001, "Resolve(unknown) falls back to UTF-8");
        Check(TerminalEncodings.Resolve("windows-1251").CodePage == 1251, "Resolve windows-1251");
        Check(TerminalEncodings.Resolve("cp866").CodePage == 866, "Resolve cp866");
        Check(TerminalEncodings.Resolve("koi8-r").CodePage == 20866, "Resolve koi8-r");
        Check(TerminalEncodings.Resolve("iso-8859-5").CodePage == 28595, "Resolve iso-8859-5");
        Check(TerminalEncodings.Resolve("us-ascii").CodePage == 20127, "Resolve us-ascii");
        Check(TerminalEncodings.Resolve(TerminalEncodings.DefaultName).CodePage == 65001, "the default name is UTF-8");
        Check(TerminalEncodings.All.Any(choice => choice.Name == TerminalEncodings.DefaultName), "the default is offered in the list");
        Check(TerminalEncodings.All.Select(choice => choice.Name).Distinct().Count() == TerminalEncodings.All.Count, "encoding names are unique");

        foreach (TerminalEncodings.Choice choice in TerminalEncodings.All.Where(c => c.Name != "us-ascii"))
        {
            Encoding encoding = TerminalEncodings.Resolve(choice.Name);
            byte[] bytes = encoding.GetBytes(hello);
            Check(DecodeInChunks(new TextStreamDecoder(encoding), bytes, 1) == hello,
                choice.DisplayName + ": text survives encode + one-byte-per-read decode");
            Check(encoding.GetString(encoding.GetBytes(word)) == word, choice.DisplayName + ": Cyrillic round trip");
        }

        Console.WriteLine();
        Console.WriteLine(_passed + " checks passed");
        return 0;
    }
}
