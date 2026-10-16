
namespace SmarTTY.TextEditing;

public interface ITextSource
{
    int LineCount { get; }

    ILineCollection Lines { get; }

    string GetText();
}
