using SmarTTY.Ssh;
using System;
using System.Collections.Generic;

namespace SmarTTY.Terminal;

/// <summary>
/// The Smart Terminal tab as the completion of a command line sees it: where the shell is, what it has in its environment, the SSH
/// session to ask it things, the command that runs a command the way the tab does, and the UI thread. Every member is one that
/// <see cref="CompletionSession"/> or <see cref="SmarTTY.Completion.ShellCompletionAnalyzer"/> called on the control before.
/// </summary>
public interface ISmartTerminalHost
{
    /// <summary>The directory the shell is in.</summary>
    string CurrentDirectory { get; }

    /// <summary>The home directory of the user (what "~" stands for).</summary>
    string HomeDirectory { get; }

    /// <summary>The tab has environment variables of its own, which the commands it runs get.</summary>
    bool HasEnvironment { get; }

    /// <summary>The names of the environment variables of the tab that have a value.</summary>
    IEnumerable<string> EnvironmentNames { get; }

    /// <summary>The connection (it is replaced when the tab reconnects, so read it each time).</summary>
    SshSession Session { get; }

    /// <summary>The analyzer of the shell of the connection.</summary>
    SmarTTY.Completion.ShellCompletionAnalyzer ShellAnalyzer { get; }

    /// <summary>The command line that runs <paramref name="command"/> on the server the way the tab runs its commands (its environment, its shell).</summary>
    string BuildRemoteCommand(string command, bool? interactive = null);

    /// <summary>
    /// Queues <paramref name="action"/> for the UI thread. False when it was not queued because the tab has been closed or has no window
    /// yet; the queue itself may throw <see cref="InvalidOperationException"/> when the window goes away at that moment.
    /// </summary>
    bool TryBeginInvoke(Action action);
}
