using SmarTTY.Completion;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;

namespace SmarTTY.GUI.TextView;

/// <summary>
/// The parameter hint of a call on WPF, the view of <see cref="SignatureHintController"/> as <c>Controls/TextHintControl</c> is for the
/// WinForms editor: the parameters with the active one in bold (one under the other for a vertical signature) and a description under them,
/// in a popup under the caret (above it when the controller asks or there is no room below). It never takes the keyboard.
/// </summary>
internal sealed class WpfHintView : IHintView
{
    private readonly IWpfSuggestionHost _host;

    private readonly Popup _popup;

    private readonly TextBlock _signature;

    private readonly TextBlock _description;

    private ISignatureInfo _signatureInfo;

    private int _activeIndex;

    private string _text = "";

    private bool _showDescription;

    public WpfHintView(IWpfSuggestionHost host)
    {
        _host = host;
        _signature = new TextBlock { FontFamily = host.ItemFontFamily, FontSize = host.ItemFontSize };
        _description = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 500, Margin = new Thickness(0, 3, 0, 0) };
        _signature.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Text");
        _description.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextSecondary");
        StackPanel panel = new StackPanel { Margin = new Thickness(5, 2, 5, 2) };
        panel.Children.Add(_signature);
        panel.Children.Add(_description);
        Border border = new Border { BorderThickness = new Thickness(1), Child = panel };
        border.SetResourceReference(Border.BackgroundProperty, "Theme.PopupBackground");
        border.SetResourceReference(Border.BorderBrushProperty, "Theme.BorderStrong");
        _popup = new Popup
        {
            Child = border,
            PlacementTarget = host.PlacementTarget,
            StaysOpen = true,
            Focusable = false
        };
    }

    public string SignatureText
    {
        get
        {
            if (_signatureInfo == null)
            {
                return _text;
            }
            string text = "";
            for (int i = 0; i < _signatureInfo.ParameterCount; i++)
            {
                if (text != "")
                {
                    text += _signatureInfo.Separator;
                }
                text += _signatureInfo.GetParameter(i).Text;
            }
            return text;
        }
        set
        {
            _text = value ?? "";
            _signatureInfo = null;
            Render();
        }
    }

    public string Description
    {
        get
        {
            return _description.Text;
        }
        set
        {
            _description.Text = value ?? "";
        }
    }

    public bool ShowDescription
    {
        get
        {
            return _showDescription;
        }
        set
        {
            _showDescription = value;
            _description.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    public bool Visible => _popup.IsOpen;

    public void SetActiveParameter(int index)
    {
        if (_activeIndex != index)
        {
            _activeIndex = index;
            Render();
        }
    }

    public void SetSignature(ISignatureInfo signatureInfo)
    {
        _signatureInfo = signatureInfo;
        Render();
    }

    public void Dismiss()
    {
        _popup.IsOpen = false;
    }

    public void ShowAt(bool isVertical)
    {
        Reposition(isVertical);
        _description.Visibility = _showDescription ? Visibility.Visible : Visibility.Collapsed;
        _popup.IsOpen = true;
    }

    public void Reposition(bool isVertical)
    {
        // As TextHintControl: above the caret when asked (a vertical signature), else under it; the popup goes to the other side when there is no room.
        Rect caret = _host.CaretBounds;
        _popup.Placement = isVertical ? PlacementMode.Top : PlacementMode.Bottom;
        _popup.PlacementRectangle = new Rect(caret.Left, caret.Top - (isVertical ? 5 : 0), Math.Max(1, caret.Width), caret.Height + 5);
        if (_popup.IsOpen)
        {
            // A popup that is open takes a new place only when it is asked to measure again.
            _popup.HorizontalOffset += 0.01;
            _popup.HorizontalOffset -= 0.01;
        }
    }

    private void Render()
    {
        _signature.Inlines.Clear();
        if (_signatureInfo == null)
        {
            _signature.Inlines.Add(new Run(_text));
            return;
        }
        string separator = _signatureInfo.Separator;
        for (int i = 0; i < _signatureInfo.ParameterCount; i++)
        {
            string text = _signatureInfo.GetParameter(i).Text + (i == _signatureInfo.ParameterCount - 1 ? "" : separator);
            Run run = new Run(text);
            if (i == _activeIndex)
            {
                run.FontWeight = FontWeights.Bold;
            }
            _signature.Inlines.Add(run);
            if (_signatureInfo.IsVertical && i < _signatureInfo.ParameterCount - 1)
            {
                _signature.Inlines.Add(new LineBreak());
            }
        }
    }
}
