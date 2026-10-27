using SmarTTY.Controls;
using SmarTTY.Utilities;
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace SmarTTY.GUI;

/// <summary>
/// The small window of a paste that takes time (a large paste, a slow paste, a paste line by line): what is done so
/// far, a progress bar and Cancel. It does not take the keyboard focus, so the terminal keeps getting the keys (Esc
/// cancels the paste there). The WPF counterpart of <c>Forms/PasteProgressForm</c>.
/// </summary>
public partial class PasteProgressWindow : Window, IPasteProgress
{
    private const int GwlExStyle = -20;

    private const int WsExNoActivate = 0x08000000;

    private const int WsExToolWindow = 0x00000080;

    private const uint GaRoot = 2;

    private System.Windows.Forms.Control _anchor;

    private bool _closed;

    public PasteProgressWindow(string title)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title ?? "";
    }

    /// <summary>The user pressed Cancel.</summary>
    public event EventHandler CancelRequested;

    /// <summary>Shows <paramref name="done"/> of <paramref name="total"/> (the bar), with <paramref name="text"/> above it.</summary>
    public void SetProgress(long done, long total, string text)
    {
        StatusText.Text = text ?? "";
        Progress.Value = PasteProgressRules.BarValue(done, total);
    }

    /// <summary>Opens the window over the bottom edge of <paramref name="anchor"/> without taking the focus from it.</summary>
    public void ShowNear(System.Windows.Forms.Control anchor)
    {
        _anchor = anchor;
        Window owner = OwnerOf(anchor);
        if (owner != null)
        {
            Owner = owner;
        }
        else
        {
            // Without an owner the window could end up behind the terminal; keep it above instead.
            Topmost = true;
        }
        Show();
        PositionNearAnchor();
    }

    void IPasteProgress.Close()
    {
        Close();
    }

    void IDisposable.Dispose()
    {
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // WS_EX_NOACTIVATE keeps the keyboard focus in the terminal when the window is clicked;
        // WS_EX_TOOLWINDOW keeps it out of the taskbar and the Alt-Tab list, as the WinForms form did.
        IntPtr handle = new WindowInteropHelper(this).Handle;
        int exStyle = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, exStyle | WsExNoActivate | WsExToolWindow);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        base.OnClosed(e);
    }

    /// <summary>
    /// The WPF window that hosts <paramref name="anchor"/>, so the progress window is owned by it. The terminal control
    /// is hosted in a WinForms host inside a WPF window, so the root of its Win32 handle is that WPF window.
    /// </summary>
    private static Window OwnerOf(System.Windows.Forms.Control anchor)
    {
        try
        {
            if (anchor != null && anchor.IsHandleCreated)
            {
                return HwndSource.FromHwnd(GetAncestor(anchor.Handle, GaRoot))?.RootVisual as Window;
            }
        }
        catch
        {
        }
        return null;
    }

    private void PositionNearAnchor()
    {
        System.Windows.Forms.Control anchor = _anchor;
        if (anchor == null || !anchor.IsHandleCreated)
        {
            return;
        }
        PresentationSource source = PresentationSource.FromVisual(this);
        Matrix toDevice = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        Matrix fromDevice = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        try
        {
            Rectangle bounds = anchor.RectangleToScreen(anchor.ClientRectangle);
            double widthPx = ActualWidth * toDevice.M11;
            double heightPx = ActualHeight * toDevice.M22;
            double leftPx = PasteProgressRules.NearLeft(bounds.Left, bounds.Width, widthPx);
            double topPx = PasteProgressRules.NearTop(bounds.Top, bounds.Bottom, heightPx, 24);
            System.Windows.Point dip = fromDevice.Transform(new System.Windows.Point(leftPx, topPx));
            Left = dip.X;
            Top = dip.Y;
        }
        catch
        {
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_closed)
        {
            CancelRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    private static void SetWindowLong(IntPtr hwnd, int index, int value)
    {
        if (IntPtr.Size == 8)
        {
            SetWindowLongPtr(hwnd, index, new IntPtr(value));
        }
        else
        {
            SetWindowLong32(hwnd, index, value);
        }
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
