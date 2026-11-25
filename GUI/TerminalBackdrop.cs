using SmarTTY.Native;
using SmarTTY.Utilities;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shell;

namespace SmarTTY.GUI;

/// <summary>
/// The see-through background of the terminals of the main window: while the terminal opacity of the settings is below 100 %, the window
/// and the content of its tabs are drawn without a background of their own, the terminals draw theirs with that alpha (see
/// <c>TerminalRenderView.BackgroundAlpha</c>), and what is behind the window shows through, blurred or clear. The tab strips, the menu, the
/// tool bars and the file manager keep their opaque backgrounds.
/// </summary>
/// <remarks>
/// A WPF window with the system frame cannot be see-through on Windows 10 (the DWM ignores the alpha of its pixels and shows black), so such
/// a window is made with <c>AllowsTransparency</c> (per-pixel alpha), which needs a window without the system frame: <see cref="Prepare"/> does
/// it before the window is shown, with a title bar of its own (the title, minimize, maximize and close; dragging, the double click and the
/// system menu through <see cref="WindowChrome"/>) and the maximized size fitted to the work area. That cannot change once the window is
/// shown: a window opened at 100 % becomes see-through the next time it is opened (<see cref="Apply"/> says so), and one opened see-through
/// keeps its own title bar when the opacity is set to 100 % again, with opaque content.
/// </remarks>
internal static class TerminalBackdrop
{
    /// <summary>
    /// True on a window while its terminals are see-through; inherited, so that the styles of the controls inside (the tab controls of the
    /// main window) drop their backgrounds with a trigger on it, and the terminal views draw their background with alpha only then.
    /// </summary>
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive", typeof(bool), typeof(TerminalBackdrop), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    // Set by Prepare on a window made with per-pixel alpha.
    private static readonly DependencyProperty SeeThroughProperty = DependencyProperty.RegisterAttached(
        "SeeThrough", typeof(bool), typeof(TerminalBackdrop), new PropertyMetadata(false));

    // The title bar Prepare put above the content of the window.
    private static readonly DependencyProperty TitleBarProperty = DependencyProperty.RegisterAttached(
        "TitleBar", typeof(FrameworkElement), typeof(TerminalBackdrop), new PropertyMetadata(null));

    // The blur to give the window when its handle exists.
    private static readonly DependencyProperty PendingBlurProperty = DependencyProperty.RegisterAttached(
        "PendingBlur", typeof(bool?), typeof(TerminalBackdrop), new PropertyMetadata(null));

    /// <summary>The height of the title bar of a see-through window, in device-independent pixels.</summary>
    private const double TitleBarHeight = 30;

    /// <summary>
    /// The background of a see-through window where nothing else is drawn: almost transparent, but not quite, so that the mouse is not let
    /// through to the window behind (a layered window does not get the mouse where its pixels have no alpha at all).
    /// </summary>
    private static readonly SolidColorBrush ClearBackground = Frozen(Color.FromArgb(1, 0, 0, 0));

    public static bool GetIsActive(DependencyObject element) => (bool)element.GetValue(IsActiveProperty);

    public static void SetIsActive(DependencyObject element, bool value) => element.SetValue(IsActiveProperty, value);

    /// <summary>Whether the window was made see-through by <see cref="Prepare"/> (its terminals can show what is behind it).</summary>
    public static bool IsSeeThroughWindow(Window window) => window != null && (bool)window.GetValue(SeeThroughProperty);

