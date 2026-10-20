using System;

namespace VisualTTY.DeviceTerminal;

/// <summary>
/// Tells a full-screen program (mc, htop, top, vi, nano, alsamixer ...) from a shell that waits for a command, by
/// watching the escape sequences the device sends. A COM/TCP terminal edits the command line locally while the shell
/// is at its prompt and forwards every key to the device while a command runs; the text of the last chunk ("ends
/// with <c>$ </c>") is not enough to decide between the two, because a screen program prints such text all the time
/// (the command line at the bottom of mc, a menu entry that ends with "&gt; "). This class looks for what only a
/// program that owns the screen does:
/// <list type="bullet">
/// <item>it switches to the alternate screen (<c>ESC[?1049h</c>, <c>ESC[?47h</c>, <c>ESC[?1047h</c>) - the caller
/// reports that through <c>alternateScreenActive</c>, because the emulator already tracks it;</item>
/// <item>it addresses the screen by row and column (<c>ESC[12;5H</c>, <c>ESC[12d</c>, <c>ESC[1;24r</c>) - the only
/// way to paint a screen on a terminal without an alternate screen, such as the Linux console (TERM=linux) that a
/// serial port usually has.</item>
/// </list>
/// The program is considered gone when it restores the terminal: application cursor keys off (<c>ESC[?1l</c>,
/// <c>ESC&gt;</c>), alternate screen off, a soft or full reset. Every curses or slang program does that on exit.
/// A shell never sends these sequences to draw its prompt, so the state never gets stuck at a normal prompt.
/// </summary>
public sealed class RemoteProgramTracker
{
    // The longest escape sequence kept over to the next chunk when a chunk ends in the middle of it.
    private const int MaxCarryLength = 40;

    private bool _painting;

    private bool _alternateScreen;

    // The unfinished escape sequence at the end of the previous chunk.
    private string _carry = string.Empty;

    /// <summary>True while a program that draws the whole screen seems to be running.</summary>
    public bool OwnsScreen => _alternateScreen || _painting;

    /// <summary>Forgets everything: a new command starts, the connection is new or the screen was reset by the user.</summary>
    public void Reset()
    {
        _painting = false;
        _alternateScreen = false;
        _carry = string.Empty;
    }

    /// <summary>
    /// Looks at one received chunk. Call it for every chunk while a command runs, after the chunk was given to the
    /// terminal emulator.
    /// </summary>
    /// <param name="text">The received text, escape sequences included.</param>
    /// <param name="alternateScreenActive">Whether the emulator shows the alternate screen after this chunk.</param>
    public void Observe(string text, bool alternateScreenActive)
    {
        _alternateScreen = alternateScreenActive;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        string scan = _carry.Length == 0 ? text : _carry + text;
        _carry = string.Empty;
        int i = 0;
        while (i < scan.Length)
        {
            int esc = scan.IndexOf('\u001b', i);
            if (esc < 0)
            {
                return;
            }
            if (esc + 1 >= scan.Length)
            {
                KeepUnfinished(scan, esc);
                return;
            }
            char kind = scan[esc + 1];
            if (kind == '[')
            {
                int next = ReadCsi(scan, esc);
                if (next < 0)
                {
                    KeepUnfinished(scan, esc);
                    return;
                }
                i = next;
            }
            else if (kind == ']')
            {
                // A window title or similar: its text must not be taken for escape sequences.
                int next = SkipOsc(scan, esc);
                if (next < 0)
                {
                    KeepUnfinished(scan, esc);
                    return;
                }
                i = next;
            }
            else
            {
                if (kind == '>' || kind == 'c')
                {
                    // Numeric keypad / full reset: what a program does when it gives the terminal back.
                    _painting = false;
                }
                i = esc + 2;
            }
        }
    }

    private void KeepUnfinished(string scan, int escIndex)
    {
        if (scan.Length - escIndex <= MaxCarryLength)
        {
            _carry = scan.Substring(escIndex);
        }
    }

    // Reads one CSI sequence that starts at scan[esc] and returns the index after it, or -1 when it is not complete.
    private int ReadCsi(string scan, int esc)
    {
        int j = esc + 2;
        int paramStart = j;
        while (j < scan.Length && scan[j] >= '0' && scan[j] <= '?')
        {
            j++;
        }
        int paramEnd = j;
        while (j < scan.Length && scan[j] >= ' ' && scan[j] <= '/')
        {
            j++;
        }
        int intermediateEnd = j;
        if (j >= scan.Length)
        {
            return -1;
        }
        char final = scan[j];
        if (final < '@' || final > '~')
        {
            // Not a valid sequence (a control character in the middle of it): skip the ESC and carry on after it.
            return esc + 1;
        }
        HandleCsi(scan.Substring(paramStart, paramEnd - paramStart), scan.Substring(paramEnd, intermediateEnd - paramEnd), final);
        return j + 1;
    }

    // Returns the index after an OSC string (ended by BEL or ESC \), or -1 when its end has not arrived yet.
    private static int SkipOsc(string scan, int esc)
    {
        for (int j = esc + 2; j < scan.Length; j++)
        {
            if (scan[j] == '\a')
            {
                return j + 1;
            }
            if (scan[j] == '\u001b')
            {
                return j + 1 < scan.Length ? (scan[j + 1] == '\\' ? j + 2 : j) : -1;
            }
        }
        return -1;
    }

    private void HandleCsi(string parameters, string intermediates, char final)
    {
        if (parameters.Length > 0 && parameters[0] == '?')
        {
            if (final == 'h' || final == 'l')
            {
                HandlePrivateMode(parameters.Substring(1), set: final == 'h');
            }
            return;
        }
        switch (final)
        {
            case 'H':
            case 'f':
                // Plain ESC[H and ESC[1;1H only move the cursor home (clear, reset); a real position means a screen is being drawn.
                if (!IsHome(parameters))
                {
                    _painting = true;
                }
                break;
            case 'd':
                // Absolute row.
                if (HasDigit(parameters))
                {
                    _painting = true;
                }
                break;
            case 'r':
                // A scroll region with both margins.
                if (parameters.IndexOf(';') >= 0)
                {
                    _painting = true;
                }
                break;
            case 'p':
                // DECSTR, soft terminal reset.
                if (intermediates == "!")
                {
                    _painting = false;
                }
                break;
        }
    }

    private void HandlePrivateMode(string parameters, bool set)
    {
        foreach (string item in parameters.Split(';'))
        {
            if (!int.TryParse(item, out int mode))
            {
                continue;
            }
            bool alternateScreen = mode == 47 || mode == 1047 || mode == 1049;
            if (set && alternateScreen)
            {
                _painting = true;
            }
            // 1 is "application cursor keys": programs switch it on when they start and off when they leave. It is
            // deliberately not used for the start - some shells (zsh) switch it on at every prompt.
            else if (!set && (alternateScreen || mode == 1))
            {
                _painting = false;
            }
        }
    }

    private static bool IsHome(string parameters)
    {
        // "", "1", "1;1", "0;0", ";" ... : every given number is 0 or 1 (a missing one means 1).
        foreach (string item in parameters.Split(';'))
        {
            if (item.Length != 0 && item != "0" && item != "1")
            {
                return false;
            }
        }
        return true;
    }

    private static bool HasDigit(string parameters)
    {
        foreach (char ch in parameters)
        {
            if (ch >= '0' && ch <= '9')
            {
                return true;
            }
        }
        return false;
    }
}
