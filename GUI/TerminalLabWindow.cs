using SmarTTY.Controls;
using SmarTTY.GUI.TerminalView;
using SmarTTY.Terminal.Input;
using SmarTTY.Terminal.Rendering;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace SmarTTY.GUI;

/// <summary>
/// Tools > WPF terminal lab: the new WPF terminal view (stage 2) on its own, before it replaces the WinForms one in the tabs. It runs the
/// generated scripts of <see cref="TerminalBenchmarkScripts"/> and shows what the frames cost, mirrors a classic terminal tab (the output
/// of its connection is shown here too, and what is typed here goes to it) to try the keyboard and the IME against a real program, logs
/// the bytes of every key, and says how WPF renders on this machine (hardware, software, Remote Desktop) with a switch that forces the
/// software mode in this window.
/// </summary>
internal sealed class TerminalLabWindow : Window
{
    private readonly Func<TerminalEmulatorControl> _activeTerminal;

    private readonly TerminalPanel _panel;

    private readonly TerminalRenderView _view;

    private readonly TerminalDisplayModel _model = new TerminalDisplayModel(80, 24);

    private readonly ComboBox _scripts = new ComboBox { MinWidth = 260 };

    private readonly Button _runButton = new Button { Content = "Run", Padding = new Thickness(10, 2, 10, 2) };

    private readonly Button _mirrorButton = new Button { Content = "Mirror the active terminal tab", Padding = new Thickness(10, 2, 10, 2) };

    private readonly CheckBox _localEcho = new CheckBox { Content = "Local echo", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };

    private readonly CheckBox _software = new CheckBox { Content = "Software rendering (this window)", VerticalAlignment = VerticalAlignment.Center };

    private readonly CheckBox _blink = new CheckBox { Content = "Blinking caret", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };

    private readonly ComboBox _fontSize = new ComboBox { Width = 60 };

    private readonly TextBlock _diagnostics = new TextBlock { Margin = new Thickness(4), TextWrapping = TextWrapping.Wrap };

    private readonly TextBlock _statsText = new TextBlock { Margin = new Thickness(4), FontFamily = new FontFamily("Consolas") };

    private readonly TextBox _keyLog = new TextBox
    {
        IsReadOnly = true,
        Height = 130,
        FontFamily = new FontFamily("Consolas"),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
    };

    private readonly DispatcherTimer _statsTimer;

    private readonly Stopwatch _statsClock = Stopwatch.StartNew();

    private readonly List<string> _keyLogLines = new List<string>();

    private long _framesAtLastTick;

    private double _lastTickSeconds;

    // The benchmark that runs: its clock, its size, whether all of it was queued, and the frames when it started.
    private Stopwatch _benchmarkClock;

    private long _benchmarkLength;

    private bool _benchmarkQueued;

    private long _benchmarkStartFrames;

    private string _benchmarkName;

    // The mirrored tab.
    private TerminalEmulatorControl _mirror;

