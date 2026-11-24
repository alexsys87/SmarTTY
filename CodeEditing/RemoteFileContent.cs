using SmarTTY.TextEditing;
using System;

namespace SmarTTY.CodeEditing;

/// <summary>
/// The content of a remote file in the two views of its editor: the bytes that are on the server and the encoding they are read with, the
/// encoding and the line breaks the file is saved with, and the switching between the text and the hex view (the bytes the hex view shows,
/// which view can be edited, the text kept while the bytes are shown, the question about bytes the text cannot hold, the modified state of
/// both). It is what <c>RemoteFileEditor</c> had in its fields and in <c>GetBytesForHexView</c>, <c>EnterHexMode</c>, <c>ShowHexView</c>,
/// <c>ExitHexMode</c>, <c>ResetHexMode</c>, <c>ApplyContent</c>, <c>SetSaveEncoding</c>, <c>SetLineEnding</c>, <c>IsModified</c>,
/// <c>GetChanges</c> and the part of <c>CompleteSave</c> about the content, moved here as it was. The editor is reached through
/// <see cref="IRemoteFileContentHost"/>, the hex view through <see cref="IRemoteHexView"/>; the events are raised where the editor raised its own.
/// </summary>
public sealed class RemoteFileContent
{
    private readonly string _remotePath;

    private readonly IRemoteFileContentHost _host;

    private readonly IRemoteHexView _hexView;

    // The state of the saves: the bytes kept from the hex view and the text they were shown as.
    private readonly RemoteFileSession _session;

    // The hex view: its bytes, its undo history and the bytes it saved. The text document is kept apart, so that the
    // two views can be switched back and forth without saving one into the other (see ExitHexMode).
    private HexDocument _hexDocument;

    private bool _hexMode;

    // The text, its caret and its saved state at the moment the hex view was opened.
    private string _textBeforeHex;

    private bool _textModifiedBeforeHex;

    private TextDocument.TextPosition _textCaretBeforeHex;

    /// <param name="remotePath">The path of the file on the server (the titles of <see cref="GetChanges"/>).</param>
    public RemoteFileContent(string remotePath, IRemoteFileContentHost host, IRemoteHexView hexView, RemoteFileSession session)
    {
        _remotePath = remotePath;
        _host = host;
        _hexView = hexView;
        _session = session;
    }

    /// <summary>
    /// The bytes of the file as they are on the server (what was downloaded, then what was saved): "Reopen with encoding" decodes them again.
    /// Null for a new file and before the file is loaded.
    /// </summary>
    public byte[] RawBytes { get; private set; }

    /// <summary>The encoding <see cref="RawBytes"/> are read with.</summary>
    public TextFileEncoding RawEncoding { get; private set; } = TextFileEncodings.Utf8;

    /// <summary>The encoding the file is read in and saved in.</summary>
    public TextFileEncoding FileEncoding { get; private set; } = TextFileEncodings.Utf8;

    /// <summary>The line breaks of the file as it was loaded, or as the user converted them.</summary>
    public LineEndingKind LineEndings { get; private set; } = LineEndingKind.None;

    /// <summary>The bytes are shown instead of the text.</summary>
    public bool HexMode => _hexMode;

    /// <summary>The document of the hex view (also after the view was left, until the content is replaced); null when there was none.</summary>
    public HexDocument HexDocument => _hexDocument;

    /// <summary>Raised when the editor switched between the text and the hex view.</summary>
    public event EventHandler HexModeChanged;

    /// <summary>Raised when the encoding or the line endings changed (or the content was replaced).</summary>
    public event EventHandler FormatChanged;

    /// <summary>
    /// The bytes the editor shows: the server version when the text is unchanged, otherwise the text encoded in the
    /// file encoding, or the exact bytes the hex view left when the text has not been edited since.
    /// </summary>
    private byte[] GetBytesForHexView(string text)
    {
        if (_session.HexBytesForTextSave != null && _session.HexTextSnapshot == text)
        {
            return _session.HexBytesForTextSave;
        }
        if (!_host.IsTextModified && RawBytes != null)
        {
            return RawBytes;
        }
        return TextFileEncodings.Encode(text, FileEncoding);
    }

    /// <summary>Shows the bytes of the text in the hex view.</summary>
    public void EnterHexMode()
    {
        if (_hexMode || !_host.IsLoaded || _host.IsSaving)
        {
            return;
        }
        string text = _host.TextDocument.GetText();
        HexDocument document = _hexDocument;
        bool reuse = document != null && _session.HexTextSnapshot == text && _session.HexBytesForTextSave != null
            && HexDocument.ByteArraysEqual(document.ToArray(), _session.HexBytesForTextSave);
        if (!reuse)
        {
            try
            {
                byte[] serverBytes = RawBytes ?? Array.Empty<byte>();
                HexDocument previous = document;
                document = new HexDocument(GetBytesForHexView(text), serverBytes);
                // The text was edited meanwhile: the coloured blocks of the bytes are kept as far as they still fit.
                document.CopyHighlightsFrom(previous);
            }
            catch (InvalidOperationException exception)
            {
                _host.ShowError(exception.Message);
                return;
            }
        }
        ShowHexView(document);
    }

