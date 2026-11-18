using SmarTTY.Completion;
using SmarTTY.Forms;
using System.Windows.Forms;

namespace SmarTTY.Controls;

/// <summary>Makes the window of a signature hint of a code editor.</summary>
internal static class HintViewFactory
{
    /// <summary>
    /// A hint window of its own, or, when <paramref name="parentControl"/> is not null, a hint control that is a child of it (where a
    /// window cannot be shown above the editor). Both position themselves under the caret of the host.
    /// </summary>
    public static IHintView Create(ICompletionHost host, Control parentControl)
    {
        if (parentControl == null)
        {
            TextHintForm textHintForm = new TextHintForm();
            textHintForm.Host = host;
            return textHintForm;
        }
        TextHintControl textHintControl = new TextHintControl();
        textHintControl.Host = host;
        textHintControl.Visible = false;
        textHintControl.Parent = parentControl;
        return textHintControl;
    }
}
