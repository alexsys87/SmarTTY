namespace SmarTTY.Terminal.Emulation;

/// <summary>
/// Where the parser writes down what an escape sequence meant (a debugging aid of a Debug build: the control passes null otherwise, and
/// the calls are made with <c>?.</c>).
/// </summary>
public interface IEscapeTrace
{
    void SetPosition(int position);

    void SetDescription(string text);

    void SetWarning(string text);

    void SetRaw(string text);
}