    public TerminalLabWindow(Func<TerminalEmulatorControl> activeTerminal)
    {
        _activeTerminal = activeTerminal;
        Title = "WPF terminal lab";
        Width = 1000;
        Height = 760;
        XtermKeyTable keys;
        using (Stream layout = Assembly.GetExecutingAssembly().GetManifestResourceStream("VisualTTY.XTermKeys.xml"))
        {
            keys = new XtermKeyTable(layout);
        }
        _panel = new TerminalPanel(keys);
        _view = _panel.View;
        _view.Attach(_model);
        _view.DataEntered += OnDataEntered;
        _view.InputLogged += OnInputLogged;
        _model.TitleChanged += () => Title = "WPF terminal lab - " + _model.Title;

        foreach (string name in TerminalBenchmarkScripts.Names)
        {
            _scripts.Items.Add(name);
        }
        _scripts.SelectedIndex = 0;
        foreach (int size in new[] { 9, 10, 11, 12, 13, 14, 16, 18, 20, 24 })
        {
            _fontSize.Items.Add(size);
        }
        _fontSize.SelectedItem = 13;
        _fontSize.SelectionChanged += (sender, e) =>
        {
            if (_fontSize.SelectedItem is int size)
            {
                _view.TerminalFontSize = size;
            }
        };
        _runButton.Click += (sender, e) => RunBenchmark();
        _mirrorButton.Click += (sender, e) => ToggleMirror();
        _software.Checked += (sender, e) => SetSoftwareRendering(true);
        _software.Unchecked += (sender, e) => SetSoftwareRendering(false);
        _blink.Checked += (sender, e) => _view.CursorBlink = true;
        _blink.Unchecked += (sender, e) => _view.CursorBlink = false;
        Button reset = new Button { Content = "Reset", Padding = new Thickness(10, 2, 10, 2) };
        reset.Click += (sender, e) =>
        {
            _view.ResetTerminal();
            _view.Stats.Reset();
            _framesAtLastTick = 0;
            _view.Focus();
        };

        WrapPanel tools = new WrapPanel { Margin = new Thickness(4) };
        foreach (UIElement element in new UIElement[] { _scripts, _runButton, _mirrorButton, reset, new TextBlock { Text = "Font size", VerticalAlignment = VerticalAlignment.Center }, _fontSize, _localEcho, _blink, _software })
        {
            if (element is FrameworkElement frameworkElement)
            {
                frameworkElement.Margin = new Thickness(4, 2, 4, 2);
            }
            tools.Children.Add(element);
        }

        StackPanel bottom = new StackPanel();
        bottom.Children.Add(_diagnostics);
        bottom.Children.Add(_statsText);
        bottom.Children.Add(new TextBlock { Margin = new Thickness(4, 4, 4, 0), Text = "Keys and the bytes they sent (control characters as ^X):" });
        bottom.Children.Add(_keyLog);

        DockPanel root = new DockPanel();
        DockPanel.SetDock(tools, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(tools);
        root.Children.Add(bottom);
        root.Children.Add(_panel);
        Content = root;

        _statsTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(500) };
        _statsTimer.Tick += (sender, e) => UpdateStats();
        _statsTimer.Start();
        RenderCapability.TierChanged += OnTierChanged;
        Loaded += (sender, e) =>
        {
            UpdateDiagnostics();
            _model.ProcessOutput("WPF terminal lab. Type here (local echo), run a script, or mirror a classic terminal tab.\r\n" +
                "\u001b[1mBold\u001b[0m \u001b[31mred\u001b[0m \u001b[42mgreen background\u001b[0m \u001b[4munderline\u001b[0m \u001b[7minverse\u001b[0m " +
                "\u001b[38;5;208m256 colours\u001b[0m \u001b[38;2;120;180;255m24-bit\u001b[0m Кириллица ─│┌┐\r\n");
            _view.Focus();
        };
        Closed += (sender, e) =>
        {
            StopMirror();
            _statsTimer.Stop();
            RenderCapability.TierChanged -= OnTierChanged;
        };
    }

    private void OnTierChanged(object sender, EventArgs e)
    {
        UpdateDiagnostics();
    }

    private HwndSource Source => PresentationSource.FromVisual(this) as HwndSource;

    private void SetSoftwareRendering(bool software)
    {
        HwndSource source = Source;
        if (source?.CompositionTarget != null)
        {
            source.CompositionTarget.RenderMode = software ? RenderMode.SoftwareOnly : RenderMode.Default;
        }
        UpdateDiagnostics();
    }

    private void UpdateDiagnostics()
    {
        int tier = RenderCapability.Tier >> 16;
        string tierText = tier == 0 ? "0 - software (no usable GPU: Remote Desktop, a virtual machine without 3D, an old driver)"
            : tier == 1 ? "1 - partial hardware" : "2 - hardware";
        HwndSource source = Source;
        string windowMode = source?.CompositionTarget?.RenderMode.ToString() ?? "?";
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        TerminalFont font = _view.TerminalFont;
        _diagnostics.Text = "Rendering tier " + tierText + ". Remote session: " + (SystemParameters.IsRemoteSession ? "yes" : "no")
            + ". Process render mode: " + RenderOptions.ProcessRenderMode + ", this window: " + windowMode
            + ". DPI: " + (dpi.PixelsPerInchX).ToString("0", CultureInfo.InvariantCulture) + " (" + dpi.PixelsPerDip.ToString("0.##", CultureInfo.InvariantCulture) + " px/dip)"
            + ". Font " + _view.TerminalFontFamily.Source + " " + font.EmSize.ToString(CultureInfo.InvariantCulture)
            + ", cell " + (font.CellWidth * font.PixelsPerDip).ToString("0", CultureInfo.InvariantCulture) + " x " + (font.CellHeight * font.PixelsPerDip).ToString("0", CultureInfo.InvariantCulture) + " px"
            + ", grid " + _model.Columns + " x " + _model.Rows + ".";
    }

