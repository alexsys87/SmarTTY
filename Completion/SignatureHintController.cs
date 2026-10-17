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

    public void Show(ISignatureInfo signatureInfo, int activeParamIndex, string description, Control parentControl)
    {
        this.Hide();
        if (parentControl == null)
        {
            this._view = new TextHintForm();
        }
        else
        {
            TextHintControl textHintControl = (TextHintControl)(this._view = new TextHintControl());
            textHintControl.Visible = false;
            textHintControl.Parent = parentControl;
        }
        string fullSignatureText = "";
        string separator = signatureInfo.Separator;
        for (int i = 0; i < signatureInfo.ParameterCount; i++)
        {
            if (fullSignatureText != "")
            {
                fullSignatureText += separator;
            }
            fullSignatureText += signatureInfo.GetParameter(i).Text;
        }
        if (description == null)
        {
            this._view.ShowDescription = false;
        }
        else
        {
            this._view.ShowDescription = true;
            this._view.Description = description;
        }
        this._view.SetSignature(signatureInfo);
        this._view.SetActiveParameter(activeParamIndex);
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

    public void SetActiveParameter(int activeParamIndex)
    {
        if (this._view != null)
        {
            this._view.SetActiveParameter(activeParamIndex);
        }
    }
}
