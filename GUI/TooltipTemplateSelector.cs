using System.Windows;
using System.Windows.Controls;
using VisualTTY;

namespace SmarTTY.GUI;

public class TooltipTemplateSelector : DataTemplateSelector
{
    public DataTemplate FullPathInfo { get; set; }

    public DataTemplate OptionInfo { get; set; }

    public DataTemplate FileInfo { get; set; }

    public override DataTemplate SelectTemplate(object item, DependencyObject container)
    {
        if (!(item is VirtualTerminalPopupSummary.PathInfo))
        {
            if (!(item is VirtualTerminalPopupSummary.OptionInfo))
            {
                if (item is VirtualTerminalPopupSummary.FileOrDirectoryInfo)
                {
                    return FileInfo;
                }
                return ((DataTemplateSelector)this).SelectTemplate(item, container);
            }
            return OptionInfo;
        }
        return FullPathInfo;
    }
}
