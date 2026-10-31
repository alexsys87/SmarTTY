using SmarTTY.CodeEditing;
using System;

namespace SmarTTY.Controls;

/// <summary>Arguments for the "open file" request raised by a terminal control when the user activates a link.
/// For SSH tabs the path is a remote (server-side) file; for device (serial/network) terminal tabs it is a local
/// file path or an URL, because the terminal itself runs on this machine.</summary>
public class OpenFileEventArgs : EventArgs
{
    public string Path;

    public int? Line;

    public int? Column;

    /// <summary>What <see cref="Column"/> counts (the tool that wrote the message decides); characters when nothing is said.</summary>
    public ColumnUnit ColumnUnit;
}
