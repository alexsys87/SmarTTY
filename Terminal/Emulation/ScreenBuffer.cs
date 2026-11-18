using SmarTTY.TextEditing;

namespace SmarTTY.Terminal.Emulation;

public class ScreenBuffer
{
    public EditableTextDocument Document = new EditableTextDocument();

    public readonly LineInfoStore Lines;

    public GridPoint Cursor;

    public int SavedTopLine;

    public int MaxScrollback;

    public int FirstRow { get; set; }

    public ScreenBuffer(ILineInfoHost host, int maxScrollback = int.MaxValue)
    {
        Lines = new LineInfoStore(host);
        this.MaxScrollback = maxScrollback;
    }
}
