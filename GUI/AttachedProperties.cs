using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SmarTTY.GUI;

public static class AttachedProperties
{
    public static readonly DependencyProperty MenuPopupBackgroundOverride = DependencyProperty.RegisterAttached("MenuPopupBackgroundOverride", typeof(Brush), typeof(AttachedProperties), (PropertyMetadata)new FrameworkPropertyMetadata((object)null, (PropertyChangedCallback)MenuPopupBackgroundOverrideChanged));

    public static void SetMenuPopupBackgroundOverride(UIElement element, object value)
    {
        ((DependencyObject)element).SetValue(MenuPopupBackgroundOverride, value);
    }

    public static Brush GetMenuPopupBackgroundOverride(UIElement element)
    {
        return (Brush)((DependencyObject)element).GetValue(MenuPopupBackgroundOverride);
    }

    private static void MenuPopupBackgroundOverrideChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((FrameworkElement)((d is Border) ? d : null)).Loaded -= HookedBorderLoaded;
        ((FrameworkElement)((d is Border) ? d : null)).Loaded += HookedBorderLoaded;
    }

    private static void HookedBorderLoaded(object sender, EventArgs e)
    {
        Border border = sender as Border;
        if (border != null)
        {
            Brush menuPopupBackgroundOverride = GetMenuPopupBackgroundOverride((UIElement)border);
            if (VisualTreeHelper.GetParent((DependencyObject)border) == null && menuPopupBackgroundOverride != null)
            {
                border.Background = menuPopupBackgroundOverride;
            }
        }
    }

    static AttachedProperties()
    {
    }
}
