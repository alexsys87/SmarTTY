using SmarTTY.Controls;
using System;
using System.Windows.Forms;

namespace SmarTTY.TextEditing;

public interface IScrollBar : IDisposable
{
    ScrollLimits Limits { get; set; }

    int Value { get; set; }

    bool Visible { get; set; }

    int Width { get; }

    int Height { get; }

    int Maximum { get; }

    event EventHandler ValueChanged;

    void Register(ReadonlyTextBrowser readonlyTextBrowser, DockStyle dockStyle);
}
