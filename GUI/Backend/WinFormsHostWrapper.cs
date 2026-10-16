using System.Windows;
using System.Windows.Forms.Integration;

namespace SmarTTY.GUI.Backend;

internal class WinFormsHostWrapper : WindowsFormsHost
{
    protected override void OnIsKeyboardFocusedChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnIsKeyboardFocusedChanged(e);
        object newValue = e.NewValue;
        bool flag = false;
        int num;
        if (newValue is bool)
        {
            flag = (bool)newValue;
            num = 1;
        }
        else
        {
            num = 0;
        }
        if (((uint)num & (flag ? 1u : 0u)) != 0 && ((WindowsFormsHost)this).Child != null)
        {
            ((WindowsFormsHost)this).Child.Focus();
        }
    }
}
