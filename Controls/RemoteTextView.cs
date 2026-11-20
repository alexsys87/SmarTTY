using SmarTTY.CodeEditing;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;

namespace SmarTTY.Controls;

/// <summary>
/// The WinForms text view of a <see cref="RemoteFileEditor"/>: the <c>CodeEditorPP</c> the editor was until stage 3E-4, with what the editor
/// overrode of it. The modified state, copy, cut, paste, delete and Select all go to the editor, which sends them to the hex view while that is
/// shown; the comment style is the editor's; a change of the colours or the font goes to the hex control it hosts and to the editor.
/// </summary>
public class RemoteTextView : CodeEditorPP, IRemoteTextView
{
    private readonly ControlUiDispatcher _uiDispatcher;

    private readonly ControlUserPrompt _userPrompt;

    public RemoteTextView()
    {
        _uiDispatcher = new ControlUiDispatcher(this);
        _userPrompt = new ControlUserPrompt(this);
    }

    /// <summary>The editor of the file shown here (set by the editor when it takes the view).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public RemoteFileEditor Owner { get; internal set; }

    /// <summary>The editor's own WinForms hex view, which this view hosts (null when the editor was given another one).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal HexEditorControl HexControl { get; set; }

    /// <summary>The unsaved state covers both views: the bytes in hex mode, the text in text mode.</summary>
    public override bool IsModified
    {
        get
        {
            return Owner != null ? Owner.IsModified : base.IsModified;
        }
        set
        {
            if (Owner != null)
            {
                Owner.IsModified = value;
            }
            else
            {
                base.IsModified = value;
            }
        }
    }

    /// <summary>Comments follow the syntax of the file, also when the highlighting is switched off.</summary>
    public override CommentStyle CommentStyle => Owner?.CommentStyle;

    public override void CopySelection(bool select = true)
    {
        if (Owner != null)
        {
            Owner.CopySelection(select);
        }
        else
        {
            base.CopySelection(select);
        }
    }

    public override void CopyAll()
    {
        if (Owner != null)
        {
            Owner.CopyAll();
        }
        else
        {
            base.CopyAll();
        }
    }

    public override void Cut()
    {
        if (Owner != null)
        {
            Owner.Cut();
        }
        else
        {
            base.Cut();
        }
    }

    public override void Paste()
    {
        if (Owner != null)
        {
            Owner.Paste();
        }
        else
        {
            base.Paste();
        }
    }

    public override void DeleteSelection()
    {
        if (Owner != null)
        {
            Owner.DeleteSelection();
        }
        else
        {
            base.DeleteSelection();
        }
    }

    public override void SelectAllText()
    {
        if (Owner != null)
        {
            Owner.SelectAllText();
        }
        else
        {
            base.SelectAllText();
        }
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        if (HexControl != null)
        {
            HexControl.Font = Font;
        }
        AppearanceChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnBackColorChanged(EventArgs e)
    {
        base.OnBackColorChanged(e);
        if (HexControl != null)
        {
            HexControl.BackColor = BackColor;
        }
        AppearanceChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnForeColorChanged(EventArgs e)
    {
        base.OnForeColorChanged(e);
        if (HexControl != null)
        {
            HexControl.ForeColor = ForeColor;
        }
        AppearanceChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The colours, the font or the zoom of the text changed.</summary>
    public event EventHandler AppearanceChanged;

    EditableTextDocument IRemoteTextView.Document => EditableDocument;

    bool IRemoteTextView.IsTextModified
    {
        get
        {
            return base.IsModified;
        }
        set
        {
            base.IsModified = value;
        }
    }

    void IRemoteTextView.RaiseModifiedChanged()
    {
        OnModifiedChanged();
    }

    void IRemoteTextView.SetCaret(TextDocument.TextPosition position)
    {
        SetCaret(position);
    }

    void IRemoteTextView.FocusText()
    {
        Focus();
    }

    void IRemoteTextView.ApplyMessageColors(RgbColor background, RgbColor error, RgbColor warning, RgbColor border)
    {
        ApplyMessageColors(background.ToColor(), error.ToColor(), warning.ToColor(), border.ToColor());
    }

    void IRemoteTextView.CopySelection(bool select)
    {
        base.CopySelection(select);
    }

    void IRemoteTextView.CopyAll()
    {
        base.CopyAll();
    }

    void IRemoteTextView.Cut()
    {
        base.Cut();
    }

    void IRemoteTextView.Paste()
    {
        base.Paste();
    }

    void IRemoteTextView.DeleteSelection()
    {
        base.DeleteSelection();
    }

    void IRemoteTextView.SelectAllText()
    {
        base.SelectAllText();
    }

    void IRemoteTextView.GetAppearance(out RgbColor background, out RgbColor foreground, out string fontFamily, out float fontSizeInPoints)
    {
        System.Drawing.Font font = LogicalFont ?? Font;
        background = BackColor.ToRgb();
        foreground = ForeColor.ToRgb();
        fontFamily = font.FontFamily.Name;
        fontSizeInPoints = font.SizeInPoints * FontZoom;
    }

    bool IRemoteTextView.IsClosed => IsDisposed;

    event EventHandler IRemoteTextView.Closed
    {
        add
        {
            Disposed += value;
        }
        remove
        {
            Disposed -= value;
        }
    }

    void IRemoteTextView.Close()
    {
        Dispose();
    }

    bool IRemoteTextView.Post(Action action)
    {
        try
        {
            BeginInvoke((ThreadStart)(() => action()));
            return true;
        }
        catch
        {
            // The editor was closed meanwhile.
            return false;
        }
    }

    IUiDispatcher IRemoteTextView.UiDispatcher => _uiDispatcher;

    IUserPrompt IRemoteTextView.UserPrompt => _userPrompt;

    IWin32Window IRemoteTextView.MessageOwner => this;
}
