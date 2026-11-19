using SmarTTY.CodeEditing;
using SmarTTY.Controls;
using SmarTTY.IO;
using SmarTTY.Native;
using SmarTTY.Terminal;
using SmarTTY.Terminal.Emulation;
using SmarTTY.Terminal.Input;
using SmarTTY.Terminal.Rendering;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Media;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using VisualTTY;

namespace SmarTTY.GUI.TerminalView;

/// <summary>
/// The classic terminal of a tab on the WPF view (stage 2B): what <see cref="TerminalEmulatorControl"/> did for a classic SSH tab, without
/// WinForms. The screen and the escape sequences are <see cref="TerminalDisplayModel"/>, the drawing and the keyboard
/// <see cref="TerminalRenderView"/>; this class adds what a tab needs around them: the output queue of the channel, the paste with its
/// guard, its progress window and its menu, the links, the log file, the bell, the settings, the search of the window, and the events the
/// window and the session listen to. It is the terminal of a tab through <see cref="ITerminalSurface"/>.
/// </summary>
public class WpfTerminal : Border, ITerminalSurface, ISearchTarget
{
    // From this size a paste is sent in pieces, one after another, so that the window does not wait for the whole text.
    private const int AsyncPasteThreshold = 16 * 1024;

    private const int AsyncPasteChunkSize = 4096;

    private static XtermKeyTable _keyTable;

    private readonly TerminalPanel _panel;

    private readonly TerminalRenderView _view;

    private readonly TerminalDisplayModel _model = new TerminalDisplayModel(80, 24);

    private readonly Anchor _anchor;

    private readonly object _logLock = new object();

    private FileStream _logStream;

    private PasteJob _pasteJob;

    private PasteMode _pasteMode;

    private volatile bool _disposed;

    private string _linkExtensionsText;

    private HashSet<string> _linkExtensions;

    public WpfTerminal()
    {
        _panel = new TerminalPanel(KeyTable);
        _view = _panel.View;
        _anchor = new Anchor(this);
        Child = _panel;
        AllowDrop = true;
        Focusable = false;
        _view.Attach(_model);
        _model.LinkHost = new LinkHost(this);
        _model.TitleChanged += () => TitleChanged?.Invoke(this, EventArgs.Empty);
        _model.OutputReceived += output => OutputReceived?.Invoke(output);
        _model.Bell += OnBell;
        _view.DataEntered += OnViewData;
        _view.TerminalSizeChanged += (columns, rows) =>
        {
            OnGridResized(columns, rows);
            PostResize(columns, rows);
        };
        _view.PasteRequested = () =>
        {
            Paste();
            return true;
        };
        _view.PasteMenuRequested += () => ShowPasteMenu();
        _view.ContextMenuRequested += OnContextMenuRequested;
        _view.CtrlTabPressed += shift => CtrlTabPressed?.Invoke(this, shift);
        _view.PreviewTerminalKey += OnPreviewTerminalKey;
        _view.PreviewTerminalText = OnTerminalText;
        _view.DocumentClicked += position => OnDocumentClicked(position);
    }

    // The key layout of the application (VisualTTY/XTermKeys.xml), read once.
    private static XtermKeyTable KeyTable
    {
        get
        {
            if (_keyTable == null)
            {
                using (Stream layout = Assembly.GetExecutingAssembly().GetManifestResourceStream("VisualTTY.XTermKeys.xml"))
                {
                    _keyTable = new XtermKeyTable(layout);
                }
            }
            return _keyTable;
        }
    }

    internal TerminalRenderView View => _view;

    internal TerminalDisplayModel Model => _model;

    // Events of a tab.

    /// <summary>Bytes for the remote side (through <see cref="Dispatcher"/> when there is one, as the WinForms terminal did).</summary>
    public event Action<WpfTerminal, byte[], TerminalInputKind> DataEntered;

    /// <summary>The number of columns or rows changed.</summary>
    public event Action<WpfTerminal, int, int> TerminalSizeChanged;

    public event EventHandler TitleChanged;

    public event Action<string> OutputReceived;

    /// <summary>A link to a file was clicked; the path is absolute when the directory of the shell is known.</summary>
    public event EventHandler<OpenFileEventArgs> FileLinkClicked;

