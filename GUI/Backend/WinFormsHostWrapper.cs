using System.Windows;
using System.Windows.Forms.Integration;

namespace SmarTTY.GUI.Backend;

internal class WinFormsHostWrapper : WindowsFormsHost
{
    protected override void OnIsKeyboardFocusedChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnIsKeyboardFocusedChanged(e);
        object newValue = e.NewValue;
        bool isKeyboardFocused = false;
        int value;
        if (newValue is bool)
        {
            isKeyboardFocused = (bool)newValue;
            value = 1;
        }
        else
        {
            value = 0;
        }
        if (((uint)value & (isKeyboardFocused ? 1u : 0u)) != 0 && ((WindowsFormsHost)this).Child != null)
        {
            ((WindowsFormsHost)this).Child.Focus();
        }
    }
}
