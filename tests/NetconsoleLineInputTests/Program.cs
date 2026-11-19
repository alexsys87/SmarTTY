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
        host = new FakeHost();
        input = new NetconsoleLineInput(host);
        input.Process("\u001b[A");
        Equal(string.Join("|", host.Log), "set [A|bytes 127", "an escape sequence is cut: the ESC goes to the device, the rest into the box");
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
