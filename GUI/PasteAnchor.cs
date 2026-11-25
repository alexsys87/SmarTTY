using System;
using SmarTTY.Controls;
using VisualTTY;

namespace SmarTTY.GUI;

/// <summary>
/// The terminal a paste belongs to, as the paste needs it: a window handle for the clipboard and as the owner of its questions, the thread
/// its steps run on, whether the terminal is still there, and where on the screen it is (the progress window and the paste menu appear
/// there). It used to be the WinForms control itself; each WPF terminal gives an anchor of its own.
/// </summary>
public abstract class PasteAnchor : IDialogOwner
{
    /// <summary>A window handle of the terminal (or of the window it is in).</summary>
    public abstract IntPtr Handle { get; }

    /// <summary>The terminal is still there and can run <see cref="Post"/>ed work.</summary>
    public abstract bool IsAlive { get; }

    /// <summary>Runs an action on the thread of the terminal, later.</summary>
    public abstract void Post(Action action);

    /// <summary>The area of the terminal on the screen, in device pixels.</summary>
    public abstract ScreenRect ScreenBounds { get; }
}
