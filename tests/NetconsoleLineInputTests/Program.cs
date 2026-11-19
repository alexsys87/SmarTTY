using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using VisualTTY.DeviceTerminal;

// Checks of the line input of a netconsole tab (VisualTTY/DeviceTerminal/NetconsoleLineInput.cs): the history walk with its draft, and what typed text does to the
// input box (letters, Enter, Backspace, control characters). The panel is a fake that keeps the box as a string.
internal static class Program
{
    private sealed class FakeHost : INetconsoleLineHost
    {
        public string Box = "";
        public readonly List<string> Sent = new List<string>();
        public readonly List<string> Log = new List<string>();
        public List<string> HistoryLines = new List<string>();

        string INetconsoleLineHost.InputText => Box;

        void INetconsoleLineHost.SetInputText(string text)
        {
            Box = text;
            Log.Add("set " + text);
        }

        void INetconsoleLineHost.SendLine()
        {
            Sent.Add(Box);
            Log.Add("send");
            Box = "";
        }

        void INetconsoleLineHost.SendBytes(byte[] bytes)
        {
            Log.Add("bytes " + string.Join(",", bytes));
        }

        // Not the plain bytes of the character, so that a check can tell that the connection's encoding was used.
        byte[] INetconsoleLineHost.Encode(string text) => Encoding.UTF8.GetBytes(text).Select((byte b) => (byte)(b + 100)).ToArray();

