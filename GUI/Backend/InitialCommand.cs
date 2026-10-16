
namespace SmarTTY.GUI.Backend;

public struct InitialCommand(string initialCommand, bool echoInitialCommand)
{
    public readonly string Command = initialCommand;

    public readonly bool Echo = echoInitialCommand;
}
