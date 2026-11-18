using System;

namespace SmarTTY.Completion;

public class SignatureHintController
{
    // Makes the window of a hint (the view knows where it goes and what it is shown in).
    private readonly Func<IHintView> _createView;

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

    internal SignatureHintController(Func<IHintView> createView)
    {
        _createView = createView;
    }

    public void Show(ISignatureInfo signatureInfo, int activeParamIndex, string description)
    {
        this.Hide();
        this._view = _createView();
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
        this._view.ShowAt(_isVertical);
    }

    public void Reposition()
    {
        if (IsVisible)
        {
            _view.Reposition(_isVertical);
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