    /// <summary>
    /// Makes <paramref name="window"/> a window with per-pixel alpha and a title bar of its own when the terminal opacity is below 100 %.
    /// Call it before the window is shown and before anything sets its <see cref="Window.WindowStyle"/>; at 100 % it does nothing.
    /// </summary>
    public static void Prepare(Window window, int opacityPercent)
    {
        if (window == null || WindowChromeRules.ClampOpacityPercent(opacityPercent) >= WindowChromeRules.MaxOpacityPercent)
        {
            return;
        }
        if (IsSeeThroughWindow(window) || new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            return;
        }
        try
        {
            window.WindowStyle = WindowStyle.None;
            window.AllowsTransparency = true;
        }
        catch (InvalidOperationException)
        {
            // Too late for this window: it stays as it is.
            return;
        }
        window.SetValue(SeeThroughProperty, true);
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = TitleBarHeight,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false
        });
        // The content of the window goes below a title bar of its own, in a frame of the colour of the theme.
        UIElement content = window.Content as UIElement;
        window.Content = null;
        FrameworkElement titleBar = CreateTitleBar(window);
        window.SetValue(TitleBarProperty, titleBar);
        DockPanel root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(titleBar, Dock.Top);
        root.Children.Add(titleBar);
        if (content != null)
        {
            root.Children.Add(content);
        }
        Border frame = new Border { BorderThickness = new Thickness(1), Child = root };
        frame.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");
        window.Content = frame;
        window.SourceInitialized += OnSourceInitialized;
        window.StateChanged += (sender, e) => UpdateMaximizeButton(window);
    }

    /// <summary>
    /// Applies the terminal opacity and the blur to <paramref name="window"/>. Returns false when the window would have to be see-through
    /// but was not opened so (it becomes so the next time it is opened; see <see cref="Prepare"/>).
    /// </summary>
    public static bool Apply(Window window, int opacityPercent, bool blur)
    {
        if (window == null)
        {
            return true;
        }
        bool seeThrough = WindowChromeRules.ClampOpacityPercent(opacityPercent) < WindowChromeRules.MaxOpacityPercent;
        if (!IsSeeThroughWindow(window))
        {
            return !seeThrough;
        }
        if (seeThrough)
        {
            SetIsActive(window, true);
            window.Background = ClearBackground;
        }
        else
        {
            // Opaque again: the window keeps its own title bar (it cannot get the system frame back), with the backgrounds of the theme.
            window.ClearValue(IsActiveProperty);
            window.ClearValue(Control.BackgroundProperty);
        }
        SetBlurWhenReady(window, seeThrough && blur);
        return true;
    }

    /// <summary>Shows or hides the title bar of a see-through window (the full screen mode has none).</summary>
    public static void SetTitleBarVisible(Window window, bool visible)
    {
        if (window?.GetValue(TitleBarProperty) is FrameworkElement titleBar)
        {
            titleBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            WindowChrome chrome = WindowChrome.GetWindowChrome(window);
            if (chrome != null)
            {
                chrome.CaptionHeight = visible ? TitleBarHeight : 0;
                chrome.ResizeBorderThickness = visible ? new Thickness(6) : new Thickness(0);
            }
        }
    }

    private static void SetBlurWhenReady(Window window, bool blur)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            window.SetValue(PendingBlurProperty, (bool?)blur);
            return;
        }
        DwmBackdrop.SetBlur(handle, blur);
    }

    private static void OnSourceInitialized(object sender, EventArgs e)
    {
        Window window = (Window)sender;
        window.SourceInitialized -= OnSourceInitialized;
        IntPtr handle = new WindowInteropHelper(window).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WindowHook);
        if (window.GetValue(PendingBlurProperty) is bool blur)
        {
            window.ClearValue(PendingBlurProperty);
            DwmBackdrop.SetBlur(handle, blur);
        }
        UpdateMaximizeButton(window);
    }

    private static IntPtr WindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == DwmBackdrop.WmGetMinMaxInfo && DwmBackdrop.FitMaximizedToWorkArea(hwnd, lParam))
        {
            handled = true;
        }
        return IntPtr.Zero;
    }

    // ------------------------------------------------------------------ the title bar

    private const string CaptionButtonStyle =
        "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
        "<Setter Property='Width' Value='46'/>" +
        "<Setter Property='Focusable' Value='False'/>" +
        "<Setter Property='FontFamily' Value='Segoe MDL2 Assets'/>" +
        "<Setter Property='FontSize' Value='10'/>" +
        "<Setter Property='Foreground' Value='{DynamicResource Theme.Text}'/>" +
        "<Setter Property='Background' Value='Transparent'/>" +
        "<Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>" +
        "<Border x:Name='Back' Background='{TemplateBinding Background}'>" +
        "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>" +
        "<ControlTemplate.Triggers>" +
        "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Back' Property='Background' Value='{DynamicResource Theme.ControlHoverBackground}'/></Trigger>" +
        "<Trigger Property='IsPressed' Value='True'><Setter TargetName='Back' Property='Background' Value='{DynamicResource Theme.ControlPressedBackground}'/></Trigger>" +
        "</ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>";

    private const string CloseButtonStyle =
        "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
        "<Setter Property='Width' Value='46'/>" +
        "<Setter Property='Focusable' Value='False'/>" +
        "<Setter Property='FontFamily' Value='Segoe MDL2 Assets'/>" +
        "<Setter Property='FontSize' Value='10'/>" +
        "<Setter Property='Foreground' Value='{DynamicResource Theme.Text}'/>" +
        "<Setter Property='Background' Value='Transparent'/>" +
        "<Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>" +
        "<Border x:Name='Back' Background='{TemplateBinding Background}'>" +
        "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>" +
        "<ControlTemplate.Triggers>" +
        "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Back' Property='Background' Value='#E81123'/><Setter Property='Foreground' Value='White'/></Trigger>" +
        "<Trigger Property='IsPressed' Value='True'><Setter TargetName='Back' Property='Background' Value='#F1707A'/></Trigger>" +
        "</ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>";

    // The glyphs of Segoe MDL2 Assets (Windows 10 and later) for the caption buttons.
    private const string MinimizeGlyph = "";
    private const string MaximizeGlyph = "";
    private const string RestoreGlyph = "";
    private const string CloseGlyph = "";

    private static FrameworkElement CreateTitleBar(Window window)
    {
        Style captionStyle = (Style)XamlReader.Parse(CaptionButtonStyle);
        Style closeStyle = (Style)XamlReader.Parse(CloseButtonStyle);
        Button minimize = CaptionButton(MinimizeGlyph, "Minimize", captionStyle, () => SystemCommands.MinimizeWindow(window));
        Button maximize = CaptionButton(MaximizeGlyph, "Maximize", captionStyle, () =>
        {
            if (window.WindowState == WindowState.Maximized)
            {
                SystemCommands.RestoreWindow(window);
            }
            else
            {
                SystemCommands.MaximizeWindow(window);
            }
        });
        maximize.Name = "MaximizeButton";
        Button close = CaptionButton(CloseGlyph, "Close", closeStyle, () => SystemCommands.CloseWindow(window));
        DockPanel bar = new DockPanel { Height = TitleBarHeight, LastChildFill = true };
        bar.SetResourceReference(Panel.BackgroundProperty, "Theme.WindowBackground");
        foreach (Button button in new[] { close, maximize, minimize })
        {
            DockPanel.SetDock(button, Dock.Right);
            bar.Children.Add(button);
        }
        TextBlock title = new TextBlock
        {
            Margin = new Thickness(10, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsHitTestVisible = false
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Text");
        title.SetBinding(TextBlock.TextProperty, new Binding(nameof(Window.Title)) { Source = window });
        bar.Children.Add(title);
        // Dragging, the double click and the system menu of the right button are those of the caption area of the WindowChrome.
        return bar;
    }

    private static Button CaptionButton(string glyph, string toolTip, Style style, Action click)
    {
        Button button = new Button { Content = glyph, ToolTip = toolTip, Style = style };
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        button.Click += (sender, e) => click();
        return button;
    }

    private static void UpdateMaximizeButton(Window window)
    {
        if (window.GetValue(TitleBarProperty) is DockPanel bar)
        {
            foreach (object child in bar.Children)
            {
                if (child is Button button && button.Name == "MaximizeButton")
                {
                    bool maximized = window.WindowState == WindowState.Maximized;
                    button.Content = maximized ? RestoreGlyph : MaximizeGlyph;
                    button.ToolTip = maximized ? "Restore" : "Maximize";
                }
            }
        }
    }

    private static SolidColorBrush Frozen(Color color)
    {
        SolidColorBrush brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
