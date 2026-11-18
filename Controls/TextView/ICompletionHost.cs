using System.Drawing;
using System.Windows.Forms;

namespace SmarTTY.Completion;

public interface ICompletionHost
{
    Control HostControl { get; }

    string CurrentText { get; }

    Rectangle CaretScreenBounds { get; }

    void AcceptSuggestion(ISuggestion suggestion, Keys keys);

    void ForwardMessage(Message message);

    void SetPopupActive(bool changed);
}
