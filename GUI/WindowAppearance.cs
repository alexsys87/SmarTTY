using SmarTTY.GUI.Styles;
using SmarTTY.Native;
using SmarTTY.Utilities;
using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using VisualTTY;

namespace SmarTTY.GUI;

/// <summary>
/// What every window of the program gets without asking for it: the title bar and frame in the colours of the theme
/// (<see cref="DwmTitleBar"/>), the opacity from the settings, and the size the user gave a window that can be resized.
/// </summary>
/// <remarks>
/// <c>SecondaryWindowStyle</c> (every window uses it) sets <see cref="ManageProperty"/>; <see cref="Install"/> adds a class handler of
/// <c>Loaded</c> for a window that has no such style. Both end in <see cref="Attach"/>, which does its work once for a window:
/// at <c>SourceInitialized</c> (the window handle exists, nothing is shown yet) the title bar, the opacity and the remembered size
/// are applied, and when the window closes its size is remembered.
/// </remarks>
internal static class WindowAppearance
{
    /// <summary>Set to true by the window style: the window gets the appearance of this class.</summary>
    public static readonly DependencyProperty ManageProperty = DependencyProperty.RegisterAttached(
        "Manage", typeof(bool), typeof(WindowAppearance), new PropertyMetadata(false, OnManageChanged));

    private static readonly DependencyProperty AttachedProperty = DependencyProperty.RegisterAttached(
        "Attached", typeof(bool), typeof(WindowAppearance), new PropertyMetadata(false));

    private static bool _installed;

    private static int _opacityPercent = WindowChromeRules.MaxOpacityPercent;

    public static bool GetManage(DependencyObject element) => (bool)element.GetValue(ManageProperty);

    public static void SetManage(DependencyObject element, bool value) => element.SetValue(ManageProperty, value);

    /// <summary>The opacity of all windows in per cent (100: not transparent); a new value is applied to the open windows at once.</summary>
    public static int OpacityPercent
    {
        get => _opacityPercent;
        set
        {
            int percent = WindowChromeRules.ClampOpacityPercent(value);
            if (percent == _opacityPercent)
            {
                return;
            }
            _opacityPercent = percent;
            ForEachWindow(ApplyOpacity);
        }
    }

