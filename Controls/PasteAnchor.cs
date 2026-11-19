using System;
using System.Drawing;
using System.Windows.Forms;

namespace SmarTTY.Controls;

/// <summary>
/// The terminal a paste belongs to, as the paste needs it: a window handle for the clipboard and as the owner of its questions, the thread
/// its steps run on, whether the terminal is still there, and where on the screen it is (the progress window and the paste menu appear
/// there). It used to be the WinForms control itself; the WPF terminal of stage 2 has no control, so it gives an anchor of its own.
/// </summary>
public abstract class PasteAnchor : IWin32Window
{
    /// <summary>A window handle of the terminal (or of the window it is in).</summary>
    public abstract IntPtr Handle { get; }

    /// <summary>The terminal is still there and can run <see cref="Post"/>ed work.</summary>
    public abstract bool IsAlive { get; }

    /// <summary>Runs an action on the thread of the terminal, later.</summary>
    public abstract void Post(Action action);

    /// <summary>The area of the terminal on the screen, in device pixels.</summary>
    public abstract Rectangle ScreenBounds { get; }

    /// <summary>The anchor of a WinForms control.</summary>
    public static PasteAnchor For(Control control)
    {
        return new ControlAnchor(control);
    }

    private sealed class ControlAnchor : PasteAnchor
    {
        private readonly Control _control;

        public ControlAnchor(Control control)
        {
            _control = control;
        }

        public override IntPtr Handle => _control.Handle;

        public override bool IsAlive => !_control.IsDisposed && _control.IsHandleCreated;

        public override void Post(Action action)
        {
            _control.BeginInvoke(action);
        }

        public override Rectangle ScreenBounds => _control.RectangleToScreen(_control.ClientRectangle);
    }
}
