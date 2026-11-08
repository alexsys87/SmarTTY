using System;
using System.Collections.Generic;

namespace SmarTTY.Terminal;

/// <summary>
/// What the "Command history" window (terminal menu) shows and does for one terminal tab: the Smart Terminal and the
/// COM/TCP terminal can delete commands, a classic shell tab can only put one into the terminal.
/// </summary>
public sealed class CommandHistorySource
{
    /// <summary>Names the tab in the title of the window.</summary>
    public string Title { get; set; }

    /// <summary>The commands, the newest first. Called on a background thread: it may wait for the server.</summary>
    public Func<IReadOnlyList<string>> Load { get; set; }

    /// <summary>Removes the commands from the history (on the UI thread); null when commands cannot be deleted here.</summary>
    public Action<IReadOnlyList<string>> Delete { get; set; }

    /// <summary>Said in the confirmation before commands are deleted, or null.</summary>
    public string DeleteWarning { get; set; }

    /// <summary>Puts the chosen command into the command line of the tab (on the UI thread), ready to run with Enter.</summary>
    public Action<string> Insert { get; set; }

    /// <summary>Shown under the list, or null.</summary>
    public string Note { get; set; }

    /// <summary>
    /// Opens the window for a source. Set by the application to the WPF window; null where there is none (the Mono test
    /// harness), and then nothing is shown.
    /// </summary>
    public static Action<CommandHistorySource> ShowWindow { get; set; }

    /// <summary>Opens the window, if the application has one.</summary>
    public void Show()
    {
        ShowWindow?.Invoke(this);
    }
}