    /// <summary>Hooks the theme and the windows that have no style; call once at the start of the program.</summary>
    public static void Install()
    {
        if (_installed)
        {
            return;
        }
        _installed = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
        ThemeManager.ThemeChanged += (sender, e) => ForEachWindow(ApplyTitleBar);
        // The high contrast theme of the system takes over the colours of the frame (and gives them back when it is switched off).
        SystemParameters.StaticPropertyChanged += (sender, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.HighContrast))
            {
                ForEachWindow(ApplyTitleBar);
            }
        };
    }

    private static void OnManageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Window window && (bool)e.NewValue)
        {
            Attach(window);
        }
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window)
        {
            Attach(window);
        }
    }

    private static void Attach(Window window)
    {
        if ((bool)window.GetValue(AttachedProperty))
        {
            return;
        }
        window.SetValue(AttachedProperty, true);
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            // The window is open already (it had no style): do what SourceInitialized would have done, minus the size.
            ApplyTitleBar(window);
            ApplyOpacity(window);
            HookStyle(window);
            int openWidth = (int)Math.Round(window.ActualWidth);
            int openHeight = (int)Math.Round(window.ActualHeight);
            window.Closing += (sender, e) => RememberSize(window, openWidth, openHeight);
            return;
        }
        window.SourceInitialized += (sender, e) =>
        {
            ApplyTitleBar(window);
            ApplyOpacity(window);
            HookStyle(window);
            // The window shows its first frame and is activated after this: the opacity is put again when it is there (WPF sets
            // up the surface of the window while it is shown, and a style it changes then must not take the opacity away).
            window.ContentRendered += (s, args) => ApplyOpacity(window);
            window.Activated += (s, args) => ApplyOpacity(window);
            ApplyRememberedSize(window, out int initialWidth, out int initialHeight);
            window.Closing += (s, args) => RememberSize(window, initialWidth, initialHeight);
        };
    }

    private static void ForEachWindow(Action<Window> action)
    {
        Application application = Application.Current;
        if (application == null)
        {
            return;
        }
        Window[] windows = new Window[application.Windows.Count];
        application.Windows.CopyTo(windows, 0);
        foreach (Window window in windows)
        {
            if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
            {
                action(window);
            }
        }
    }

    // ------------------------------------------------------------------ the title bar

    private static void ApplyTitleBar(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (SystemParameters.HighContrast)
        {
            DwmTitleBar.Apply(handle, dark: false, caption: null, text: null, border: null);
            return;
        }
        Color? background = ThemeColor(window, "Theme.WindowBackground");
        if (background == null)
        {
            return;
        }
        Color back = background.Value;
        Color? text = ThemeColor(window, "Theme.Text");
        Color? border = ThemeColor(window, "Theme.Border");
        DwmTitleBar.Apply(
            handle,
            WindowChromeRules.IsDark(back.R, back.G, back.B),
            WindowChromeRules.ToColorRef(back.R, back.G, back.B),
            text.HasValue ? WindowChromeRules.ToColorRef(text.Value.R, text.Value.G, text.Value.B) : (int?)null,
            border.HasValue ? WindowChromeRules.ToColorRef(border.Value.R, border.Value.G, border.Value.B) : (int?)null);
    }

    private static Color? ThemeColor(Window window, string key)
    {
        return window.TryFindResource(key) is SolidColorBrush brush ? brush.Color : (Color?)null;
    }

    // ------------------------------------------------------------------ the opacity

    private static void ApplyOpacity(Window window)
    {
        if (window.AllowsTransparency)
        {
            // The window draws its own transparency (WPF owns the layered style then).
            return;
        }
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }
        byte alpha = WindowChromeRules.OpacityToAlpha(_opacityPercent);
        // An opaque window that is not layered needs nothing (the usual case: no call at all).
        if (alpha == 255 && !DwmTitleBar.IsLayered(handle))
        {
            return;
        }
        if (!DwmTitleBar.SetOpacity(handle, alpha))
        {
            System.Diagnostics.Debug.WriteLine("WindowAppearance: the opacity " + _opacityPercent + " % could not be set on " + window.GetType().Name);
        }
    }

    /// <summary>
    /// Watches the extended style of a window: when something else (WPF itself, which writes again the extended style it keeps for
    /// the window) is about to take the layered style away while the opacity is below 100 %, the style keeps it.
    /// </summary>
    /// <remarks>
    /// This is done in <c>WM_STYLECHANGING</c>, before the change, and not by putting the style back after it: WPF took the style away
    /// again each time, and the opacity put back from the dispatcher at the background priority every time made a loop that never ended.
    /// It kept the dispatcher busy, so the work at a lower priority never ran (the Smart Terminal did not start and the file manager kept
    /// loading), and the window was never transparent.
    /// </remarks>
    private static void HookStyle(Window window)
    {
        if (window.AllowsTransparency)
        {
            // WPF owns the layered style of such a window.
            return;
        }
        HwndSource source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        source?.AddHook(StyleHook);
    }

    private static IntPtr StyleHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == DwmTitleBar.WmStyleChanging && (int)wParam.ToInt64() == DwmTitleBar.ExStyleIndex
            && _opacityPercent < WindowChromeRules.MaxOpacityPercent)
        {
            DwmTitleBar.KeepLayeredStyle(lParam);
        }
        return IntPtr.Zero;
    }

    // ------------------------------------------------------------------ the size

    /// <summary>The windows that keep a place of their own in the GUI preferences (and the size with it).</summary>
    private static bool KeepsOwnPlacement(Window window)
    {
        return window is MainWindow || window is ConnectionSelectionWindow;
    }

    private static bool CanChangeWidth(Window window) => IsResizable(window) && window.SizeToContent != SizeToContent.Width && window.SizeToContent != SizeToContent.WidthAndHeight;

    private static bool CanChangeHeight(Window window) => IsResizable(window) && window.SizeToContent != SizeToContent.Height && window.SizeToContent != SizeToContent.WidthAndHeight;

    private static bool IsResizable(Window window)
    {
        return window.ResizeMode == ResizeMode.CanResize || window.ResizeMode == ResizeMode.CanResizeWithGrip;
    }

    private static string KeyOf(Window window) => window.GetType().FullName;

    /// <summary>
    /// Gives a window that opens at its own size the size the user left it at. Windows centre themselves before the handle exists,
    /// so the window keeps the middle it had: the new size is put around the old centre, inside its monitor.
    /// </summary>
    private static void ApplyRememberedSize(Window window, out int initialWidth, out int initialHeight)
    {
        initialWidth = 0;
        initialHeight = 0;
        try
        {
            if (KeepsOwnPlacement(window) || (!CanChangeWidth(window) && !CanChangeHeight(window)))
            {
                return;
            }
            IntPtr handle = new WindowInteropHelper(window).Handle;
            CompositionTarget target = PresentationSource.FromVisual(window)?.CompositionTarget;
            if (target == null || !DwmTitleBar.TryGetBounds(handle, out int left, out int top, out int widthPx, out int heightPx))
            {
                return;
            }
            double scaleX = target.TransformFromDevice.M11;
            double scaleY = target.TransformFromDevice.M22;
            initialWidth = (int)Math.Round(widthPx * scaleX);
            initialHeight = (int)Math.Round(heightPx * scaleY);
            CommonGUIPreferences preferences = GuiPreferencesStore.Current;
            if (preferences == null || !WindowSizeList.TryGet(preferences.WindowSizes, KeyOf(window), out int savedWidth, out int savedHeight))
            {
                return;
            }
            ScreenRect monitor = DisplayMonitors.BoundsAt(left + widthPx / 2, top + heightPx / 2);
            double width = CanChangeWidth(window)
                ? WindowChromeRules.ClampLength(savedWidth, window.MinWidth, window.MaxWidth, monitor.Width * scaleX)
                : double.NaN;
            double height = CanChangeHeight(window)
                ? WindowChromeRules.ClampLength(savedHeight, window.MinHeight, window.MaxHeight, monitor.Height * scaleY)
                : double.NaN;
            if (double.IsNaN(width) && double.IsNaN(height))
            {
                return;
            }
            double newWidthPx = double.IsNaN(width) ? widthPx : width / scaleX;
            double newHeightPx = double.IsNaN(height) ? heightPx : height / scaleY;
            double newLeftPx = WindowChromeRules.CenteredStart(left + widthPx / 2.0, newWidthPx, monitor.Left, monitor.Left + monitor.Width);
            double newTopPx = WindowChromeRules.CenteredStart(top + heightPx / 2.0, newHeightPx, monitor.Top, monitor.Top + monitor.Height);
            if (!double.IsNaN(width))
            {
                window.Width = width;
            }
            if (!double.IsNaN(height))
            {
                window.Height = height;
            }
            // The size the window opens at is the one it is closed at when the user leaves it alone: nothing to remember then.
            initialWidth = (int)Math.Round(double.IsNaN(width) ? initialWidth : width);
            initialHeight = (int)Math.Round(double.IsNaN(height) ? initialHeight : height);
            window.Left = newLeftPx * scaleX;
            window.Top = newTopPx * scaleY;
        }
        catch
        {
            // A window that cannot be sized as remembered opens as it is.
        }
    }

    /// <summary>Remembers the size a window is closed at when the user changed it (it differs from the one the window opened at).</summary>
    private static void RememberSize(Window window, int initialWidth, int initialHeight)
    {
        try
        {
            if (KeepsOwnPlacement(window) || !IsResizable(window) || window.WindowState == WindowState.Minimized)
            {
                return;
            }
            bool widthKept = CanChangeWidth(window);
            bool heightKept = CanChangeHeight(window);
            if (!widthKept && !heightKept)
            {
                return;
            }
            // RestoreBounds is the normal (not maximized) size, in device-independent pixels.
            Rect bounds = window.WindowState == WindowState.Normal ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight) : window.RestoreBounds;
            int width = (int)Math.Round(bounds.Width);
            int height = (int)Math.Round(bounds.Height);
            if (width <= 0 || height <= 0)
            {
                return;
            }
            // A pixel of rounding at a scaling of 125 % or 150 % is no change.
            bool changed = (widthKept && Math.Abs(width - initialWidth) > 1) || (heightKept && Math.Abs(height - initialHeight) > 1);
            if (!changed)
            {
                return;
            }
            CommonGUIPreferences preferences = GuiPreferencesStore.Current;
            if (preferences == null)
            {
                return;
            }
            // A window that cannot change one of the two keeps what was remembered for it (or what it opened at).
            WindowSizeList.TryGet(preferences.WindowSizes, KeyOf(window), out int oldWidth, out int oldHeight);
            int storedWidth = widthKept ? width : (oldWidth > 0 ? oldWidth : width);
            int storedHeight = heightKept ? height : (oldHeight > 0 ? oldHeight : height);
            if (WindowSizeList.Set(preferences.WindowSizes, KeyOf(window), storedWidth, storedHeight))
            {
                GuiPreferencesStore.Save();
            }
        }
        catch
        {
            // Not being able to remember a size is not worth a message.
        }
    }
}
