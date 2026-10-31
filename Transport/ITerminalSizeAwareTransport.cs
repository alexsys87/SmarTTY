namespace SmarTTY.Transport;

/// <summary>
/// A transport whose far end wants to know the size of the terminal (Telnet NAWS). The size is passed on whenever
/// it is known or changes; a transport that does not need it simply does not implement this interface.
/// </summary>
internal interface ITerminalSizeAwareTransport
{
    void SetTerminalSize(int columns, int rows);
}

/// <summary>
/// A transport that tells the far end which character set the terminal uses (Telnet CHARSET) and follows a change of
/// the terminal's encoding while connected.
/// </summary>
internal interface ITextEncodingAwareTransport
{
    void SetTextEncoding(System.Text.Encoding encoding);
}
