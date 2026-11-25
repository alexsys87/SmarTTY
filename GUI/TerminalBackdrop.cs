using SmarTTY.Native;
using SmarTTY.Utilities;
using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace SmarTTY.GUI;

/// <summary>
/// The see-through background of the terminals of a window (the main window): while the terminal opacity of the settings is below
/// 100 %, the window and the content of its tabs are drawn without a background of their own, the terminals draw theirs with that
/// alpha, and the DWM shows what is behind the window through it (<see cref="DwmBackdrop"/>, blurred or clear). The tab strips, the
/// menu, the tool bars and the file manager keep their opaque backgrounds.
/// </summary>
internal static class TerminalBackdrop
{
    /// <summary>
    /// True on a window while its terminals are see-through; inherited, so that the styles of the controls inside (the tab controls of
    /// the main window) drop their backgrounds with a trigger on it.
    /// </summary>
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive", typeof(bool), typeof(TerminalBackdrop), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    // The background of the composition target before the window was made see-through (given back when it is opaque again).
    private static readonly DependencyProperty SavedBackgroundColorProperty = DependencyProperty.RegisterAttached(
        "SavedBackgroundColor", typeof(Color?), typeof(TerminalBackdrop), new PropertyMetadata(null));

    // The settings to apply when the window handle exists (the window is made before it is shown).
    private static readonly DependencyProperty PendingProperty = DependencyProperty.RegisterAttached(
        "Pending", typeof(Tuple<int, bool>), typeof(TerminalBackdrop), new PropertyMetadata(null));

    public static bool GetIsActive(DependencyObject element) => (bool)element.GetValue(IsActiveProperty);

    public static void SetIsActive(DependencyObject element, bool value) => element.SetValue(IsActiveProperty, value);

    /// <summary>
    /// Makes the terminals of <paramref name="window"/> see-through with this opacity (100: opaque, as they always were), the background
    /// behind them blurred or not.
    /// </summary>
    public static void Apply(Window window, int opacityPercent, bool blur)
    {
        if (window == null)
        {
            return;
        }
        int percent = WindowChromeRules.ClampOpacityPercent(opacityPercent);
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            bool hooked = window.GetValue(PendingProperty) != null;
            window.SetValue(PendingProperty, Tuple.Create(percent, blur));
            if (!hooked)
            {
                window.SourceInitialized += OnSourceInitialized;
            }
            return;
        }
        HwndSource source = HwndSource.FromHwnd(handle);
        if (source?.CompositionTarget == null)
        {
            return;
        }
        if (percent < WindowChromeRules.MaxOpacityPercent)
        {
            if (window.GetValue(SavedBackgroundColorProperty) == null)
            {
                window.SetValue(SavedBackgroundColorProperty, (Color?)source.CompositionTarget.BackgroundColor);
            }
            SetIsActive(window, true);
            window.Background = Brushes.Transparent;
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
            DwmBackdrop.Enable(handle, blur);
            return;
        }
        if (!GetIsActive(window))
        {
            // Never made see-through: nothing to give back, and no call to the DWM for the usual case.
            return;
        }
        window.ClearValue(IsActiveProperty);
        window.ClearValue(Window.BackgroundProperty);
        if (window.GetValue(SavedBackgroundColorProperty) is Color saved)
        {
            source.CompositionTarget.BackgroundColor = saved;
        }
        window.ClearValue(SavedBackgroundColorProperty);
        DwmBackdrop.Disable(handle);
    }

    private static void OnSourceInitialized(object sender, EventArgs e)
    {
        Window window = (Window)sender;
        window.SourceInitialized -= OnSourceInitialized;
        if (window.GetValue(PendingProperty) is Tuple<int, bool> pending)
        {
            window.ClearValue(PendingProperty);
            Apply(window, pending.Item1, pending.Item2);
        }
    }
}
