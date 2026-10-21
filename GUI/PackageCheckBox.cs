using System.Windows.Controls;
using VisualTTY;

namespace SmarTTY.GUI;

/// <summary>
/// The check box of a package row in the package manager. WPF cycles a three-state check box as
/// unchecked -> checked -> indeterminate -> unchecked, so the first click on an upgradeable package (indeterminate,
/// i.e. "keep as is") would mark it for removal. The click goes through <see cref="LinuxPackageInfo.ToggleSelection"/>
/// instead, which cycles "keep" -> "upgrade" -> "remove" -> "keep", like the Space key in the list does.
/// </summary>
public class PackageCheckBox : CheckBox
{
    protected override void OnToggle()
    {
        if (DataContext is LinuxPackageInfo package)
        {
            // The binding brings the new value back to the check box, which raises Checked/Unchecked/Indeterminate.
            package.ToggleSelection();
        }
        else
        {
            base.OnToggle();
        }
    }
}
