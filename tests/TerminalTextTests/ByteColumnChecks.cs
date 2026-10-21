using System;
using System.Collections.Generic;
using System.Text;
using SmarTTY.Terminal;

// Byte-oriented line editing (BusyBox ash without Unicode support, e.g. OpenWrt): the shell moves its cursor one
// column per UTF-8 byte, so a Cyrillic letter (2 bytes) needs two Backspaces and every "\b" it prints steps back one
// byte. The steps below replay what a real BusyBox 1.36 ash printed for the keys SmarTTY sends (each key repeated
// once per byte of the letter it crosses), through a one-line screen model that follows the same rules as
// TerminalEmulatorControl in this mode. Without the byte model each Backspace erased two letters from the screen.
internal static class ByteColumnChecks
{
    private sealed class Step
    {
        public readonly string Key;

        public readonly int? ExpectedPresses;

        public readonly byte[] Output;

        public Step(string key, int? expectedPresses, string output)
        {
            Key = key;
            ExpectedPresses = expectedPresses;
            Output = Encoding.GetEncoding("ISO-8859-1").GetBytes(output);
        }
    }

    /// <summary>One screen line in byte mode, mirroring TerminalEmulatorControl's handling.</summary>
    private sealed class Screen
    {
        private readonly StringBuilder _line = new StringBuilder();

        private readonly Utf8StreamDecoder _decoder = new Utf8StreamDecoder { EscapeInvalidBytes = true };

        private int _partialColumn = -1;

        private List<byte> _partialBytes;

        // An escape sequence split between two reads waits for its end, as in TerminalEmulatorControl.
        private string _incompleteEscape = "";

        public int Column;

        public int Offset;

        public string Line => _line.ToString().TrimEnd();

        public void Receive(byte[] data, bool bytewise)
        {
            if (!bytewise)
            {
                Process(_decoder.Decode(data, 0, data.Length));
                return;
            }
            for (int index = 0; index < data.Length; index++)
            {
                Process(_decoder.Decode(data, index, 1));
            }
        }

        private void Put(int column, char ch)
        {
            while (_line.Length <= column)
            {
                _line.Append(' ');
            }
            _line[column] = ch;
        }

        private void Process(string text)
        {
            text = _incompleteEscape + text;
            _incompleteEscape = "";
            for (int index = 0; index < text.Length; index++)
            {
                char ch = text[index];
                if (ch == '\b')
                {
                    ByteColumns.MoveBack(_line.ToString(), ref Column, ref Offset, 1);
                }
                else if (ch == '\r')
                {
                    Column = 0;
                    Offset = 0;
                }
                else if (ch == '\u001b')
                {
                    int end = index + 2;
                    while (end < text.Length && !(text[end] >= '@' && text[end] <= '~'))
                    {
                        end++;
                    }
                    if (end >= text.Length)
                    {
                        _incompleteEscape = text.Substring(index);
                        return;
                    }
                    string parameter = text.Substring(index + 2, end - index - 2);
                    int count = int.TryParse(parameter, out int value) && value > 0 ? value : 1;
                    switch (text[end])
                    {
                        case 'D':
                            ByteColumns.MoveBack(_line.ToString(), ref Column, ref Offset, count);
                            break;
                        case 'C':
                            ByteColumns.MoveForward(_line.ToString(), ref Column, ref Offset, count, 1000);
                            break;
                        case 'J':
                        case 'K':
                            if (Column < _line.Length)
                            {
                                _line.Length = Column;
                            }
                            break;
                    }
                    index = end;
                }
                else
                {
                    WriteChar(ch);
                }
            }
        }

        private void WriteChar(char ch)
        {
            if (Offset > 0)
            {
                // The editor prints after the first bytes of the half-crossed letter: they keep the cell.
                byte[] prefix = ByteColumns.CellPrefix(_line.ToString(), Column, Offset);
                Put(Column, '�');
                _partialColumn = Column;
                _partialBytes = new List<byte>(prefix);
                Column++;
                Offset = 0;
            }
            if (Utf8StreamDecoder.IsEscapedByte(ch))
            {
                byte value = (byte)(ch - Utf8StreamDecoder.EscapedByteBase);
                if (_partialColumn == Column - 1 && _line[_partialColumn] == '�' && ByteColumns.CanContinue(_partialBytes, value))
                {
                    string completed = ByteColumns.TryComplete(_partialBytes, value);
                    if (completed != null)
                    {
                        Put(_partialColumn, completed[0]);
                        _partialColumn = -1;
                    }
                    return;
                }
                Put(Column, '�');
                _partialColumn = (ByteColumns.SequenceLength(value) > 0) ? Column : -1;
                _partialBytes = new List<byte> { value };
                Column++;
                return;
            }
            if (_partialColumn == Column)
            {
                _partialColumn = -1;
            }
            Put(Column, ch);
            Column++;
        }

        public int PressesFor(string key)
        {
            string line = _line.ToString();
            switch (key)
            {
                case "Backspace":
                case "Left":
                    return ByteColumns.BackwardKeyCount(line, Column, Offset);
                case "Delete":
                case "Right":
                    return ByteColumns.ForwardKeyCount(line, Column, Offset);
                default:
                    return 0;
            }
        }
    }

