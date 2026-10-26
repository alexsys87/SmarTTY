using System;
using System.Text;

namespace VisualTTY.DeviceTerminal;

/// <summary>
/// The one-line state a COM-port or raw-TCP terminal shows in the status bar: the connection, and - once it is up -
/// what the terminal believes the device is doing. A line-editing terminal draws its own prompt and edits the
/// command locally while the shell waits, so the status line tells the two cases apart: "at prompt '&gt; '" when the
/// keys are edited here, "full-screen program" when they go to a program that owns the screen (mc, htop, vi ...),
/// and nothing extra while a command is simply running.
/// </summary>
public static class DeviceTerminalStateText
{
    /// <summary>
    /// The state suffix for a connected device terminal, or an empty string when there is nothing to add (still
    /// connecting, disconnected, a full-screen program, or a command that is not a full-screen program).
    /// </summary>
    /// <param name="connected">Whether the terminal is connected.</param>
    /// <param name="inputLineShown">Whether the local input line is shown, i.e. the shell is at its prompt.</param>
    /// <param name="fullScreenProgramActive">Whether a program that owns the screen is running on the device.</param>
    /// <param name="prompt">The prompt the terminal edits after, for example "&gt; ".</param>
    public static string State(bool connected, bool inputLineShown, bool fullScreenProgramActive, string prompt)
    {
        if (!connected)
        {
            return string.Empty;
        }
        if (fullScreenProgramActive)
        {
            return "full-screen program";
        }
        return inputLineShown ? "at prompt '" + Printable(prompt) + "'" : string.Empty;
    }

    /// <summary>
    /// The prompt as it can be shown on one line: the trailing blank a prompt keeps ("&gt; ") is dropped, and control
    /// characters (a colour code the device sent before the prompt) become a visible placeholder.
    /// </summary>
    private static string Printable(string prompt)
    {
        if (string.IsNullOrEmpty(prompt))
        {
            return string.Empty;
        }
        StringBuilder builder = new StringBuilder(prompt.Length);
        foreach (char character in prompt)
        {
            builder.Append(char.IsControl(character) ? '?' : character);
        }
        return builder.ToString().TrimEnd();
    }
}
