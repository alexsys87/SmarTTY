using System.Drawing;

namespace SmarTTY.TextEditing;

internal interface ICaret
{
    int Width { get; set; }

    int Height { get; set; }

    bool OverwriteMode { get; set; }

    bool UnderlineStyle { get; set; }

    bool ShowWhenUnfocused { get; set; }

    bool IsEnabled { get; }

    void Enable();

    void Disable();

    void SetPosition(Point point);
}