    private void UpdateStats()
    {
        TerminalFrameStats stats = _view.Stats;
        double now = _statsClock.Elapsed.TotalSeconds;
        double fps = (stats.Frames - _framesAtLastTick) / Math.Max(0.001, now - _lastTickSeconds);
        _framesAtLastTick = stats.Frames;
        _lastTickSeconds = now;
        double reuse = stats.RowsDrawn + stats.RowsReused == 0 ? 0 : 100.0 * stats.RowsReused / (stats.RowsDrawn + stats.RowsReused);
        StringBuilder text = new StringBuilder();
        text.Append("frames/s ").Append(fps.ToString("0", CultureInfo.InvariantCulture))
            .Append(" | frames ").Append(stats.Frames)
            .Append(" | last frame: parse ").Append(stats.LastParseMilliseconds.ToString("0.0", CultureInfo.InvariantCulture)).Append(" ms, build ")
            .Append(stats.LastBuildMilliseconds.ToString("0.0", CultureInfo.InvariantCulture)).Append(" ms, rows drawn ").Append(stats.LastRowsDrawn)
            .Append(", moved/kept ").Append(stats.LastRowsReused)
            .Append(" | rows kept overall ").Append(reuse.ToString("0", CultureInfo.InvariantCulture)).Append('%')
            .Append(" | budget hits ").Append(stats.BudgetHits)
            .Append(" | waiting ").Append(_view.PendingOutput).Append(" chars");
        if (_benchmarkClock != null)
        {
            text.Append("\n").Append(BenchmarkState());
        }
        _statsText.Text = text.ToString();
    }

    private string BenchmarkState()
    {
        double seconds = _benchmarkClock.Elapsed.TotalSeconds;
        long frames = _view.Stats.Frames - _benchmarkStartFrames;
        bool done = _benchmarkQueued && _view.PendingOutput == 0;
        string state = done ? "done" : "running";
        if (done && _benchmarkClock.IsRunning)
        {
            _benchmarkClock.Stop();
        }
        return _benchmarkName + ": " + state + ", " + seconds.ToString("0.00", CultureInfo.InvariantCulture) + " s, "
            + (_benchmarkLength / 1024.0 / Math.Max(seconds, 0.001)).ToString("0", CultureInfo.InvariantCulture) + " KB/s of output, "
            + frames + " frames (" + (frames / Math.Max(seconds, 0.001)).ToString("0", CultureInfo.InvariantCulture) + "/s)";
    }

    private void RunBenchmark()
    {
        if (_mirror != null)
        {
            MessageBox.Show(this, "Stop the mirror first: the script would be mixed with the output of the tab.", Title);
            return;
        }
        int script = Math.Max(0, _scripts.SelectedIndex);
        List<string> chunks = TerminalBenchmarkScripts.Create(script, _model.Columns, _model.Rows);
        _benchmarkName = TerminalBenchmarkScripts.Names[script];
        _benchmarkLength = TerminalBenchmarkScripts.Length(chunks);
        _benchmarkQueued = false;
        _benchmarkStartFrames = _view.Stats.Frames;
        _benchmarkClock = Stopwatch.StartNew();
        // Queued from another thread, as a connection delivers its output.
        Task.Run(() =>
        {
            foreach (string chunk in chunks)
            {
                _view.EnqueueOutput(chunk);
            }
            _benchmarkQueued = true;
        });
        _view.Focus();
    }

    // The mirror of a classic terminal tab.

