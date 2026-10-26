using System;
using System.Collections.Generic;
using VisualTTY.DeviceTerminal;

// Regression checks for the device-terminal state shown in the status bar (VisualTTY/DeviceTerminal/
// DeviceTerminalStateText.cs): the state of the connection, the prompt while the shell waits, and the
// full-screen-program flag.
// Run with: dotnet run --project tests/DeviceTerminalStateTests
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

    private static void Equal<T>(T actual, T expected, string name)
    {
        bool equal = EqualityComparer<T>.Default.Equals(actual, expected);
        Check(equal, name + (equal ? "" : $" (expected '{expected}', got '{actual}')"));
    }

    private static int Main()
    {
        try
        {
            CheckNotConnected();
            CheckAtPrompt();
            CheckRunningCommand();
            CheckFullScreenProgram();
            Console.WriteLine($"All {_passed} checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }
    }

    private static void CheckNotConnected()
    {
        Equal(DeviceTerminalStateText.State(connected: false, inputLineShown: false, fullScreenProgramActive: false, prompt: "> "), "", "disconnected: nothing is added");
        Equal(DeviceTerminalStateText.State(connected: false, inputLineShown: true, fullScreenProgramActive: false, prompt: "> "), "", "disconnected: a shown input line is not reported");
    }

    private static void CheckAtPrompt()
    {
        // The trailing blank of a prompt is not part of what is shown.
        Equal(DeviceTerminalStateText.State(true, true, false, "> "), "at prompt '>'", "prompt: '> ' is shown without the blank");
        Equal(DeviceTerminalStateText.State(true, true, false, "root@OpenWrt:/# "), "at prompt 'root@OpenWrt:/#'", "prompt: a shell prompt");
        Equal(DeviceTerminalStateText.State(true, true, false, "Password:"), "at prompt 'Password:'", "prompt: a password prompt");
        Equal(DeviceTerminalStateText.State(true, true, false, null), "at prompt ''", "prompt: a missing prompt does not throw");
    }

    private static void CheckRunningCommand()
    {
        // A command runs and the terminal is not showing an input line, but the program does not own the screen.
        Equal(DeviceTerminalStateText.State(true, false, false, "> "), "", "command: nothing is added while a command runs");
    }

    private static void CheckFullScreenProgram()
    {
        Equal(DeviceTerminalStateText.State(true, false, true, "> "), "full-screen program", "full-screen: reported");
        // The flag wins over the prompt, which is what the terminal believes while the program owns the screen.
        Equal(DeviceTerminalStateText.State(true, true, true, "> "), "full-screen program", "full-screen: reported even with an input line");
    }
}
