using System;

namespace SmarTTY.GUI.HexView;

/// <summary>What a search in the hex editor found (or did not find); moved from the WinForms hex control, removed in stage 3E-5.</summary>
public sealed class HexSearchEventArgs : EventArgs
{
    public bool Found { get; internal set; }

    public int Offset { get; internal set; }

    public int Length { get; internal set; }
}
