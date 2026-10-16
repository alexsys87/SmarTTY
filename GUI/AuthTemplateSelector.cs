using System.Windows;
using System.Windows.Controls;
using VisualTTY;

namespace SmarTTY.GUI;

public class AuthTemplateSelector : DataTemplateSelector
{
    public DataTemplate Password { get; set; }

    public DataTemplate PublicKey { get; set; }

    public DataTemplate OpenSSHKey { get; set; }

    public override DataTemplate SelectTemplate(object item, DependencyObject container)
    {
        if (item is AuthType)
        {
            switch ((AuthType)item)
            {
                case AuthType.OpenSSHKey:
                    return OpenSSHKey;
                case AuthType.PublicKey:
                    return PublicKey;
                case AuthType.Password:
                    return Password;
            }
        }
        return ((DataTemplateSelector)this).SelectTemplate(item, container);
    }
}
