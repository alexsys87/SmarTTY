
using System;

namespace SmarTTY.GUI.Backend;

public struct InitialCommand(string initialCommand, bool echoInitialCommand, Action tabKeptAfterExit = null)
{
    public readonly string Command = initialCommand;

    public readonly bool Echo = echoInitialCommand;

    /// <summary>
    /// Called (on the UI thread) when the command has exited and the user chose to keep its tab open,
    /// e.g. to return to the dialog that started the command.
    /// </summary>
    public readonly Action TabKeptAfterExit = tabKeptAfterExit;
}