    /// <summary>The user chose to send a very large paste as a file instead of typing it.</summary>
    public event Action<WpfTerminal, string> LargePasteUploadRequested;

    /// <summary>Paste found files (copied in Explorer) and no text in the clipboard.</summary>
    public event Action<WpfTerminal, string[]> ClipboardFilesPasted;

    /// <summary>Ctrl+Tab (true with Shift).</summary>
    public event Action<WpfTerminal, bool> CtrlTabPressed;

    /// <summary>The context menu of the terminal is asked for at a point of the screen (device pixels).</summary>
    public event Action<WpfTerminal, System.Drawing.Point> ContextMenuRequested;

    /// <summary>A key before the terminal looks at it: a handler that marks it handled takes it (the shortcuts of the window).</summary>
    public event KeyEventHandler ShortcutKey;

    // Settings.

    /// <summary>The settings of the application (paste, links, keys); the terminal reads them when it needs them.</summary>
    public GlobalSettings AppSettings
    {
        get => _appSettings;
        set
        {
            _appSettings = value;
            ApplyKeySettings();
        }
    }

    private GlobalSettings _appSettings;

    /// <summary>Serializes the input of all tabs and the beeps (the worker of the window), as for the WinForms terminal.</summary>
    public TerminalEventDispatcher InputDispatcher { get; set; }

    public TerminalEmulatorControl.BeepMode BellMode { get; set; } = TerminalEmulatorControl.BeepMode.SystemBeep;

    public string BellSoundFile { get; set; }

    /// <summary>What a line break of pasted text is sent as while the program reads the keyboard.</summary>
    public string PasteLineBreak
    {
        get => _view.PasteLineBreak;
        set => _view.PasteLineBreak = value;
    }

    /// <summary>The encoding of typed and pasted text.</summary>
    public Encoding InputEncoding
    {
        get => _view.InputEncoding;
        set => _view.InputEncoding = value;
    }

    /// <summary>What Enter sends while the program reads the keyboard; null: a CR (the device terminal sets the line ending of its connection).</summary>
    public string EnterSequence
    {
        get => _enterSequence;
        set
        {
            _enterSequence = value;
            _view.KeyOptions.EnterSequence = value ?? "\r";
        }
    }

    private string _enterSequence;

    /// <summary>Alt+F4 goes to the program instead of closing the window.</summary>
    public bool ForwardAltF4
    {
        get => _view.KeyOptions.ForwardAltF4;
        set => _view.KeyOptions.ForwardAltF4 = value;
    }

    public bool HighlightLogLevels
    {
        get => _model.HighlightLogLevels;
        set => _model.HighlightLogLevels = value;
    }

    public bool FileLinks
    {
        get => _model.FileLinks;
        set => _model.FileLinks = value;
    }

    public bool UrlLinks
    {
        get => _model.UrlLinks;
        set => _model.UrlLinks = value;
    }

    /// <summary>Tells the directory of the shell when it did not report one; called when a link with a relative name is clicked.</summary>
    public Func<string> DirectoryFallback { get; set; }

    /// <summary>The size of the grid stays as it is (the window is being resized).</summary>
    public bool LayoutSuspended
    {
        get => _view.GridSizeFrozen;
        set => _view.GridSizeFrozen = value;
    }

