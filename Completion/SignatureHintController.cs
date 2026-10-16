using SmarTTY.Controls;
using SmarTTY.Forms;
using System.Windows.Forms;

namespace SmarTTY.Completion;

public class SignatureHintController
{
    private ICompletionHost _host;

    private IHintView _view;

    private bool _isVertical;

    public bool IsVisible
    {
        get
        {
            if (_view != null)
            {
                return _view.Visible;
            }
            return false;
        }
    }

    public SignatureHintController(ICompletionHost completionHost)
    {
        _host = completionHost;
    }

    public void Show(ISignatureInfo signatureInfo, int value2, string text3, Control control)
    {
        this.Hide();
        if (control == null)
        {
            this._view = new TextHintForm();
        }
        else
        {
            TextHintControl textHintControl = (TextHintControl)(this._view = new TextHintControl());
            textHintControl.Visible = false;
            textHintControl.Parent = control;
        }
        string text = "";
        string text2 = signatureInfo.Separator;
        for (int num = 0; num < signatureInfo.ParameterCount; num++)
        {
            if (text != "")
            {
                text += text2;
            }
            text += signatureInfo.GetParameter(num).Text;
        }
        if (text3 == null)
        {
            this._view.ShowDescription = false;
        }
        else
        {
            this._view.ShowDescription = true;
            this._view.Description = text3;
        }
        this._view.SetSignature(signatureInfo);
        this._view.SetActiveParameter(value2);
        _isVertical = signatureInfo.IsVertical;
        this._view.ShowAt(_host, _isVertical);
    }

    public void Reposition()
    {
        if (IsVisible)
        {
            _view.Reposition(_host, _isVertical);
        }
    }

    public void Hide()
    {
        if (_view != null)
        {
            _view.Dismiss();
            _view = null;
        }
    }

    public void SetActiveParameter(int value2)
    {
        if (this._view != null)
        {
            this._view.SetActiveParameter(value2);
        }
    }
}
