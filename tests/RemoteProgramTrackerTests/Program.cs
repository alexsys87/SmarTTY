using System;
using VisualTTY.DeviceTerminal;

// Regression harness for telling a full-screen program (mc, htop, vi ...) from a shell prompt on a COM/TCP terminal.
// The local input line must come back when the shell is back and must stay away while a program owns the screen.
// Run with: dotnet run --project tests/RemoteProgramTrackerTests
internal static class Program
{
    private static int _passed;

    private const string Esc = "\u001b";

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    // What the terminal does for a chunk received while a command runs: the tracker first, then the prompt text.
    private static bool ShowsPrompt(RemoteProgramTracker tracker, string chunk, bool alternateScreen = false)
    {
        tracker.Observe(chunk, alternateScreen);
        return !tracker.OwnsScreen && LineInputText.EndsLikePrompt(chunk, "> ");
    }

    private static int Main()
    {
        // --- prompt text ---
        Check(LineInputText.EndsLikePrompt("root@OpenWrt:~# ", "> "), "prompt: root prompt");
        Check(LineInputText.EndsLikePrompt("\r\nuser@host:~$ ", "> "), "prompt: leading line break");
        Check(LineInputText.EndsLikePrompt("=> ", "> "), "prompt: u-boot style");
        Check(LineInputText.EndsLikePrompt("[root@x ~]% ", "[root@x ~]% "), "prompt: the detected prompt is accepted too");
        Check(!LineInputText.EndsLikePrompt("hello\r\n", "> "), "prompt: plain output is not a prompt");
        Check(!LineInputText.EndsLikePrompt("root@OpenWrt:~#", "> "), "prompt: the blank after # is part of the pattern");
        Check(!LineInputText.EndsLikePrompt("", "> ") && !LineInputText.EndsLikePrompt(null, "> "), "prompt: empty text");
        Check(LineInputText.EndsLikePrompt("x# ", null) && !LineInputText.EndsLikePrompt("x", ""), "prompt: empty current prompt does not match everything");

        // --- the shell itself never counts as a program ---
        {
            RemoteProgramTracker t = new RemoteProgramTracker();
            Check(ShowsPrompt(t, "mc\r\nroot@OpenWrt:~# "), "shell: echo and prompt");
            Check(ShowsPrompt(t, Esc + "[H" + Esc + "[2J" + "root@OpenWrt:~# "), "shell: clear (home + erase) is not a program");
            Check(ShowsPrompt(t, Esc + "[1;1H" + Esc + "[J" + "root@OpenWrt:~# "), "shell: explicit 1;1 home is not a program");
            Check(ShowsPrompt(t, Esc + "[?1h" + Esc + "=" + "user@host:~$ "), "shell: application keys switched on at the prompt (zsh) is not a program");
            Check(ShowsPrompt(t, Esc + "[1;32mroot" + Esc + "[0m@host:~# "), "shell: colored prompt");
            Check(ShowsPrompt(t, Esc + "]0;root@host: ~" + "\a" + "root@host:~# "), "shell: window title before the prompt");
            Check(ShowsPrompt(t, Esc + "[?2004h" + "root@host:~# "), "shell: bracketed paste on");
        }

        // --- mc / htop on the Linux console (no alternate screen) ---
        {
            RemoteProgramTracker t = new RemoteProgramTracker();
            Check(!ShowsPrompt(t, Esc + "[?1h" + Esc + "=" + Esc + "[24;1H" + Esc + "[K" + "root@OpenWrt:/# "), "mc: first screen with its own command line");
            Check(t.OwnsScreen, "mc: owns the screen");
            Check(!ShowsPrompt(t, "$ "), "mc: a later chunk that is only '$ ' is still mc");
            Check(!ShowsPrompt(t, "File menu >"), "mc: ordinary text");
            Check(!ShowsPrompt(t, "Panel > "), "mc: menu text ending with '> '");
            Check(!ShowsPrompt(t, Esc + "[3;5H" + "# "), "mc: positioned text ending with '# '");
            Check(!ShowsPrompt(t, Esc + "[24;1H" + Esc + "[7m 1Help " + Esc + "[0m > "), "mc: function key bar");
            Check(ShowsPrompt(t, Esc + "[r" + Esc + "[24;1H" + Esc + "[?1l" + Esc + ">" + "\r\nroot@OpenWrt:/# "), "mc: exit restores the terminal and the prompt returns");
            Check(!t.OwnsScreen, "mc: no longer owns the screen");
        }
        {
            RemoteProgramTracker t = new RemoteProgramTracker();
            Check(!ShowsPrompt(t, Esc + "[?25l" + Esc + "[1;1H" + "  PID USER      PRI  NI  VIRT  RES  S CPU% MEM%   TIME+  Command" + Esc + "[2;1H" + "    1 root       20   0  1200   800 S  0.0  0.1  0:01.20 init " + Esc + "[3;1H$ "), "htop: header rows are not a prompt");
            Check(!ShowsPrompt(t, "Tasks: 25, 12 thr; 1 running > "), "htop: text ending with '> '");
            t.Observe(Esc + "[24;1H" + Esc + "[?25h" + Esc + "[?1l" + Esc + ">", false);
            Check(!t.OwnsScreen, "htop: exit restores the terminal");
            // The prompt text arrives later, in its own chunk.
            Check(ShowsPrompt(t, "\r\nroot@OpenWrt:~# "), "htop: the prompt after exit is accepted");
        }

        // --- vim / nano style with the alternate screen ---
        {
            RemoteProgramTracker t = new RemoteProgramTracker();
            Check(!ShowsPrompt(t, Esc + "[?1049h" + Esc + "[22;0;0t" + Esc + "[1;1H" + "text# ", alternateScreen: true), "alt screen: entered");
            Check(!ShowsPrompt(t, "more text$ ", alternateScreen: true), "alt screen: text that looks like a prompt");
            Check(ShowsPrompt(t, Esc + "[?1049l" + "root@host:~# ", alternateScreen: false), "alt screen: left, prompt in the same chunk");
        }
        {
            // The terminal reports the alternate screen even when the sequence was not seen by the tracker.
            RemoteProgramTracker t = new RemoteProgramTracker();
            Check(!ShowsPrompt(t, "$ ", alternateScreen: true), "alt screen reported by the terminal blocks the prompt");
            Check(ShowsPrompt(t, "root@host:~# ", alternateScreen: false), "alt screen left: prompt accepted");
        }

        // --- sequences split between chunks ---
        {
            RemoteProgramTracker t = new RemoteProgramTracker();
            t.Observe("abc" + Esc + "[12", false);
            Check(!t.OwnsScreen, "split: unfinished sequence waits");
            t.Observe(";5Hxyz", false);
            Check(t.OwnsScreen, "split: finished by the next chunk");
            t.Observe(Esc, false);
            t.Observe("[?1", false);
            Check(t.OwnsScreen, "split: still waiting");
            t.Observe("l" + Esc + ">", false);
            Check(!t.OwnsScreen, "split: reset sequence in three parts");
        }
        {
            RemoteProgramTracker t = new RemoteProgramTracker();
            t.Observe(Esc + "]0;title" + Esc + "\\", false);
            Check(!t.OwnsScreen, "osc: a title ended by ESC \\ is not a screen");
            t.Observe(Esc + "]0;ti", false);
            t.Observe("tle\a" + Esc + "[5;5H", false);
            Check(t.OwnsScreen, "osc: a title split between chunks, then a real position");
        }

        // --- more reset signals, other positioning forms ---
        {
            RemoteProgramTracker t = new RemoteProgramTracker();
            t.Observe(Esc + "[12d", false);
            Check(t.OwnsScreen, "painting: absolute row");
            t.Observe(Esc + "c", false);
            Check(!t.OwnsScreen, "reset: full reset");
            t.Observe(Esc + "[1;24r", false);
            Check(t.OwnsScreen, "painting: scroll region");
            t.Observe(Esc + "[!p", false);
            Check(!t.OwnsScreen, "reset: soft reset");
            t.Observe(Esc + "[5;7f", false);
            Check(t.OwnsScreen, "painting: f form of the position");
            t.Reset();
            Check(!t.OwnsScreen, "Reset(): forgets the program");
            t.Observe(Esc + "[11H", false);
            Check(t.OwnsScreen, "painting: row 11 is a position, not home");
            t.Reset();
            t.Observe(Esc + "[?1000h" + Esc + "[?25l", false);
            Check(!t.OwnsScreen, "mouse and hidden cursor alone do not count");
            t.Observe(null, false);
            t.Observe("", false);
            t.Observe(Esc + "[", false);
            t.Observe(Esc, false);
            t.Observe("garbage" + Esc + "\u0001" + "[5;5H", false);
            Check(true, "robustness: null, empty, truncated and invalid input do not throw");
        }

        Console.WriteLine();
        Console.WriteLine(_passed + " checks passed");
        return 0;
    }
}
