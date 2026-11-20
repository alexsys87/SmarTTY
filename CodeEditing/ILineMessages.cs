namespace SmarTTY.CodeEditing;

/// <summary>Which lines have a message shown in a row of its own under them.</summary>
public interface ILineMessages
{
    bool HasMessage(int line);
}
