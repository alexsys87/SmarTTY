using System.Collections.Generic;

namespace SmarTTY.Terminal;

/// <summary>What a paste line by line does when the command that ran last has ended.</summary>
public enum LinePasteStep
{
    /// <summary>Go on: the next command runs when the prompt is back.</summary>
    Continue,

    /// <summary>The paste is over and there is nothing to say (every command was run).</summary>
    EndSilently,

    /// <summary>The paste is over and a note says why.</summary>
    EndWithNote
}

/// <summary>
/// "Paste line by line" in a Smart Terminal tab: the commands that are still to be run, one after another. The next one starts only when the previous one has
/// ended; the paste stops at the first command that fails (when <see cref="StopOnError"/> is set) or when it is cancelled.
/// </summary>
public sealed class LinePasteRun
{
    /// <summary>The commands that are still to be run.</summary>
    public readonly Queue<string> Pending;

    /// <summary>How many commands were pasted.</summary>
    public readonly int Total;

    /// <summary>The paste stops after a command that failed.</summary>
    public readonly bool StopOnError;

    /// <summary>How many commands were started.</summary>
    public int Started;

    /// <summary>The paste was cancelled (the command that runs goes on, no other starts).</summary>
    public bool Cancelled;

    public LinePasteRun(List<string> commands, bool stopOnError)
    {
        Pending = new Queue<string>(commands);
        Total = commands.Count;
        StopOnError = stopOnError;
    }

    /// <summary>At a prompt: the paste is over, because it was cancelled, there is nothing left, or the command before failed and the paste stops then.</summary>
    public bool IsOver(bool lastCommandFailed)
    {
        return Cancelled || Pending.Count == 0 || (StopOnError && lastCommandFailed);
    }

    /// <summary>Takes the next command; <paramref name="progressText"/> is what the progress window says ("Command 2 of 5: ...", a long command cut short).</summary>
    public string TakeNext(out string progressText)
    {
        string command = Pending.Dequeue();
        Started++;
        string shown = command.Length > 60 ? command.Substring(0, 57) + "..." : command;
        progressText = "Command " + Started + " of " + Total + ": " + shown;
        return command;
    }

    /// <summary>
    /// After a command has ended: whether the paste goes on, and if it has to stop here, the note to print (<paramref name="note"/>, null when there is none).
    /// </summary>
    public LinePasteStep AfterCommand(bool lastCommandFailed, out string note)
    {
        note = null;
        if (Pending.Count == 0)
        {
            return LinePasteStep.EndSilently;
        }
        if (Cancelled)
        {
            note = "[paste cancelled, " + Pending.Count + " command(s) not run]";
        }
        else if (StopOnError && lastCommandFailed)
        {
            note = "[paste stopped after a failed command, " + Pending.Count + " command(s) not run]";
        }
        else
        {
            return LinePasteStep.Continue;
        }
        return LinePasteStep.EndWithNote;
    }

    /// <summary>Cancels the paste. False when it was cancelled already.</summary>
    public bool Cancel()
    {
        if (Cancelled)
        {
            return false;
        }
        Cancelled = true;
        return true;
    }
}
