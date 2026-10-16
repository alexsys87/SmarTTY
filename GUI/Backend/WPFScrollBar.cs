using SmarTTY.Controls;
using SmarTTY.TextEditing;
using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Threading;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;

namespace SmarTTY.GUI.Backend;

internal class WPFScrollBar : ScrollBar, IScrollBar, IDisposable
{
    public bool Visible
    {
        get
        {
            return (int)((UIElement)this).Visibility == 0;
        }
        set
        {
            ((UIElement)this).Visibility = ((!value) ? Visibility.Collapsed : Visibility.Visible);
        }
    }

    int IScrollBar.Maximum => (int)((RangeBase)this).Maximum;

    int IScrollBar.Value
    {
        get
        {
            if (((DispatcherObject)this).Dispatcher.Thread != Thread.CurrentThread)
            {
                return (int)((DispatcherObject)this).Dispatcher.Invoke((Delegate)(Func<double>)(() => ((RangeBase)this).Value), new object[0]);
            }
            return (int)((RangeBase)this).Value;
        }
        set
        {
            ((RangeBase)this).Value = value;
        }
    }

    int IScrollBar.Width => 0;

    int IScrollBar.Height => 0;

    public ScrollLimits Limits
    {
        get
        {
            return new ScrollLimits
            {
                Maximum = (int)((RangeBase)this).Maximum,
                LargeChange = (int)((ScrollBar)this).ViewportSize,
                Minimum = (int)((RangeBase)this).Minimum
            };
        }
        set
        {
            ((RangeBase)this).Maximum = value.Maximum;
            ((RangeBase)this).Minimum = value.Minimum;
            if (value.LargeChange > value.Range)
            {
                value.LargeChange = value.Range;
            }
            double largeChange = (((ScrollBar)this).ViewportSize = value.LargeChange);
            ((RangeBase)this).LargeChange = largeChange;
        }
    }

    private event EventHandler _ValueChanged;

    event EventHandler IScrollBar.ValueChanged
    {
        add
        {
            _ValueChanged += value;
        }
        remove
        {
            _ValueChanged -= value;
        }
    }

    public WPFScrollBar()
    {
        ((FrameworkElement)this).SetResourceReference(FrameworkElement.StyleProperty, (object)"MainWindowScrollBar");
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        try
        {
            ((RangeBase)this).Value = ((RangeBase)this).Value - (double)(e.Delta / 40);
            ((RoutedEventArgs)e).Handled = true;
        }
        catch
        {
        }
        base.OnMouseWheel(e);
    }

    protected override void OnValueChanged(double oldValue, double newValue)
    {
        base.OnValueChanged(oldValue, newValue);
        _ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
    }

    public void Register(ReadonlyTextBrowser readonlyTextBrowser, DockStyle right)
    {
    }
}
