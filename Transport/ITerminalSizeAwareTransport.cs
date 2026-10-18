namespace SmarTTY.Transport;

/// <summary>
/// A transport whose far end wants to know the size of the terminal (Telnet NAWS). The size is passed on whenever
/// it is known or changes; a transport that does not need it simply does not implement this interface.
/// </summary>
internal interface ITerminalSizeAwareTransport
{
    void SetTerminalSize(int columns, int rows);
}