    private void ToggleMirror()
    {
        if (_mirror != null)
        {
            StopMirror();
            return;
        }
        TerminalEmulatorControl terminal = _activeTerminal?.Invoke();
        if (terminal == null || terminal is SshTerminalControl || terminal is SerialTerminalControl)
        {
            MessageBox.Show(this, "Open a classic terminal tab (not the Smart Terminal and not a COM/TCP device) and make it the active tab, then press the button again.", Title);
            return;
        }
        _mirror = terminal;
        _view.ResetTerminal();
        _model.SuppressReplies = true;
        _view.SetFixedGrid(terminal.TerminalSize.Width, terminal.TerminalSize.Height);
        _view.EnqueueOutput(SeedFrom(terminal));
        terminal.OutputReceived += OnMirroredOutput;
        terminal.TerminalSizeChanged += OnMirroredResize;
        _mirrorButton.Content = "Stop the mirror";
        _localEcho.IsEnabled = false;
        _runButton.IsEnabled = false;
        AppendLog("mirror", "started: " + terminal.TerminalSize.Width + " x " + terminal.TerminalSize.Height + "; press Ctrl+L to make the program draw its screen again");
        _view.Focus();
    }

    // What the screen of the tab shows now, as output: the text without its colours and the cursor (the modes of the program are not known
    // until it sets them again, e.g. after Ctrl+L).
    private static string SeedFrom(TerminalEmulatorControl terminal)
    {
        StringBuilder seed = new StringBuilder();
        if (terminal.ActiveBufferIndex != 0)
        {
            seed.Append("\u001b[?1049h");
        }
        seed.Append("\u001b[2J\u001b[H");
        System.Drawing.Point cursor = terminal.CursorPosition;
        int firstLine = terminal.CursorDocumentLine - cursor.Y;
        int columns = terminal.TerminalSize.Width;
        for (int row = 0; row < terminal.TerminalSize.Height; row++)
        {
            string text;
            try
            {
                text = terminal.GetLineText(firstLine + row) ?? "";
            }
            catch (Exception)
            {
                text = "";
            }
            if (text.Length > columns)
            {
                text = text.Substring(0, columns);
            }
            seed.Append("\u001b[").Append(row + 1).Append(";1H").Append(text);
        }
        seed.Append("\u001b[").Append(cursor.Y + 1).Append(';').Append(cursor.X + 1).Append('H');
        return seed.ToString();
    }

    private void OnMirroredOutput(string output)
    {
        _view.EnqueueOutput(output);
    }

    private void OnMirroredResize(TerminalEmulatorControl terminal, int columns, int rows)
    {
        Dispatcher.BeginInvoke(new Action(() => _view.SetFixedGrid(columns, rows)));
    }

    private void StopMirror()
    {
        if (_mirror == null)
        {
            return;
        }
        _mirror.OutputReceived -= OnMirroredOutput;
        _mirror.TerminalSizeChanged -= OnMirroredResize;
        _mirror = null;
        _model.SuppressReplies = false;
        _mirrorButton.Content = "Mirror the active terminal tab";
        _localEcho.IsEnabled = true;
        _runButton.IsEnabled = true;
        AppendLog("mirror", "stopped");
    }

    private void OnDataEntered(byte[] bytes, TerminalInputKind kind)
    {
        TerminalEmulatorControl mirror = _mirror;
        if (mirror != null)
        {
            if (!mirror.IsDisposed)
            {
                mirror.SendInput(bytes, (TerminalEmulatorControl.InputKind)(int)kind);
            }
            return;
        }
        if (_localEcho.IsChecked == true && kind != TerminalInputKind.MouseReport)
        {
            // Enter is shown as a new line; the keys' escape sequences move the cursor, as a program would.
            string text = Encoding.UTF8.GetString(bytes);
            _view.EnqueueOutput(text == "\r" ? "\r\n" : text);
        }
    }

    private void OnInputLogged(string source, string text)
    {
        AppendLog(source, TerminalKeyEncoder.Describe(text));
    }

    private void AppendLog(string source, string text)
    {
        _keyLogLines.Add(source + "  ->  " + text);
        if (_keyLogLines.Count > 200)
        {
            _keyLogLines.RemoveRange(0, _keyLogLines.Count - 200);
        }
        _keyLog.Text = string.Join("\n", _keyLogLines);
        _keyLog.ScrollToEnd();
    }
}