    /// <summary>Shows or hides the scroll bar (the setting of the window, Auto follows the mouse).</summary>
    public bool ScrollBarVisible
    {
        get => _panel.ScrollBarVisibility == Visibility.Visible;
        set => _panel.ScrollBarVisibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The caret is drawn while the terminal does not have the focus.</summary>
    public bool CaretWhenUnfocused
    {
        get => _view.ShowCaretWhenUnfocused;
        set => _view.ShowCaretWhenUnfocused = value;
    }

    /// <summary>Applies the terminal settings of the application (the WinForms terminal got them in <c>ApplyTerminalSettings</c>).</summary>
    public void ApplySettings(GlobalSettings settings, System.Drawing.Font font)
    {
        AppSettings = settings;
        _model.ShowUnrecognizedAsHex = settings.ShowUnrecognizedCharactersAsHex;
        _model.MaxChunkLength = settings.LargeBlockCutoffPoint;
        BellMode = settings.BeepMode;
        BellSoundFile = settings.BeepFileName;
        _view.CopyOnSelect = settings.CopyOnSelection;
        TerminalColorScheme scheme = _view.ColorScheme;
        if (settings.DefaultTextColor.Alpha != 0)
        {
            System.Drawing.Color text = settings.DefaultTextColor;
            scheme.DefaultForeground = RgbColor.FromArgb(255, text.R, text.G, text.B);
        }
        ConsolePalette palette = settings.PaletteOverride ?? ConsolePalette.PuTTY;
        for (int index = 0; index < 8; index++)
        {
            if (index < palette.NormalColors.Length)
            {
                System.Drawing.Color normal = palette.NormalColors[index];
                scheme.SetPaletteColor(index, bright: false, RgbColor.FromArgb(normal.A, normal.R, normal.G, normal.B));
            }
            if (index < palette.BrightColors.Length)
            {
                System.Drawing.Color bright = palette.BrightColors[index];
                scheme.SetPaletteColor(index, bright: true, RgbColor.FromArgb(bright.A, bright.R, bright.G, bright.B));
            }
        }
        if (font != null)
        {
            // The font of the settings, as the WinForms terminal used it: the family, the size in points (to device-independent pixels, 1/96
            // inch; WPF scales them to the DPI of the monitor itself), bold and italic.
            _view.SetFont(new FontFamily(font.FontFamily.Name), Math.Max(4, font.SizeInPoints * 96.0 / 72.0), font.Bold, font.Italic);
        }
        else
        {
            _view.Invalidate();
        }
        _panel.ScrollBarVisibility = settings.VerticalScrollbarMode == GlobalSettings.ScrollbarMode.Hide ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// The colours of text without a colour of its own and of the background (a device terminal has its own; null keeps the one there is).
    /// </summary>
    public void SetDefaultColors(RgbColor? foreground, RgbColor? background)
    {
        TerminalColorScheme scheme = _view.ColorScheme;
        if (foreground.HasValue)
        {
            scheme.DefaultForeground = foreground.Value;
        }
        if (background.HasValue)
        {
            scheme.DefaultBackground = background.Value;
        }
        _view.Invalidate();
    }

    /// <summary>The selection is copied when the mouse button is released.</summary>
    public bool CopyOnSelect
    {
        get => _view.CopyOnSelect;
        set => _view.CopyOnSelect = value;
    }

    private void ApplyKeySettings()
    {
        GlobalSettings settings = _appSettings;
        if (settings == null)
        {
            return;
        }
        _view.KeyOptions.AltSendsEscape = settings.SendAltPlusLetterToSession;
        _view.KeyOptions.ShiftPageScrolls = settings.ShiftPageXXToScroll;
        _view.WheelSendsArrowKeys = settings.EmulateDirectionKeysWithMouseWheel;
        _view.MiddleButtonPastes = PasteGuard.OptionsOf(settings).MiddleClickPastes;
    }

    // ITerminalSurface.

    /// <summary>
    /// What the owner keeps with the terminal (the channel and the session of a tab). Not <see cref="FrameworkElement.Tag"/>: that is a
    /// dependency property, which only the UI thread may read, and the session reads this one on the thread of the channel (is this output
    /// still for this terminal?) and the window on the thread that writes the input. Reading the dependency property there threw, so no
    /// output was shown and no key reached the shell.
    /// </summary>
    public new object Tag
    {
        get => Volatile.Read(ref _tag);
        set => Volatile.Write(ref _tag, value);
    }

    private object _tag;

    public bool IsDisposed => _disposed;

    public string Title => _model.Title;

    public GridPoint GridSize => new GridPoint(_model.Columns, _model.Rows);

    public GridPoint CursorCell => _model.CursorPosition;

    public string GetScreenLineText(int row)
    {
        return _model.GetScreenLineText(row);
    }

    public string ReportedDirectory => _model.ReportedDirectory;

    public bool ByteOrientedLineEditing
    {
        get => _model.ByteOrientedLineEditing;
        set => _model.ByteOrientedLineEditing = value;
    }

    public bool SuppressReplies
    {
        get => _model.SuppressReplies;
        set => _model.SuppressReplies = value;
    }

    public bool HideCaret
    {
        get => _view.CaretHiddenByOwner;
        set => _view.CaretHiddenByOwner = value;
    }

    public bool AutoWrap
    {
        get => _model.AutoWrap;
        set => _model.AutoWrap = value;
    }

    public void EnqueueOutput(string text)
    {
        if (!_disposed && !string.IsNullOrEmpty(text))
        {
            _view.EnqueueOutput(text);
        }
    }

    public void ProcessOutput(string output)
    {
        if (!_disposed)
        {
            _view.ProcessOutputNow(output);
        }
    }

    public void OpenLogFile(string path)
    {
        lock (_logLock)
        {
            _logStream?.Dispose();
            _logStream = null;
            if (path != null)
            {
                _logStream = File.Open(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            }
        }
    }

    public void WriteLog(byte[] buffer, int offset, int count)
    {
        try
        {
            lock (_logLock)
            {
                if (_logStream != null)
                {
                    _logStream.Write(buffer, offset, count);
                    _logStream.Flush();
                }
            }
        }
        catch
        {
            // A log that cannot be written must not break the terminal.
        }
    }

    public void TypeText(string text)
    {
        _model.DropPrediction();
        _view.SendInput(text, TerminalInputKind.Typed);
    }

    public virtual void InsertCommand(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            TypeText(text);
        }
    }

    private PasteSettings PasteOptions => PasteGuard.OptionsOf(_appSettings);

    public void Paste()
    {
        string text = Win32Clipboard.GetText(_anchor.Handle);
        if (string.IsNullOrEmpty(text) && TryPasteClipboardFiles())
        {
            return;
        }
        PasteText(text);
    }

    private bool TryPasteClipboardFiles()
    {
        if (ClipboardFilesPasted == null || !PasteOptions.OfferClipboardFiles || !Win32Clipboard.HasFiles())
        {
            return false;
        }
        string[] files = Win32Clipboard.GetFiles(_anchor.Handle);
        if (files == null || files.Length == 0)
        {
            return false;
        }
        ClipboardFilesPasted(this, files);
        return true;
    }

    public virtual void PasteText(string clipboardText)
    {
        _model.DropPrediction();
        try
        {
            if (string.IsNullOrEmpty(clipboardText))
            {
                return;
            }
            // With bracketed paste the program gets the text as one piece and decides itself what to do with the line breaks, so only
            // without it the lines run at once.
            bool bracketed = _model.BracketedPaste;
            if (_pasteJob != null && _pasteJob.IsActive)
            {
                // The previous paste is still being sent.
                return;
            }
            clipboardText = PasteGuard.ApplyMode(clipboardText, _pasteMode);
            string text = PasteGuard.Prepare(clipboardText, _appSettings, linesRunAtOnce: !bracketed, owner: _anchor);
            if (text != null)
            {
                text = PasteGuard.FitToEncoding(text, InputEncoding, _appSettings, _anchor);
            }
            if (text != null && !OfferUpload(text))
            {
                string lineBreak = PasteGuard.ResolveLineBreak(_appSettings, PasteLineBreak);
                SendPasted(XtermProtocol.PreparePaste(text, lineBreak, bracketed), lineBreak, bracketed);
            }
            _model.ScrollToScreen();
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
    }

    public virtual void PasteSpecial(PasteMode mode)
    {
        _pasteMode = mode;
        try
        {
            Paste();
        }
        finally
        {
            _pasteMode = PasteMode.Normal;
        }
    }

    // A text this large is better uploaded than typed: asks, and returns true when the text must not be pasted.
    private bool OfferUpload(string text)
    {
        int thresholdKB = PasteOptions.UploadOfferThresholdKB;
        if (thresholdKB <= 0 || LargePasteUploadRequested == null || text.Length < thresholdKB * 1024)
        {
            return false;
        }
        switch (PasteGuard.AskUpload(text.Length, _anchor))
        {
            case System.Windows.Forms.DialogResult.Yes:
                LargePasteUploadRequested(this, text);
                return true;
            case System.Windows.Forms.DialogResult.No:
                return false;
            default:
                return true;
        }
    }

    private void SendPasted(string text, string lineBreak, bool bracketed)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        if (text.Length < AsyncPasteThreshold)
        {
            _view.SendInput(text, TerminalInputKind.Paste);
            return;
        }
        _pasteJob = new PasteJob(_anchor, "Pasting", PasteModes.BuildSteps(text, AsyncPasteChunkSize, lineBreak, splitLines: false),
            (string piece, Action written) => SendWithCompletion(piece, written),
            _appSettings?.Paste, slow: false,
            (PasteJob.Outcome outcome, string message) =>
            {
                _pasteJob = null;
                if (outcome == PasteJob.Outcome.Cancelled && bracketed)
                {
                    // The program waits for the end of the paste.
                    _view.SendInput(XtermProtocol.BracketedPasteEnd, TerminalInputKind.Paste);
                }
                else if (outcome == PasteJob.Outcome.Failed)
                {
                    MessageBoxes.Error(message);
                }
            });
        _pasteJob.Start();
    }

    // As a paste, but written() is called (on any thread) when the data has been handed over.
    private bool SendWithCompletion(string text, Action written)
    {
        if (DataEntered == null)
        {
            return false;
        }
        byte[] bytes = InputEncoding.GetBytes(text);
        Post(() =>
        {
            try
            {
                DataEntered?.Invoke(this, bytes, TerminalInputKind.Paste);
            }
            finally
            {
                written?.Invoke();
            }
        });
        return true;
    }

    /// <summary>Shows the menu with the clipboard, the texts pasted lately and the snippets, at the mouse or at the caret.</summary>
    public void ShowPasteMenu(System.Drawing.Point? screenLocation = null)
    {
        System.Drawing.Point where = screenLocation ?? MousePointOnScreen();
        PasteMenu.Show(_anchor, ContextMenuReference, _appSettings, where, Paste, PasteText);
    }

    /// <summary>A WinForms menu whose look the paste menu takes (the context menu of the terminals of the window).</summary>
    public System.Windows.Forms.ContextMenuStrip ContextMenuReference { get; set; }

    private System.Drawing.Point MousePointOnScreen()
    {
        System.Drawing.Point mouse = System.Windows.Forms.Cursor.Position;
        System.Drawing.Rectangle bounds = _anchor.ScreenBounds;
        return bounds.Contains(mouse) ? mouse : new System.Drawing.Point(bounds.Left + 12, bounds.Top + 12);
    }

    private void OnContextMenuRequested(Point screen)
    {
        System.Drawing.Point where = new System.Drawing.Point((int)Math.Round(screen.X), (int)Math.Round(screen.Y));
        // What the right button does when the settings say something else than the menu; Shift+right button still opens it.
        PasteSettings.MouseAction action = PasteOptions.RightClick;
        if (action != PasteSettings.MouseAction.Menu && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            if (action == PasteSettings.MouseAction.CopyOrPaste && _model.HasSelection)
            {
                _view.CopySelection();
                _model.ClearSelection();
            }
            else
            {
                Paste();
            }
            return;
        }
        ContextMenuRequested?.Invoke(this, where);
    }

    // Esc stops a paste that is being sent; Ctrl+Shift+V opens the paste menu when the settings say so; then the window's shortcuts.
    private void OnPreviewTerminalKey(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        ModifierKeys modifiers = e.KeyboardDevice.Modifiers;
        if (key == Key.Escape && modifiers == ModifierKeys.None && CancelActivePaste())
        {
            e.Handled = true;
            return;
        }
        if (key == Key.V && modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && PasteOptions.CtrlShiftV == PasteSettings.ShortcutAction.HistoryMenu)
        {
            ShowPasteMenu();
            e.Handled = true;
            return;
        }
        OnKeyBeforeShortcuts(e);
        if (e.Handled)
        {
            return;
        }
        ShortcutKey?.Invoke(this, e);
        if (!e.Handled)
        {
            OnTerminalKey(e);
        }
    }

    /// <summary>
    /// A key before the window's shortcuts: a subclass whose own keys must win over them takes it here (the input line of the serial
    /// terminal handled Ctrl+C, Ctrl+L, Ctrl+R, Tab, Up, Down, Enter and Esc before the device panel saw the key).
    /// </summary>
    protected virtual void OnKeyBeforeShortcuts(KeyEventArgs e)
    {
    }

    /// <summary>A key that the window's shortcuts left: a subclass that edits a line of its own (the Smart Terminal) takes it here.</summary>
    protected virtual void OnTerminalKey(KeyEventArgs e)
    {
    }

    /// <summary>Text input before the terminal sends it: true when a subclass took it.</summary>
    protected virtual bool OnTerminalText(TextCompositionEventArgs e)
    {
        return false;
    }

    /// <summary>The grid got another size (on the UI thread, before the remote side is told).</summary>
    protected virtual void OnGridResized(int columns, int rows)
    {
    }

    /// <summary>The way the paste that is being done now was asked for (see <see cref="PasteSpecial"/>).</summary>
    protected PasteMode PasteModeInUse => _pasteMode;

    /// <summary>A plain click (no drag) on a place of the document.</summary>
    protected virtual void OnDocumentClicked(TextDocument.TextPosition position)
    {
    }

    /// <summary>The tab is being closed (before the view lets the model go).</summary>
    protected virtual void OnDisposing()
    {
    }

    /// <summary>Stops a paste that is being sent (Esc); false when there is none.</summary>
    protected virtual bool CancelActivePaste()
    {
        if (_pasteJob != null && _pasteJob.IsActive)
        {
            _pasteJob.Cancel();
            return true;
        }
        return false;
    }

    /// <summary>
    /// The terminal edits a line of its own now (the Smart Terminal at its prompt): the window then leaves Ctrl+C, Ctrl+X and Ctrl+V to it
    /// (the WinForms terminal said so with <c>CanEdit</c>).
    /// </summary>
    public virtual bool EditsLocally => false;

    /// <summary>The anchor of this terminal for pastes, questions and the paste menu.</summary>
    internal PasteAnchor PasteAnchor => _anchor;

    // The place of the caret on the screen in device pixels and the height of a row, for the popups.
    protected void GetCaretScreenPlace(out GridPoint location, out int lineHeight)
    {
        TerminalFont font = View.TerminalFont;
        TextDocument.TextPosition caret = Model.Caret;
        Point point = new Point(caret.Column * font.CellWidth, (caret.Line - Model.TopLine + 1) * font.CellHeight - font.CellHeight);
        try
        {
            Point screen = View.PointToScreen(point);
            location = new GridPoint((int)Math.Round(screen.X), (int)Math.Round(screen.Y));
        }
        catch (InvalidOperationException)
        {
            location = new GridPoint(0, 0);
        }
        lineHeight = (int)Math.Round(font.CellHeight * font.PixelsPerDip);
    }

    /// <summary>Sends bytes for the remote side through the usual path (the input dispatcher), as if they had been typed.</summary>
    protected void SendInput(string text, TerminalInputKind kind)
    {
        _view.SendInput(text, kind);
    }

    public void CopySelection(bool select = true)
    {
        _view.CopySelection();
    }

    public void CopyAll()
    {
        SelectDocument();
        _view.CopySelection();
    }

    /// <summary>Selects the whole document (the scrollback and the screen).</summary>
    public virtual void SelectAll()
    {
        SelectDocument();
    }

    private void SelectDocument()
    {
        EditableTextDocument document = _model.Document;
        if (document.LineCount == 0)
        {
            return;
        }
        int last = document.LineCount - 1;
        _model.Select(new TextDocument.TextPosition(0, 0), new TextDocument.TextPosition(document.GetLineText(last).Length, last));
    }

    public void ClearSelection()
    {
        _model.ClearSelection();
    }

    public bool HasSelection => _model.HasSelection;

    public virtual void ClearOutput()
    {
        _model.ClearOutput();
    }

    public virtual void BeginNewSession()
    {
        _model.BeginNewSession();
    }

    /// <summary>A full reset that the user asked for: the output that waits is dropped.</summary>
    public virtual void Reset()
    {
        _view.ResetTerminal();
    }

    public void FocusTerminal()
    {
        _view.Focus();
        Keyboard.Focus(_view);
    }

    public ISearchTarget SearchTarget => this;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        OnDisposing();
        _pasteJob?.Abort();
        _view.Attach(null);
        OpenLogFile(null);
    }

    // Input and resize go through the worker of the window, in order with the input of the other tabs.

    private void OnViewData(byte[] bytes, TerminalInputKind kind)
    {
        Post(() => DataEntered?.Invoke(this, bytes, kind));
    }

    private void Post(Action action)
    {
        TerminalEventDispatcher dispatcher = InputDispatcher;
        if (dispatcher != null)
        {
            dispatcher.PostInput(() => action());
        }
        else
        {
            action();
        }
    }

    private void PostResize(int columns, int rows)
    {
        TerminalEventDispatcher dispatcher = InputDispatcher;
        if (dispatcher != null)
        {
            dispatcher.PostResize(() => TerminalSizeChanged?.Invoke(this, columns, rows));
        }
        else
        {
            TerminalSizeChanged?.Invoke(this, columns, rows);
        }
    }

    // The bell.

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern bool PlaySound(string sound, UIntPtr module, uint flags);

    private void OnBell()
    {
        TerminalEventDispatcher dispatcher = InputDispatcher;
        if (dispatcher == null)
        {
            PlayBell();
        }
        else
        {
            dispatcher.PostBeep(PlayBell);
        }
    }

    private void PlayBell()
    {
        try
        {
            switch (BellMode)
            {
                case TerminalEmulatorControl.BeepMode.ConsoleBeep:
                    Console.Beep();
                    break;
                case TerminalEmulatorControl.BeepMode.WaveFile:
                    if (BellSoundFile != null)
                    {
                        new SoundPlayer(BellSoundFile).PlaySync();
                    }
                    break;
                case TerminalEmulatorControl.BeepMode.SystemBeep:
                    PlaySound(".Default", UIntPtr.Zero, 65536u);
                    break;
            }
        }
        catch
        {
            // No sound device, a missing file: the bell is not worth an error.
        }
    }

    // Links.

    private sealed class LinkHost : IOutputLinkHost
    {
        private readonly WpfTerminal _terminal;

        public LinkHost(WpfTerminal terminal)
        {
            _terminal = terminal;
        }

        public bool IsLinkExtension(string extension)
        {
            return _terminal.IsLinkExtension(extension);
        }

        public void RequestOpenFile(string path, int? line, int? column, ColumnUnit unit)
        {
            _terminal.OpenLinkedFile(path, line, column, unit);
        }

        public void RequestOpenUrl(string url)
        {
            UrlOpener.Open(url);
        }
    }

    private bool IsLinkExtension(string extension)
    {
        string text = _appSettings?.VirtualTerminal?.AutoLinkExtensions;
        if (!string.Equals(text, _linkExtensionsText, StringComparison.Ordinal) || _linkExtensions == null)
        {
            _linkExtensionsText = text;
            _linkExtensions = VirtualTerminalSettings.ParseAutoLinkExtensions(text);
        }
        return _linkExtensions.Contains(extension);
    }

    private void OpenLinkedFile(string path, int? line, int? column, ColumnUnit unit)
    {
        if (!path.StartsWith("/", StringComparison.Ordinal))
        {
            // A name relative to a directory that the shell never reported: the directory it is in now.
            string directory = null;
            try
            {
                directory = DirectoryFallback?.Invoke();
            }
            catch (Exception)
            {
            }
            path = RemotePathUtils.Combine(directory, path);
        }
        FileLinkClicked?.Invoke(this, new OpenFileEventArgs
        {
            Path = path,
            Line = line,
            Column = column,
            ColumnUnit = unit
        });
    }

    // The search of the window (ISearchTarget). The terminal is read-only: replacing does nothing.

    bool ISearchTarget.HasSelection => _model.HasSelection;

    TextDocument.TextPosition ISearchTarget.SelectionStart
    {
        get
        {
            _model.GetSelection(out TextDocument.TextPosition start, out _);
            return _model.HasSelection ? start : _model.Caret;
        }
    }

    TextDocument.TextPosition ISearchTarget.SelectionEnd
    {
        get
        {
            _model.GetSelection(out _, out TextDocument.TextPosition end);
            return _model.HasSelection ? end : _model.Caret;
        }
    }

    string ISearchTarget.GetLineText(int line)
    {
        EditableTextDocument document = _model.Document;
        return line >= 0 && line < document.LineCount ? document.GetLineText(line) : null;
    }

    string ISearchTarget.GetRangeText(TextDocument.TextPosition start, TextDocument.TextPosition end)
    {
        return _model.Document.GetRangeText(start, end);
    }

    TextDocument.TextPosition ISearchTarget.Find(TextDocument.TextPosition textPosition, TextMatcher matcher, bool searchBackward, bool wrapAround, FindSelectMode findSelectMode, TextDocument.TextPosition scopeStart, TextDocument.TextPosition scopeEnd)
    {
        TextDocument.TextPosition found = DocumentSearch.Find(_model.Document, textPosition, matcher, searchBackward, wrapAround, scopeStart, scopeEnd, out int length);
        if (found.IsValid && findSelectMode != FindSelectMode.None)
        {
            _model.Select(found, new TextDocument.TextPosition(found.Column + length, found.Line));
            if (found.Line < _model.TopLine || found.Line >= _model.TopLine + _model.Rows)
            {
                _model.TopLine = found.Line - _model.Rows / 2;
            }
        }
        return found;
    }

    void ISearchTarget.ReplaceText(TextDocument.TextPosition start, TextDocument.TextPosition end, bool select, string text)
    {
    }

    int ISearchTarget.ReplaceAll(TextMatcher matcher, string replacement, TextDocument.TextPosition scopeStart, TextDocument.TextPosition scopeEnd, out TextDocument.TextPosition newScopeEnd)
    {
        newScopeEnd = scopeEnd;
        return 0;
    }

    void ISearchTarget.SetMatchHighlight(TextMatcher matcher)
    {
        _view.SetMatchHighlight(matcher);
    }

    int ISearchTarget.AddHighlight(TextDocument.TextPosition start, TextDocument.TextPosition end, HighlightStyle highlightStyle)
    {
        return _view.AddHighlight(start, end, highlightStyle);
    }

    int ISearchTarget.SetHighlight(int highlightIndex, TextDocument.TextPosition start, TextDocument.TextPosition end, HighlightStyle highlightStyle)
    {
        return _view.SetHighlight(highlightIndex, start, end, highlightStyle);
    }

    void ISearchTarget.RemoveHighlight(int highlightIndex)
    {
        _view.RemoveHighlight(highlightIndex);
    }

    // The document of a terminal changes only by output, never by the search bar.
    event TextRangeChangedHandler ISearchTarget.RangeChanged
    {
        add { }
        remove { }
    }

    /// <summary>The terminal as the anchor of a paste: its window, its thread, its place on the screen.</summary>
    private sealed class Anchor : PasteAnchor
    {
        private readonly WpfTerminal _terminal;

        public Anchor(WpfTerminal terminal)
        {
            _terminal = terminal;
        }

        public override IntPtr Handle => (PresentationSource.FromVisual(_terminal) as HwndSource)?.Handle ?? IntPtr.Zero;

        public override bool IsAlive => !_terminal._disposed && PresentationSource.FromVisual(_terminal) != null;

        public override void Post(Action action)
        {
            _terminal.Dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
        }

        public override System.Drawing.Rectangle ScreenBounds
        {
            get
            {
                try
                {
                    Point topLeft = _terminal.PointToScreen(new Point(0, 0));
                    Point bottomRight = _terminal.PointToScreen(new Point(_terminal.ActualWidth, _terminal.ActualHeight));
                    return System.Drawing.Rectangle.FromLTRB((int)topLeft.X, (int)topLeft.Y, (int)bottomRight.X, (int)bottomRight.Y);
                }
                catch (InvalidOperationException)
                {
                    return System.Drawing.Rectangle.Empty;
                }
            }
        }
    }
}
