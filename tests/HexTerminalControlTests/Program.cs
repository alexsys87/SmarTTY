using SmarTTY.Controls;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using VisualTTY.DeviceTerminal;

// Drives the real hex view of a device terminal (Controls/HexTerminalControl.cs) with scripted bytes and writes down what it shows after every step: the rows of the
// HEX view and of the text view, the number of bytes in a row, the match that the search selected and what both views select, and the number of marks of the matches.
// HexTerminalControlGolden.txt is what the code wrote before its logic moved out of the control; it must stay the same.
// The layout is set through the maximum number of bytes in a row, which a wide window does not limit, so that it does not depend on the fonts.
//   xvfb-run -a mono tests/HexTerminalControlTests/bin/Release/net48/HexTerminalControlTests.exe            compares with the golden file
//   ... HexTerminalControlTests.exe --record <file>                                                          writes the golden file (old code only)
internal static class Program
{
    private static readonly List<string> Lines = new List<string>();

    private static HexTerminalControl _control;

    private static T Field<T>(object target, string name)
    {
        for (Type type = target.GetType(); type != null; type = type.BaseType)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field != null)
            {
                return (T)field.GetValue(target);
            }
        }
        throw new InvalidOperationException("no field " + name);
    }

    private static T Property<T>(object target, string name)
    {
        for (Type type = target.GetType(); type != null; type = type.BaseType)
        {
            PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (property != null)
            {
                return (T)property.GetValue(target, null);
            }
        }
        throw new InvalidOperationException("no property " + name);
    }

    private static void Call(string name, params object[] args)
    {
        MethodInfo method = typeof(HexTerminalControl).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        method.Invoke(_control, args);
    }

    private static TerminalEmulatorBase HexView => Field<TerminalEmulatorBase>(_control, "termHex");

    private static TerminalEmulatorBase TextView => Field<TerminalEmulatorBase>(_control, "termText");

    private static string Document(TerminalEmulatorBase view)
    {
        EditableTextDocument document = Property<EditableTextDocument>(view, "EditableDocument");
        StringBuilder text = new StringBuilder();
        for (int line = 0; line < document.LineCount; line++)
        {
            text.Append(document.GetLineText(line).TrimEnd()).Append('|');
        }
        return text.ToString();
    }

    private static string Position(TextDocument.TextPosition position)
    {
        return position.IsValid ? position.Line + ":" + position.Column : "-";
    }

    // The state after a step, in a few lines.
    private static void Dump(string step)
    {
        Lines.Add("== " + step);
        Lines.Add("hex   " + Document(HexView));
        Lines.Add("text  " + Document(TextView));
        string match = Field<int>(_control, "_matchOffset") + "+" + Field<int>(_control, "_matchLength");
        Lines.Add("state per-line=" + Field<int>(_control, "_bytesPerLine") + " laid-out=" + Field<int>(_control, "_laidOutBytesPerLine") + " mixed=" + Field<bool>(_control, "_layoutMixed")
            + " match=" + match + " selection=" + _control.HasSelection
            + " hex-sel=" + Position(Property<TextDocument.TextPosition>(HexView, "SelectionStart")) + ".." + Position(Property<TextDocument.TextPosition>(HexView, "SelectionEnd"))
            + " text-sel=" + Position(Property<TextDocument.TextPosition>(TextView, "SelectionStart")) + ".." + Position(Property<TextDocument.TextPosition>(TextView, "SelectionEnd"))
            + " marks=" + Field<List<int>>(_control, "_hexHighlightIds").Count + "/" + Field<List<int>>(_control, "_textHighlightIds").Count);
    }

    private static void Setup(int maximumBytesPerLine, bool showTextView = true, bool breaksAroundEcho = false, int timeout = 0)
    {
        _control.Clear();
        _control.SetByteHighlight(null);
        _control.HexSettings = new DeviceTerminalSettings.HexTerminalSettings
        {
            MaximumBytesPerLine = maximumBytesPerLine,
            ShowTextView = showTextView,
            BreaksAroundEcho = breaksAroundEcho,
            TimeoutForAutoBreak = timeout
        };
        Call("LayoutViews");
        if (Field<int>(_control, "_bytesPerLine") != maximumBytesPerLine && maximumBytesPerLine > 0)
        {
            throw new InvalidOperationException("the window is too narrow for " + maximumBytesPerLine + " bytes in a row (" + Field<int>(_control, "_bytesPerLine") + ")");
        }
    }

    private static byte[] Bytes(int count, int first = 0)
    {
        byte[] bytes = new byte[count];
        for (int i = 0; i < count; i++)
        {
            bytes[i] = (byte)(first + i);
        }
        return bytes;
    }

    private static byte[] Ascii(string text)
    {
        return Encoding.ASCII.GetBytes(text);
    }

    private static void Append(byte[] data, bool echo = false)
    {
        _control.AppendData(data, data.Length, echo);
    }

    private static void Relayout(int maximumBytesPerLine)
    {
        _control.HexSettings.MaximumBytesPerLine = maximumBytesPerLine;
        Call("LayoutViews");
        // The resize timer waits for a few ticks before it lays the history out again.
        for (int i = 0; i < 7; i++)
        {
            Call("OnResizeTimer", null, EventArgs.Empty);
        }
    }

    private static void Find(string label, byte[] pattern, int start, bool backward, bool wrap)
    {
        Lines.Add("find " + label + " from " + start + (backward ? " backward" : " forward") + (wrap ? " wrapping" : "") + " -> " + _control.FindBytes(pattern, start, backward, wrap));
    }

    private static void Next(string label, byte[] pattern, bool backward, bool wrap)
    {
        bool found = _control.FindNextBytes(pattern, backward, wrap);
        Dump("next " + label + (backward ? " backward" : " forward") + " found=" + found);
    }

    private static void Script()
    {
        // The rows: the HEX view and the text view lay the same bytes out.
        Setup(16);
        Append(Bytes(40));
        Dump("40 bytes, 16 in a row");
        Append(Bytes(10, 200));
        Dump("10 more bytes go on in the last row and the next");

        Setup(8);
        Append(Bytes(3));
        Append(Bytes(5, 3));
        Append(Bytes(7, 8));
        Append(Bytes(1, 15));
        Dump("8 in a row, bytes that come in pieces of 3, 5, 7 and 1");

        Setup(4);
        Append(new byte[] { 0x00, 0x1f, 0x20, 0x41, 0x7e, 0x7f, 0x80, 0xff });
        Dump("only the printable ASCII is shown as it is in the text view");

        Setup(1);
        Append(Bytes(3));
        Dump("one byte in a row");

        Setup(16, showTextView: false);
        Append(Bytes(20));
        Dump("without the text view");

        // What a call with odd arguments does.
        Setup(8);
        _control.AppendData(null, 5);
        _control.AppendData(Bytes(4), 0);
        _control.AppendData(Bytes(4), 99);
        _control.AppendData(Bytes(4), -3);
        Dump("a null buffer, a count of 0, a count past the buffer and a negative one");

        // The breaks.
        Setup(8, breaksAroundEcho: true);
        Append(Bytes(5));
        Append(Bytes(3, 100), echo: true);
        Append(Bytes(4, 50));
        Append(Bytes(2, 60));
        Append(Bytes(2, 70), echo: true);
        Dump("a break before and after the echo");

        Setup(8, breaksAroundEcho: false);
        Append(Bytes(5));
        Append(Bytes(3, 100), echo: true);
        Append(Bytes(4, 50));
        Dump("no break around the echo when it is not asked for");

        Setup(8, timeout: 40);
        Append(Bytes(5));
        Append(Bytes(3, 10));
        Dump("no break when the data comes at once");
        Thread.Sleep(90);
        Append(Bytes(4, 20));
        Dump("a break when the data is late");

        // The layout changes.
        Setup(16);
        Append(Bytes(40));
        Relayout(8);
        Dump("the history is laid out again with 8 in a row");
        Relayout(8);
        Dump("and again with the same number: nothing changes");
        Relayout(16);
        Append(Bytes(5, 100));
        Dump("16 in a row again, with new bytes");
        Setup(8, breaksAroundEcho: true);
        Append(Bytes(10));
        Append(Bytes(4, 100), echo: true);
        Append(Bytes(5, 50));
        Relayout(4);
        Dump("echo and breaks stay when the history is laid out again");

        Setup(16);
        Append(Bytes(20));
        Relayout(5);
        Append(Bytes(7, 60));
        Relayout(5);
        Dump("bytes added at another layout make the history be laid out again at the same number");

        // The search for a byte sequence.
        Setup(4);
        Append(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 1, 2, 3, 4, 9, 9, 1, 2 });
        Dump("search data");
        byte[] pattern = new byte[] { 3, 4, 5 };
        Find("a match across rows", pattern, 0, false, false);
        Find("a match across rows", pattern, 3, false, false);
        Find("a match across rows", pattern, 4, false, false);
        Find("a match across rows", pattern, 4, false, true);
        Find("last", new byte[] { 1, 2 }, 15, true, false);
        Find("last", new byte[] { 1, 2 }, 100, true, false);
        Find("backward from 9", new byte[] { 1, 2 }, 9, true, false);
        Find("backward from 0", new byte[] { 3, 4 }, 0, true, true);
        Find("none", new byte[] { 7, 7 }, 0, false, true);
        Next("first", new byte[] { 1, 2 }, false, false);
        Next("second", new byte[] { 1, 2 }, false, false);
        Next("third", new byte[] { 1, 2 }, false, false);
        Next("fourth: none left", new byte[] { 1, 2 }, false, false);
        Next("again from the start of the data, wrapping", new byte[] { 1, 2 }, false, true);
        Next("back", new byte[] { 1, 2 }, true, true);
        Next("back", new byte[] { 1, 2 }, true, true);
        Next("a pattern that is not there", new byte[] { 0xee }, false, true);
        Next("a match across rows", new byte[] { 3, 4, 5 }, false, false);
        Next("overlapping matches one by one", new byte[] { 9, 9 }, false, true);
        Next("overlapping matches one by one", new byte[] { 9 }, false, true);
        Next("overlapping matches one by one", new byte[] { 9 }, false, true);

        // A match that is selected stays selected when the rows change.
        Relayout(8);
        Dump("the layout changes while a match is selected");
        Next("after the layout changed", new byte[] { 9 }, false, true);

        // A match does not go across a break.
        Setup(4, breaksAroundEcho: true);
        Append(new byte[] { 1, 2 });
        Append(new byte[] { 3, 4 }, echo: true);
        Append(new byte[] { 5, 6 });
        Find("across a break", new byte[] { 2, 3 }, 0, false, true);
        Find("inside the echo", new byte[] { 3, 4 }, 0, false, true);
        Next("inside the echo", new byte[] { 3, 4 }, false, true);

        // Starting from a selection that the user made.
        Setup(4);
        Append(Bytes(16));
        Next("from the start", new byte[] { 5 }, false, true);
        Next("backward from the match", new byte[] { 5 }, true, false);
        Next("none before it", new byte[] { 5 }, true, false);

        // The marks of the matches.
        Setup(4);
        Append(new byte[] { 1, 2, 1, 2, 3, 1, 2, 9, 9, 1, 2 });
        _control.SetByteHighlight(new byte[] { 1, 2 });
        Dump("the matches are marked");
        _control.SetByteHighlight(new byte[] { 2, 1 });
        Dump("another pattern");
        _control.SetByteHighlight(new byte[0]);
        Dump("an empty pattern marks nothing");
        _control.SetByteHighlight(new byte[] { 1, 2 });
        Append(Bytes(4, 1));
        _control.SetByteHighlight(new byte[] { 1, 2 });
        Dump("marked again after more bytes");
        _control.Clear();
        Dump("cleared: the marks are gone");

        // Clear.
        Setup(8);
        Append(Bytes(20));
        Next("a match to clear", new byte[] { 3 }, false, true);
        _control.Clear();
        Dump("after Clear");
        Append(Bytes(3));
        Dump("and new bytes after it");
    }

    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        using (Form form = new Form { Width = 2600, Height = 700 })
        {
            _control = new HexTerminalControl { Dock = DockStyle.Fill };
            form.Controls.Add(_control);
            bool failed = false;
            form.Shown += (sender, e) =>
            {
                try
                {
                    Script();
                }
                catch (Exception exception)
                {
                    Console.WriteLine(exception);
                    failed = true;
                }
                form.Close();
            };
            Application.Run(form);
            if (failed)
            {
                return 1;
            }
        }
        string text = string.Join("\n", Lines) + "\n";
        if (args.Length == 2 && args[0] == "--record")
        {
            File.WriteAllText(args[1], text, new UTF8Encoding(false));
            Console.WriteLine("Recorded " + Lines.Count + " lines in " + args[1]);
            return 0;
        }
        string goldenPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HexTerminalControlGolden.txt");
        if (!File.Exists(goldenPath))
        {
            Console.WriteLine("FAIL: " + goldenPath + " is missing");
            return 1;
        }
        string[] golden = File.ReadAllText(goldenPath, Encoding.UTF8).Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        int mismatches = 0;
        for (int i = 0; i < Math.Max(golden.Length, Lines.Count); i++)
        {
            string expected = i < golden.Length ? golden[i] : "(missing)";
            string actual = i < Lines.Count ? Lines[i] : "(missing)";
            if (expected != actual)
            {
                if (mismatches++ < 5)
                {
                    Console.WriteLine("FAIL: line " + (i + 1));
                    Console.WriteLine("  expected: " + expected);
                    Console.WriteLine("  actual:   " + actual);
                }
            }
        }
        if (mismatches > 0)
        {
            Console.WriteLine("FAIL: " + mismatches + " of " + Lines.Count + " lines differ from the golden file");
            return 1;
        }
        Console.WriteLine("PASS: all " + Lines.Count + " lines of the hex view of a device terminal match the golden file");
        Console.WriteLine();
        Console.WriteLine("All hex terminal control checks passed.");
        return 0;
    }
}
