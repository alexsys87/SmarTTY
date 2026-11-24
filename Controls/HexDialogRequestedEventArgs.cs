using SmarTTY.CodeEditing;
using System;

namespace SmarTTY.Controls;

/// <summary>A command of the hex view asks for a dialog of the main window (moved from the WinForms hex menu, removed in stage 3E-5).</summary>
internal sealed class HexDialogRequestedEventArgs : EventArgs
{
    public HexDialogRequestedEventArgs(HexDialogKind kind)
    {
        Kind = kind;
    }

    public HexDialogKind Kind { get; }
}
