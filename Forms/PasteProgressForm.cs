using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SmarTTY.Forms;

/// <summary>
/// The small window of a paste that takes time (a large paste, a slow paste, a paste line by line): what is done so far, a
/// progress bar and Cancel. It does not take the keyboard focus, so that the terminal keeps getting the keys (Esc
/// cancels the paste there).
/// </summary>
public sealed class PasteProgressForm : Form
{
    private sealed class RootWindow : IWin32Window
    {
        public IntPtr Handle { get; }

        public RootWindow(IntPtr handle)
        {
            Handle = handle;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    private const uint GaRoot = 2;

    private const int WsExNoActivate = 0x08000000;

    private const int WsExToolWindow = 0x00000080;

    private readonly Label _status;

    private readonly ProgressBar _bar;

    /// <summary>The user pressed Cancel.</summary>
    public event EventHandler CancelRequested;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= WsExNoActivate | WsExToolWindow;
            return parameters;
        }
    }

    public PasteProgressForm(string title)
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        Font = SystemFonts.MessageBoxFont;
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ControlBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(380, 96);

        _status = new Label { AutoSize = false, Location = new Point(12, 10), Size = new Size(356, 20), Text = "" };
        _bar = new ProgressBar { Location = new Point(12, 34), Size = new Size(356, 18), Minimum = 0, Maximum = 100, Style = ProgressBarStyle.Continuous };
        Button cancel = new Button { Text = "Cancel (Esc)", Location = new Point(268, 60), Size = new Size(100, 26) };
        cancel.Click += (object sender, EventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);
        Controls.Add(_status);
        Controls.Add(_bar);
        Controls.Add(cancel);
    }

    /// <summary>Shows <paramref name="done"/> of <paramref name="total"/> (the bar), with <paramref name="text"/> above it.</summary>
    public void SetProgress(long done, long total, string text)
    {
        _status.Text = text ?? "";
        if (total <= 0)
        {
            _bar.Value = 0;
            return;
        }
        _bar.Value = (int)Math.Max(0, Math.Min(100, done * 100 / total));
    }

    /// <summary>Opens the window over the bottom edge of <paramref name="anchor"/> without taking the focus from it.</summary>
    public void ShowNear(Control anchor)
    {
        IntPtr root = IntPtr.Zero;
        try
        {
            if (anchor != null && anchor.IsHandleCreated)
            {
                Rectangle bounds = anchor.RectangleToScreen(anchor.ClientRectangle);
                Location = new Point(bounds.Left + Math.Max((bounds.Width - Width) / 2, 0), Math.Max(bounds.Bottom - Height - 24, bounds.Top));
                root = GetAncestor(anchor.Handle, GaRoot);
            }
        }
        catch
        {
        }
        if (root != IntPtr.Zero)
        {
            Show(new RootWindow(root));
        }
        else
        {
            Show();
        }
    }
}