    private static void Replay(string name, Step[] steps, string expectedLine, int expectedColumn)
    {
        foreach (bool bytewise in new[] { false, true })
        {
            Screen screen = new Screen();
            foreach (Step step in steps)
            {
                if (step.ExpectedPresses.HasValue)
                {
                    int presses = screen.PressesFor(step.Key);
                    Program.Check(presses == step.ExpectedPresses.Value, $"{name}: {step.Key} is sent {step.ExpectedPresses} times (got {presses})");
                }
                screen.Receive(step.Output, bytewise);
            }
            string mode = bytewise ? " (output arriving byte by byte)" : "";
            Program.Equal(screen.Line, expectedLine, name + ": screen matches the shell's command line" + mode);
            Program.Check(screen.Column == expectedColumn && screen.Offset == 0, $"{name}: cursor at column {expectedColumn}{mode} (got {screen.Column}+{screen.Offset})");
        }
    }

    public static void Run()
    {
        Program.Check(ByteColumns.CellByteLength("aп€�", 0) == 1, "ASCII is one byte");
        Program.Check(ByteColumns.CellByteLength("aп€�", 1) == 2, "Cyrillic is two bytes");
        Program.Check(ByteColumns.CellByteLength("aп€�", 2) == 3, "euro sign is three bytes");
        Program.Check(ByteColumns.CellByteLength("aп€�", 3) == 1, "U+FFFD stands for one stray byte");
        Program.Check(ByteColumns.CellByteLength("a", 5) == 1, "blank cells past the text are one byte");
        Program.Check(ByteColumns.BackwardKeyCount("$ при", 5, 0) == 2, "Backspace after a Cyrillic letter is sent twice");
        Program.Check(ByteColumns.BackwardKeyCount("$ abc", 5, 0) == 1, "Backspace after an ASCII letter is sent once");
        Program.Check(ByteColumns.BackwardKeyCount("$ ", 0, 0) == 1, "Backspace at the start of the line is sent once");

        int column = 5, offset = 0;
        int left = ByteColumns.MoveBack("$ при", ref column, ref offset, 1);
        Program.Check(left == 0 && column == 4 && offset == 1, "one byte back from the end enters the last letter");
        left = ByteColumns.MoveBack("$ при", ref column, ref offset, 7);
        Program.Check(left == 0 && column == 0 && offset == 0, "ESC [ 7 D from there reaches the start of the line");
        left = ByteColumns.MoveBack("$ при", ref column, ref offset, 3);
        Program.Check(left == 3, "moves past the start of the line are reported back");
        column = 2;
        offset = 0;
        ByteColumns.MoveForward("$ при", ref column, ref offset, 3, 80);
        Program.Check(column == 3 && offset == 1, "three bytes forward cross one letter and enter the next");

        Program.Check(ByteColumns.ParseProbe(new[] { "2" }), "probe: BusyBox counting bytes");
        Program.Check(!ByteColumns.ParseProbe(new[] { "1" }), "probe: Unicode-aware BusyBox");
        Program.Check(!ByteColumns.ParseProbe(new[] { "shell:/usr/bin/bash" }), "probe: bash");
        Program.Check(ByteColumns.ParseProbe(new[] { "wc: invalid option -- 'm'", "BusyBox v1.33.2 multi-call binary.", "Usage: wc [-clwL] [FILE]..." }), "probe: BusyBox without wc -m (no Unicode support)");
        Program.Check(!ByteColumns.ParseProbe(new[] { "ash: busybox: not found" }), "probe: unknown output keeps the normal mode");
        Program.Check(!ByteColumns.ParseProbe(null), "probe: no output keeps the normal mode");

        Utf8StreamDecoder decoder = new Utf8StreamDecoder { EscapeInvalidBytes = true };
        byte[] stray = { 0xD0, 0xD0, 0xB5, 0x96 };
        Program.Equal(decoder.Decode(stray, 0, stray.Length), "\uDCD0е\uDC96", "escaped decoding keeps every invalid byte");
        Program.Equal(new Utf8StreamDecoder().Decode(stray, 0, stray.Length), "�е�", "normal decoding still shows U+FFFD");

        Replay("Backspace after 'при'", new[]
        {
            new Step("type", null, "echo Ð¿Ñ\u0080Ð¸"),
            new Step("Backspace", 2, "\u0008\u001b[J\u0008\u001b[J"),
        }, "echo пр", 7);
        Replay("Left, Left, Backspace and a Cyrillic letter in the middle of 'привет'", new[]
        {
            new Step("type", null, "echo Ð¿Ñ\u0080Ð¸Ð²ÐµÑ\u0082"),
            new Step("Left", 2, "\u0008\u0008"),
            new Step("Left", 2, "\u0008\u0008"),
            new Step("Backspace", 2, "\u0008ÐµÑ\u0082\u001b[J\u0008\u0008\u0008\u0008\u0008ÐµÑ\u0082\u001b[J\u0008\u0008\u0008\u0008"),
            new Step("type", null, "ÐÐµÑ\u0082\u0008\u0008\u0008\u0008"),
            new Step("type", null, "\u0096ÐµÑ\u0082\u0008\u0008\u0008\u0008"),
        }, "echo приЖет", 9);
        Replay("Home, Right and Delete over Cyrillic letters", new[]
        {
            new Step("type", null, "Ð¿Ñ\u0080Ð¸"),
            new Step("Home", null, "\u001b[6D"),
            new Step("Right", 2, "Ð¿"),
            new Step("Delete", 2, "\u0080Ð¸\u001b[J\u0008\u0008\u0008Ð¸\u001b[J\u0008\u0008"),
        }, "пи", 1);
        Replay("Ctrl+W after a Cyrillic word", new[]
        {
            new Step("type", null, "echo x Ð¿Ñ\u0080Ð¸"),
            new Step("Ctrl+W", null, "\u0008\u001b[J\u0008\u001b[J\u0008\u001b[J\u0008\u001b[J\u0008\u001b[J\u0008\u001b[J"),
        }, "echo x", 7);
    }
}
