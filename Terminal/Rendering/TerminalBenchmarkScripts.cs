using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SmarTTY.Terminal.Rendering;

/// <summary>
/// Generated output for measuring a terminal view: every script is a list of chunks, as a connection would deliver them, and is the same
/// on every run (a fixed seed), so two runs, two views or two machines (local, Remote Desktop, a virtual machine) can be compared.
/// </summary>
public static class TerminalBenchmarkScripts
{
    /// <summary>The names of the scripts, in the order of the lab window.</summary>
    public static readonly string[] Names =
    {
        "Scrolling log (plain text)",
        "Colour flood (16, 256 and 24-bit colours)",
        "Full-screen redraw (like htop)",
        "Unicode (Cyrillic, box drawing, CJK, emoji)",
        "Tiny chunks (many reads of a few bytes)"
    };

    /// <summary>The chunks of a script for a screen of the given size.</summary>
    public static List<string> Create(int script, int columns, int rows)
    {
        columns = Math.Max(columns, 20);
        rows = Math.Max(rows, 5);
        Random random = new Random(12345 + script);
        switch (script)
        {
            case 0:
                return ScrollingLog(random, columns);
            case 1:
                return ColourFlood(random, columns);
            case 2:
                return FullScreenRedraw(random, columns, rows);
            case 3:
                return Unicode(random, columns);
            case 4:
                return TinyChunks(random, columns);
        }
        throw new ArgumentOutOfRangeException(nameof(script));
    }

    private const string Words = "lorem ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod tempor incididunt ut labore et dolore magna aliqua";

    private static string RandomText(Random random, int length)
    {
        StringBuilder builder = new StringBuilder(length);
        string[] words = Words.Split(' ');
        while (builder.Length < length)
        {
            if (builder.Length != 0)
            {
                builder.Append(' ');
            }
            builder.Append(words[random.Next(words.Length)]);
        }
        builder.Length = length;
        return builder.ToString();
    }

    // Chunks of about 4 KB, as SSH delivers a burst of output.
    private static List<string> Pack(IEnumerable<string> lines, int chunkSize = 4096)
    {
        List<string> chunks = new List<string>();
        StringBuilder builder = new StringBuilder();
        foreach (string line in lines)
        {
            builder.Append(line);
            if (builder.Length >= chunkSize)
            {
                chunks.Add(builder.ToString());
                builder.Clear();
            }
        }
        if (builder.Length != 0)
        {
            chunks.Add(builder.ToString());
        }
        return chunks;
    }

    private static List<string> ScrollingLog(Random random, int columns)
    {
        List<string> lines = new List<string>();
        for (int index = 0; index < 20000; index++)
        {
            string time = (index / 3600 % 24).ToString("00") + ":" + (index / 60 % 60).ToString("00") + ":" + (index % 60).ToString("00");
            lines.Add(time + " daemon[" + (1000 + index % 97).ToString(CultureInfo.InvariantCulture) + "]: " + RandomText(random, random.Next(10, columns - 25)) + "\r\n");
        }
        return Pack(lines);
    }

    private static List<string> ColourFlood(Random random, int columns)
    {
        List<string> lines = new List<string>();
        for (int index = 0; index < 5000; index++)
        {
            StringBuilder line = new StringBuilder();
            int column = 0;
            while (column < columns - 12)
            {
                switch (random.Next(4))
                {
                    case 0:
                        line.Append("\u001b[").Append(30 + random.Next(8)).Append(random.Next(2) == 0 ? ";1" : "").Append('m');
                        break;
                    case 1:
                        line.Append("\u001b[38;5;").Append(random.Next(256)).Append('m');
                        break;
                    case 2:
                        line.Append("\u001b[48;5;").Append(random.Next(256)).Append('m');
                        break;
                    default:
                        line.Append("\u001b[38;2;").Append(random.Next(256)).Append(';').Append(random.Next(256)).Append(';').Append(random.Next(256)).Append('m');
                        break;
                }
                string word = RandomText(random, random.Next(2, 9));
                line.Append(word).Append("\u001b[0m ");
                column += word.Length + 1;
            }
            line.Append("\r\n");
            lines.Add(line.ToString());
        }
        return Pack(lines);
    }

    private static List<string> FullScreenRedraw(Random random, int columns, int rows)
    {
        List<string> chunks = new List<string> { "\u001b[?1049h\u001b[?25l\u001b[2J" };
        for (int frame = 0; frame < 300; frame++)
        {
            StringBuilder screen = new StringBuilder();
            screen.Append("\u001b[H\u001b[7m");
            string header = (" CPU " + (frame % 100).ToString("00") + "%  tasks " + (120 + frame % 7) + "  frame " + frame).PadRight(columns);
            screen.Append(header.Substring(0, columns)).Append("\u001b[0m");
            for (int row = 2; row <= rows; row++)
            {
                screen.Append("\u001b[").Append(row).Append(";1H");
                int load = random.Next(columns / 2);
                screen.Append("\u001b[32m").Append(new string('|', load / 3)).Append("\u001b[33m").Append(new string('|', load / 3))
                    .Append("\u001b[31m").Append(new string('|', load - 2 * (load / 3))).Append("\u001b[0m");
                screen.Append("\u001b[K");
                screen.Append("\u001b[").Append(row).Append(';').Append(columns / 2 + 2).Append('H');
                screen.Append(RandomText(random, columns / 2 - 2));
            }
            chunks.Add(screen.ToString());
        }
        chunks.Add("\u001b[?25h\u001b[?1049l");
        return chunks;
    }

    private static List<string> Unicode(Random random, int columns)
    {
        string[] samples =
        {
            "Привет, мир! Это тест кириллицы.",
            "┌──┬──┐ │ab│cd│ └──┴──┘ █▓▒░",
            "\u001b(0lqqwqqk x  x  x mqqvqqj\u001b(B (DEC line drawing)",
            "日本語 中文 한국어",
            "😀 🚀 ❤ ✔ ☃",
            "éèê üöä ß ščř ąęł"
        };
        List<string> lines = new List<string>();
        for (int index = 0; index < 4000; index++)
        {
            string sample = samples[random.Next(samples.Length)];
            lines.Add("\u001b[3" + (1 + index % 7) + "m" + (index % 1000).ToString("000") + "\u001b[0m " + sample + "\r\n");
        }
        return Pack(lines);
    }

    private static List<string> TinyChunks(Random random, int columns)
    {
        List<string> chunks = new List<string>();
        for (int line = 0; line < 3000; line++)
        {
            string text = RandomText(random, random.Next(10, columns - 2)) + "\r\n";
            int offset = 0;
            while (offset < text.Length)
            {
                int length = Math.Min(text.Length - offset, random.Next(1, 8));
                chunks.Add(text.Substring(offset, length));
                offset += length;
            }
        }
        return chunks;
    }

    /// <summary>The number of characters of a script.</summary>
    public static long Length(List<string> chunks)
    {
        long length = 0;
        foreach (string chunk in chunks)
        {
            length += chunk.Length;
        }
        return length;
    }
}