    /// <summary>
    /// Shows <paramref name="document"/> in the hex view: the text document is kept aside for <see cref="ExitHexMode"/>.
    /// </summary>
    private void ShowHexView(HexDocument document)
    {
        _textBeforeHex = _host.TextDocument.GetText();
        _textModifiedBeforeHex = _host.IsTextModified;
        _textCaretBeforeHex = _host.Caret;
        AttachHexDocument(document);
        _hexMode = true;
        _host.TextReadOnly = true;
        if (!ReferenceEquals(_hexView.Document, document))
        {
            _hexView.Document = document;
        }
        _hexView.ReadOnly = _host.IsReadOnlyMode;
        _host.ShowHexView();
        _hexView.Focus();
        _host.RaiseModifiedChanged();
        HexModeChanged?.Invoke(this, EventArgs.Empty);
        FormatChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows the bytes of a restored draft in the hex view.</summary>
    public void ShowRestoredHexDraft(byte[] bytes)
    {
        ShowHexView(new HexDocument(bytes, RawBytes ?? Array.Empty<byte>()));
    }

    /// <summary>Shows the text again: the text from before the hex view when the bytes are those of the file, else the bytes decoded.</summary>
    public void ExitHexMode()
    {
        if (!_hexMode || _hexDocument == null)
        {
            return;
        }
        byte[] bytes = _hexDocument.ToArray();
        bool keepText = !_textModifiedBeforeHex && _textBeforeHex != null
            && HexDocument.ByteArraysEqual(bytes, RawBytes ?? Array.Empty<byte>());
        string text;
        if (keepText)
        {
            // Nothing changed: show the text exactly as it was, even when the encoding does not round-trip.
            text = _textBeforeHex;
        }
        else
        {
            text = TextFileEncodings.Decode(bytes, FileEncoding);
            byte[] roundTrip = null;
            try
            {
                roundTrip = TextFileEncodings.Encode(text, FileEncoding);
            }
            catch
            {
                // A character the encoding cannot store is just as lossy.
            }
            if (!HexDocument.ByteArraysEqual(roundTrip, bytes)
                && !_host.ConfirmWarning("Not all bytes can be shown as text in " + FileEncoding.DisplayName
                    + ": saving the text after it is edited would change them.\n\nShow the bytes as text anyway?"))
            {
                _hexView.Focus();
                return;
            }
        }
        _session.HexBytesForTextSave = bytes;
        _session.HexTextSnapshot = text;
        _hexMode = false;
        _host.HideHexView();
        _hexView.ReadOnly = true;
        _host.TextReadOnly = _host.IsReadOnlyMode;

        // When the bytes are still the text that was shown before, the text document and its undo history are kept:
        // switching to the hex view and back must not lose them.
        if (text == _textBeforeHex && _host.TextDocument.GetText() == text)
        {
            _host.IsTextModified = !HexDocument.ByteArraysEqual(bytes, RawBytes ?? Array.Empty<byte>());
            _host.FocusText();
            _host.RaiseModifiedChanged();
            HexModeChanged?.Invoke(this, EventArgs.Empty);
            FormatChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        LineEndingKind lineEndings = LineEndingDetector.Detect(text);
        LineEndings = lineEndings;
        EditableTextDocument document = new EditableTextDocument
        {
            DefaultLineEnding = (lineEndings == LineEndingKind.CrLf) ? TextDocument.LineEnding.CrLf : ((lineEndings == LineEndingKind.Cr) ? TextDocument.LineEnding.Cr : TextDocument.LineEnding.Lf)
        };
        document.SetText(text);
        _host.SetDocument(document);
        int line = Math.Max(0, Math.Min(_textCaretBeforeHex.Line, Math.Max(0, document.LineCount - 1)));
        _host.SetCaret(new TextDocument.TextPosition(Math.Min(_textCaretBeforeHex.Column, document.GetLineText(line).Length), line));
        _host.IsTextModified = !HexDocument.ByteArraysEqual(bytes, RawBytes ?? Array.Empty<byte>());
        _host.FocusText();
        _host.RaiseModifiedChanged();
        HexModeChanged?.Invoke(this, EventArgs.Empty);
        FormatChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AttachHexDocument(HexDocument document)
    {
        if (_hexDocument != null)
        {
            _hexDocument.ModifiedChanged -= OnHexDocumentModifiedChanged;
        }
        _hexDocument = document;
        if (_hexDocument != null)
        {
            _hexDocument.ModifiedChanged += OnHexDocumentModifiedChanged;
        }
    }

    private void OnHexDocumentModifiedChanged(object sender, EventArgs e)
    {
        if (_hexMode && ReferenceEquals(sender, _hexDocument))
        {
            _host.RaiseModifiedChanged();
        }
    }

    /// <summary>Hides and forgets the hex view (the file is being loaded again, or its text was replaced).</summary>
    public void ResetHexMode()
    {
        bool hadHex = _hexMode || _hexDocument != null;
        AttachHexDocument(null);
        _hexMode = false;
        _session.HexBytesForTextSave = null;
        _session.HexTextSnapshot = null;
        _textBeforeHex = null;
        _textModifiedBeforeHex = false;
        _host.HideHexView();
        _hexView.ReadOnly = true;
        _hexView.Document = null;
        if (hadHex)
        {
            HexModeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The unsaved state covers both views: the bytes in hex mode, the text in text mode.</summary>
    public bool IsModified
    {
        get
        {
            return (_hexMode && _hexDocument != null) ? _hexDocument.IsModified : _host.IsTextModified;
        }
        set
        {
            if (_hexMode && _hexDocument != null)
            {
                if (value)
                {
                    _hexDocument.MarkModified();
                }
                else
                {
                    _hexDocument.MarkSaved();
                }
            }
            else
            {
                _host.IsTextModified = value;
            }
        }
    }

    /// <summary>
    /// Puts <paramref name="text"/> (the file <paramref name="file"/>, read as <paramref name="encoding"/>) in the editor
    /// as unmodified.
    /// </summary>
    public void ApplyContent(byte[] file, TextFileEncoding encoding, string text)
    {
        ResetHexMode();
        RawBytes = file;
        RawEncoding = encoding;
        FileEncoding = encoding;
        LineEndings = LineEndingDetector.Detect(text);
        EditableTextDocument document = new EditableTextDocument
        {
            DefaultLineEnding = (LineEndings == LineEndingKind.CrLf) ? TextDocument.LineEnding.CrLf : ((LineEndings == LineEndingKind.Cr) ? TextDocument.LineEnding.Cr : TextDocument.LineEnding.Lf)
        };
        document.SetText(text);
        _host.SetDocument(document);
        _host.IsTextModified = false;
        _session.ConvertCrLf = null;
        _session.BomAnswered = false;
        FormatChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Saves the file in another encoding (the text stays as it is). The file counts as changed: it has to be saved.
    /// </summary>
    public void SetSaveEncoding(TextFileEncoding encoding)
    {
        if (encoding == null || encoding == FileEncoding || _host.TextReadOnly || _hexMode)
        {
            return;
        }
        FileEncoding = encoding;
        // The bytes kept from the hex view are in the old encoding: save the text in the chosen one now.
        _session.HexBytesForTextSave = null;
        _session.HexTextSnapshot = null;
        _host.IsTextModified = true;
        FormatChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Converts every line break of the file to <paramref name="lineEnding"/>, and gives the lines typed later the same
    /// one. The file counts as changed: it has to be saved.
    /// </summary>
    public void SetLineEnding(TextDocument.LineEnding lineEnding)
    {
        if (_host.TextReadOnly || _hexMode)
        {
            return;
        }
        _host.TextDocument.SetLineEnding(lineEnding);
        _host.TextDocument.DefaultLineEnding = lineEnding;
        LineEndings = (lineEnding == TextDocument.LineEnding.CrLf) ? LineEndingKind.CrLf : ((lineEnding == TextDocument.LineEnding.Cr) ? LineEndingKind.Cr : LineEndingKind.Lf);
        // The text no longer matches the bytes kept from the hex view.
        _session.HexBytesForTextSave = null;
        _session.HexTextSnapshot = null;
        _host.IsTextModified = true;
        FormatChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The differences between the file on the server (as it was opened, or last saved) and the text in the editor,
    /// for the Show Changes window.
    /// </summary>
    public TextDiff.Result GetChanges()
    {
        string original = RawBytes == null ? "" : TextFileEncodings.Decode(RawBytes, RawEncoding);
        return TextDiff.Compare(original, _host.TextDocument.GetText(), _remotePath + " (on the server)", _remotePath + " (in the editor)");
    }

    /// <summary>
    /// The bytes of <paramref name="job"/> are on the server now: they are the bytes of the file, and the view they were taken from (and the
    /// other one, when it holds the same bytes) is saved.
    /// </summary>
    public void CompleteSave(RemoteSaveJob job)
    {
        RawBytes = job.Bytes;
        RawEncoding = job.Encoding ?? FileEncoding;
        if (job.HexMode && _hexDocument != null)
        {
            _hexDocument.MarkSaved(job.Version, job.Bytes);
            _hexView.Invalidate();
            // The text is rebuilt from these bytes when the hex view is closed.
            _session.HexBytesForTextSave = null;
            _session.HexTextSnapshot = null;
        }
        else
        {
            _host.MarkTextSaved(job.Version);
            if (_hexDocument != null && HexDocument.ByteArraysEqual(_hexDocument.ToArray(), job.Bytes))
            {
                // The text was saved back as the exact bytes of the hex view: both views are saved now.
                _hexDocument.MarkSaved(_hexDocument.CurrentVersion, job.Bytes);
            }
        }
        FormatChanged?.Invoke(this, EventArgs.Empty);
    }
}