        IReadOnlyList<string> INetconsoleLineHost.History => HistoryLines;
    }

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        Console.WriteLine("PASS: " + name);
    }

    private static void Equal<T>(T actual, T expected, string name)
    {
        bool same = EqualityComparer<T>.Default.Equals(actual, expected);
        Check(same, name + (same ? "" : " (expected '" + expected + "', got '" + actual + "')"));
    }

    private static void HistoryChecks()
    {
        FakeHost host = new FakeHost { HistoryLines = new List<string> { "h1", "h2", "h3" }, Box = "typed" };
        NetconsoleLineInput input = new NetconsoleLineInput(host);
        input.Newer();
        Equal(host.Box, "typed", "Down below the newest line changes nothing");
        Equal(host.Log.Count, 0, "and the box is not even set");
        input.Older();
        Equal(host.Box, "h3", "Up brings the newest line");
        input.Older();
        Equal(host.Box, "h2", "and the one before it");
        input.Older();
        Equal(host.Box, "h1", "and the oldest");
        host.Log.Clear();
        input.Older();
        Check(host.Box == "h1" && host.Log.Count == 0, "Up at the oldest line changes nothing");
        input.Newer();
        Equal(host.Box, "h2", "Down goes to the newer line");
        input.Newer();
        Equal(host.Box, "h3", "and on");
        input.Newer();
        Equal(host.Box, "typed", "past the newest line it brings the draft back");
        host.Log.Clear();
        input.Newer();
        Check(host.Box == "typed" && host.Log.Count == 0, "and Down there changes nothing again");

        // The draft is the box as it was when the walk began; Reset makes the next walk take it again.
        input.Older();
        Equal(host.Box, "h3", "a new walk starts from the newest line");
        host.Box = "other";
        input.Reset();
        input.Older();
        Equal(host.Box, "h3", "after Reset the walk starts from the newest line again");
        input.Newer();
        Equal(host.Box, "other", "and the draft is what the box held when it began");

        // Empty history.
        host = new FakeHost { Box = "x" };
        input = new NetconsoleLineInput(host);
        input.Older();
        input.Newer();
        Check(host.Box == "x" && host.Log.Count == 0, "with no history nothing happens");

        // A line is added to the history while the walk is at its end (Down went past the newest line): the next Up shows the new newest line.
        host = new FakeHost { HistoryLines = new List<string> { "a", "b" }, Box = "t" };
        input = new NetconsoleLineInput(host);
        input.Older();
        input.Newer();
        host.HistoryLines = new List<string> { "a", "b", "c" };
        input.Older();
        Equal(host.Box, "c", "at the end of the walk the place is 'below the newest line', whatever the history has grown to");

        // The history became shorter than the place of the walk: the box is taken for what is being typed.
        host = new FakeHost { HistoryLines = new List<string> { "a", "b", "c" }, Box = "d" };
        input = new NetconsoleLineInput(host);
        input.Older();
        host.HistoryLines = new List<string> { "a" };
        input.Older();
        Equal(host.Box, "a", "a place past the end of a shorter history is the end");
        input.Newer();
        Equal(host.Box, "c", "Down from there shows the draft, which was taken from the box ('c') at that Up");
    }

    private static void ProcessChecks()
    {
        FakeHost host = new FakeHost();
        NetconsoleLineInput input = new NetconsoleLineInput(host);
        input.Process("");
        Equal(host.Log.Count, 0, "no text, nothing done");

        input.Process("abc");
        Equal(string.Join("|", host.Log), "set abc", "letters are added to the box once");

        host.Log.Clear();
        input.Process("d\r");
        Equal(string.Join("|", host.Log), "set abcd|send", "Enter sends the box with what was typed before it");
        Equal(host.Sent[0], "abcd", "the line that was sent");

        host = new FakeHost();
        input = new NetconsoleLineInput(host);
        input.Process("a\r\nb\r");
        Equal(string.Join("|", host.Sent), "a|b", "CR and LF together are one line ending");
        host = new FakeHost();
        input = new NetconsoleLineInput(host);
        input.Process("a\nb\n");
        Equal(string.Join("|", host.Sent), "a|b", "a lone LF is a line ending");
        host = new FakeHost();
        input = new NetconsoleLineInput(host);
        input.Process("a\n\nb");
        Equal(string.Join("|", host.Sent), "a|", "two LF are two line endings (the second sends an empty box)");
        Equal(host.Box, "b", "and the letter after them is in the box");
        host = new FakeHost();
        input = new NetconsoleLineInput(host);
        input.Process("a\n\r");
        Equal(host.Sent.Count, 2, "LF and then CR are two line endings");
        host = new FakeHost();
        input = new NetconsoleLineInput(host);
        input.Process("\r\n");
        Equal(host.Sent.Count, 1, "CR and LF alone are one");

        // Backspace.
        host = new FakeHost { Box = "xy" };
        input = new NetconsoleLineInput(host);
        input.Process("\b");
        Check(host.Box == "x" && string.Join("|", host.Log) == "set x", "Backspace takes the last letter of the box");
        host = new FakeHost { Box = "xy" };
        input = new NetconsoleLineInput(host);
        input.Process("\u007f");
        Equal(host.Box, "x", "so does 0x7f");
        host = new FakeHost { Box = "xy" };
        input = new NetconsoleLineInput(host);
        input.Process("ab\bc");
        Check(host.Box == "xyac" && string.Join("|", host.Log) == "set xyac", "it takes a letter typed in the same text first, and the rest is added once");
        host = new FakeHost();
        input = new NetconsoleLineInput(host);
        input.Process("\b\b");
        Equal(host.Log.Count, 0, "in an empty box it does nothing");
        host = new FakeHost { Box = "ab" };
        input = new NetconsoleLineInput(host);
        input.Process("\b\b\b");
        Check(host.Box == "" && host.Log.Count == 2, "it stops at the start of the box");
        host = new FakeHost();
        input = new NetconsoleLineInput(host);
        input.Process("ab\r\b");
        Check(host.Sent[0] == "ab" && host.Box == "", "after Enter the box is empty and Backspace has nothing to take");

        // Tabs and control characters.
        host = new FakeHost();
        input = new NetconsoleLineInput(host);
        input.Process("a\tb");
        Equal(host.Box, "a\tb", "a tab is a letter of the line");
        host = new FakeHost();
        input = new NetconsoleLineInput(host);
        input.Process("ab\u0003cd\u0004");
        Equal(string.Join("|", host.Log), "set abcd|bytes 103,104", "control characters go to the device in one write after the text is in the box, in the encoding of the connection");
        // Escape sequences go to the device whole (the fake host adds 100 to every byte, see Encode).
        foreach (var test in new[]
        {
            new { Text = "\u001b[A", Log = "bytes @27,[,A", Name = "a cursor key typed as text goes whole to the device and nothing goes into the box" },
            new { Text = "ab\u001b[Acd", Log = "set abcd|bytes @27,[,A", Name = "the letters around it go into the box, the sequence to the device" },
            new { Text = "\u001b[1;5C", Log = "bytes @27,[,1,;,5,C", Name = "a sequence with parameters (Ctrl+Right) is one piece" },
            new { Text = "\u001b[200~", Log = "bytes @27,[,2,0,0,~", Name = "its final character may be a tilde" },
            new { Text = "\u001bOA", Log = "bytes @27,O,A", Name = "ESC O and a character (the application cursor keys) is one piece" },
            new { Text = "\u001bx", Log = "bytes @27,x", Name = "ESC and a printable character (Alt and a key) is one piece" },
            new { Text = "\u001b", Log = "bytes @27", Name = "an ESC alone goes by itself" },
            new { Text = "\u001b[", Log = "bytes @27,[", Name = "a sequence that is cut short takes what there is" },
            new { Text = "\u001b[12", Log = "bytes @27,[,1,2", Name = "also with some parameters" },
            new { Text = "\u001bO", Log = "bytes @27,O", Name = "ESC O at the end of the text is the two characters" },
            new { Text = "\u001b[Ax", Log = "set x|bytes @27,[,A", Name = "what follows the sequence goes into the box" },
            new { Text = "\u001b[A\u001b[B", Log = "bytes @27,[,A,@27,[,B", Name = "two sequences go in one write" },
            new { Text = "\u001b\r", Log = "send|bytes @27", Name = "an ESC before Enter stays alone: Enter sends the line" },
            new { Text = "\u001bO\r", Log = "send|bytes @27,O", Name = "ESC O before Enter: Enter sends the line" },
            new { Text = "\u001b\u0003", Log = "bytes @27,@3", Name = "an ESC before another control character stays alone" },
            new { Text = "\u001b[A\r\n", Log = "send|bytes @27,[,A", Name = "CR and LF after a sequence are one line ending" },
        })
        {
            host = new FakeHost();
            input = new NetconsoleLineInput(host);
            input.Process(test.Text);
            // @n is a byte of a character that is not printable, as the fake host makes it: n + 100.
            string expected = string.Join(",", test.Log.Split('|').Last().Replace("bytes ", "").Split(',').Select((string item) => item.StartsWith("@", StringComparison.Ordinal) ? (int.Parse(item.Substring(1)) + 100).ToString() : ((int)item[0] + 100).ToString()));
            string log = test.Log.Contains("bytes ") ? string.Join("|", test.Log.Split('|').Select((string part) => part.StartsWith("bytes ", StringComparison.Ordinal) ? "bytes " + expected : part)) : test.Log;
            Equal(string.Join("|", host.Log), log, test.Name);
        }
        host = new FakeHost();
        input = new NetconsoleLineInput(host);
        input.Process("\u001b[A\r\n");
        Equal(host.Sent.Count, 1, "(one line was sent)");
        host = new FakeHost();
        input = new NetconsoleLineInput(host);
        input.Process("a\u0003\rb");
        Equal(string.Join("|", host.Log), "set a|send|set b|bytes 103", "the bytes of the whole text go out last, after the line that Enter sent in the middle and the letters that follow it");
    }

    private static int Main()
    {
        try
        {
            HistoryChecks();
            ProcessChecks();
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            return 1;
        }
        return 0;
    }
}
