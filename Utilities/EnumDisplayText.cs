using System;

namespace SmarTTY.Utilities;

public class EnumDisplayText : Attribute
{
    public readonly string Text;

    public EnumDisplayText(string text)
    {
        this.Text = text;
    }
}
