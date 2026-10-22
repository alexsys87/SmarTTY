using Microsoft.Win32;
using SmarTTY.Controls;
using SmarTTY.IO;
using SmarTTY.Utilities;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SmarTTY.Forms;

/// <summary>An owner for a window shown from the WPF main window: only its handle is known.</summary>
internal sealed class WindowHandleOwner : IWin32Window
{
    public WindowHandleOwner(IntPtr handle)
    {
        Handle = handle;
    }

    public IntPtr Handle { get; }
}

/// <summary>
/// Tools -> TFTP server / U-Boot upload. Runs a TFTP server for the firmware file and, on request, types the U-Boot commands
/// on the terminal's COM (or TCP) connection that make the board download it: it sets the network variables, runs tftpboot,
/// waits for the prompt after every command, checks that the download really finished, and only then runs the commands the
/// user chose for the image (boot it, write it to flash, ...). The window is built in code, like the other small dialogs.
/// The server runs only while the window is open.
/// </summary>
internal sealed class TftpFirmwareForm : DpiAwareForm
{
    private sealed class ServedFile
    {
        public string Name;
        public string FullPath;

        public override string ToString()
        {
            return Name + "    (" + FullPath + ")";
        }
    }

    private sealed class Preset
    {
        public Preset(string title, string commands)
        {
            Title = title;
            Commands = commands;
        }

        public string Title { get; }

        /// <summary>The commands, one per line; null for "keep what is typed".</summary>
        public string Commands { get; }

        public override string ToString()
        {
            return Title;
        }
    }

    /// <summary>The sequence stopped for a reason the user should read.</summary>
    private sealed class SequenceFailure : Exception
    {
        public SequenceFailure(string message) : base(message)
        {
        }
    }

    private const int TftpPort = 69;
    private const int QuietMs = 250;
    private const int PromptTimeoutMs = 4000;
    private const string AutoPrompt = "(auto)";
    private const string RegistrySubKey = "Tftp";

    private static readonly Preset[] Presets =
    {
        new Preset("Only download to the board's RAM", string.Empty),
        new Preset("Boot the downloaded image (bootm)", "bootm {addr}"),
        new Preset("Run the downloaded program (go)", "go {addr}"),
        new Preset("Write to SPI NOR flash (sf) - set the offset", "sf probe\nsf erase <flash offset> +${filesize}\nsf write {addr} <flash offset> ${filesize}"),
        new Preset("Save as a file on a FAT partition (fatwrite)", "fatwrite mmc 0:1 {addr} {file} ${filesize}"),
        new Preset("Custom commands", null)
    };

    private readonly DeviceTerminalControl _terminal;
    private readonly ConsoleOutputWatcher _watcher = new ConsoleOutputWatcher();
    private readonly List<ServedFile> _files = new List<ServedFile>();
    private readonly Dictionary<string, int> _completedTransfers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    private readonly ListBox _fileList;
    private readonly Button _addFile;
    private readonly Button _removeFile;
    private readonly ComboBox _fileToSend;
    private readonly ComboBox _serverIp;
    private readonly TextBox _deviceIp;
    private readonly CheckBox _restrictClient;
    private readonly TextBox _loadAddress;
    private readonly ComboBox _prompt;
    private readonly ComboBox _preset;
    private readonly TextBox _after;
    private readonly Button _startServer;
    private readonly Button _stopServer;
    private readonly Label _serverStatus;
    private readonly Button _send;
    private readonly Button _cancel;
    private readonly ProgressBar _progress;
    private readonly Label _stepInfo;
    private readonly TextBox _log;

    private IDisposable _consoleListener;
    private TftpServer _server;
    private CancellationTokenSource _cts;
    private bool _busy;
    private bool _commandInFlight;
    private bool _updatingPreset;
    private bool _closingWithTerminal;

    public TftpFirmwareForm(DeviceTerminalControl terminal, string terminalTitle)
    {
        _terminal = terminal;

        // Laid out for 96 DPI: DpiAwareForm scales it for the monitor it is on.
        FormIcons.Apply(this, "firmware");
        Text = "TFTP server / U-Boot upload - " + terminalTitle;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = true;
        MaximizeBox = false;
        ShowInTaskbar = true;
        ClientSize = new Size(604, 668);
        MinimumSize = new Size(Width, Height - 90);

        // ---- files ----
        GroupBox filesGroup = new GroupBox { Text = "Files the server gives out", Location = new Point(12, 8), Size = new Size(580, 134), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        _fileList = new ListBox { Location = new Point(10, 20), Size = new Size(474, 76), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, HorizontalScrollbar = true };
        _addFile = new Button { Text = "Add...", Location = new Point(492, 20), Size = new Size(76, 26), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        _removeFile = new Button { Text = "Remove", Location = new Point(492, 52), Size = new Size(76, 26), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        Label downloadLabel = new Label { Text = "Download this one:", Location = new Point(10, 107), Size = new Size(116, 20), TextAlign = ContentAlignment.MiddleLeft };
        _fileToSend = new ComboBox { Location = new Point(130, 105), Size = new Size(354, 24), DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        filesGroup.Controls.AddRange(new Control[] { _fileList, _addFile, _removeFile, downloadLabel, _fileToSend });

        // ---- network ----
        GroupBox networkGroup = new GroupBox { Text = "Network", Location = new Point(12, 148), Size = new Size(580, 84), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        Label serverLabel = new Label { Text = "This computer:", Location = new Point(10, 24), Size = new Size(116, 20), TextAlign = ContentAlignment.MiddleLeft };
        _serverIp = new ComboBox { Location = new Point(130, 22), Size = new Size(170, 24), DropDownStyle = ComboBoxStyle.DropDown };
        Label deviceLabel = new Label { Text = "Board (ipaddr):", Location = new Point(320, 24), Size = new Size(100, 20), TextAlign = ContentAlignment.MiddleLeft };
        _deviceIp = new TextBox { Location = new Point(424, 22), Size = new Size(144, 24) };
        _restrictClient = new CheckBox { Text = "Answer only the board's address", Location = new Point(130, 52), Size = new Size(300, 22), Checked = true };
        networkGroup.Controls.AddRange(new Control[] { serverLabel, _serverIp, deviceLabel, _deviceIp, _restrictClient });

        // ---- U-Boot ----
        GroupBox bootGroup = new GroupBox { Text = "U-Boot", Location = new Point(12, 238), Size = new Size(580, 214), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        Label loadLabel = new Label { Text = "Load address:", Location = new Point(10, 26), Size = new Size(116, 20), TextAlign = ContentAlignment.MiddleLeft };
        _loadAddress = new TextBox { Location = new Point(130, 24), Size = new Size(170, 24) };
        Label promptLabel = new Label { Text = "Prompt ends with:", Location = new Point(320, 26), Size = new Size(100, 20), TextAlign = ContentAlignment.MiddleLeft };
        _prompt = new ComboBox { Location = new Point(424, 24), Size = new Size(144, 24), DropDownStyle = ComboBoxStyle.DropDown };
        _prompt.Items.AddRange(new object[] { AutoPrompt, "=>", ">", "#" });
        Label afterLabel = new Label { Text = "After the download:", Location = new Point(10, 56), Size = new Size(116, 20), TextAlign = ContentAlignment.MiddleLeft };
        _preset = new ComboBox { Location = new Point(130, 54), Size = new Size(438, 24), DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        _preset.Items.AddRange(Presets);
        _after = new TextBox
        {
            Location = new Point(10, 84),
            Size = new Size(558, 90),
            Multiline = true,
            AcceptsReturn = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false,
            Font = new Font(FontFamily.GenericMonospace, 9f),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        Label hint = new Label
        {
            Text = "One command per line. {file} {addr} {serverip} {ipaddr} are filled in; replace every <...> yourself. Each command is sent after the previous one has returned to the prompt, and nothing more is sent after a failure.",
            Location = new Point(10, 176),
            Size = new Size(558, 34),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            ForeColor = SystemColors.GrayText
        };
        bootGroup.Controls.AddRange(new Control[] { loadLabel, _loadAddress, promptLabel, _prompt, afterLabel, _preset, _after, hint });

        // ---- run ----
        _startServer = new Button { Text = "Start server", Location = new Point(12, 460), Size = new Size(100, 28) };
        _stopServer = new Button { Text = "Stop server", Location = new Point(118, 460), Size = new Size(100, 28) };
        _serverStatus = new Label { Text = "Server stopped.", Location = new Point(228, 464), Size = new Size(364, 20), AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        _send = new Button { Text = "Send to the board", Location = new Point(12, 496), Size = new Size(130, 30) };
        _cancel = new Button { Text = "Cancel", Location = new Point(148, 496), Size = new Size(80, 30), Enabled = false };
        _progress = new ProgressBar { Location = new Point(238, 502), Size = new Size(354, 18), Minimum = 0, Maximum = 100, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        _stepInfo = new Label { Text = string.Empty, Location = new Point(12, 532), Size = new Size(580, 20), AutoEllipsis = true, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        _log = new TextBox
        {
            Location = new Point(12, 556),
            Size = new Size(580, 100),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font(FontFamily.GenericMonospace, 8.25f),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };

        Controls.AddRange(new Control[] { filesGroup, networkGroup, bootGroup, _startServer, _stopServer, _serverStatus, _send, _cancel, _progress, _stepInfo, _log });

        _addFile.Click += (sender, e) => AddFile();
        _removeFile.Click += (sender, e) => RemoveFile();
        _fileList.SelectedIndexChanged += (sender, e) => UpdateUi();
        _startServer.Click += (sender, e) => StartServerFromUi();
        _stopServer.Click += (sender, e) => StopServer();
        _send.Click += async (sender, e) => await SendToBoardAsync();
        _cancel.Click += (sender, e) => CancelSend();
        _preset.SelectedIndexChanged += (sender, e) => PresetChanged();
        _after.TextChanged += (sender, e) => AfterTextChanged();
        FormClosing += OnFormClosing;
        FormClosed += OnFormClosed;

        LoadSettings();
        _consoleListener = _terminal.ListenToConsole(text => _watcher.Append(text, Environment.TickCount));
        UpdateUi();
    }

    /// <summary>Closes the window because the terminal it belongs to is gone: nobody is left to ask.</summary>
    public void CloseWithTerminal()
    {
        _closingWithTerminal = true;
        Close();
    }

    // ---------- the window ----------

    private void OnFormClosing(object sender, FormClosingEventArgs e)
    {
        if (_busy && !_closingWithTerminal)
        {
            DialogResult answer = MessageBox.Show(this, "The board is being sent commands. Stop this and close the window?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }
        if (_busy)
        {
            _cts?.Cancel();
        }
    }

    private void OnFormClosed(object sender, FormClosedEventArgs e)
    {
        SaveSettings();
        StopServer();
        _consoleListener?.Dispose();
        _consoleListener = null;
    }

    private void UpdateUi()
    {
        bool running = _server != null && _server.IsRunning;
        _startServer.Enabled = !running && !_busy;
        _stopServer.Enabled = running && !_busy;
        _send.Enabled = !_busy;
        _cancel.Enabled = _busy;
        _serverIp.Enabled = !running && !_busy;
        _deviceIp.Enabled = !running && !_busy;
        _restrictClient.Enabled = !running && !_busy;
        _loadAddress.Enabled = !_busy;
        _prompt.Enabled = !_busy;
        _preset.Enabled = !_busy;
        _after.Enabled = !_busy;
        _addFile.Enabled = !_busy;
        _removeFile.Enabled = !_busy && _fileList.SelectedIndex >= 0;
        _fileToSend.Enabled = !_busy && _files.Count > 0;
        _serverStatus.Text = running ? "Listening on " + _server.LocalEndPoint + ", only while this window is open." : "Server stopped.";
    }

    private void Log(string message)
    {
        if (IsDisposed || _log.IsDisposed)
        {
            return;
        }
        if (_log.TextLength > 100000)
        {
            _log.Text = _log.Text.Substring(_log.TextLength - 50000);
        }
        _log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + "\r\n");
    }

    private void SetStep(string text)
    {
        if (!IsDisposed)
        {
            _stepInfo.Text = text;
        }
    }

    /// <summary>Runs <paramref name="action"/> on the window's thread; the server's events come on its own threads.</summary>
    private void OnUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }
        try
        {
            BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // The window is closing.
        }
    }

    // ---------- files ----------

    private void AddFile()
    {
        using System.Windows.Forms.OpenFileDialog dialog = new System.Windows.Forms.OpenFileDialog
        {
            Title = "Select the firmware (or any file the board should fetch over TFTP)",
            Filter = "All files (*.*)|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        foreach (string path in dialog.FileNames)
        {
            string name = SanitizeName(Path.GetFileName(path));
            ServedFile same = _files.Find(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            if (same != null)
            {
                same.FullPath = path; // a file of that name is already listed: the new one takes its place
            }
            else
            {
                _files.Add(new ServedFile { Name = name, FullPath = path });
            }
            if (name != Path.GetFileName(path))
            {
                Log("The file name \"" + Path.GetFileName(path) + "\" is served as \"" + name + "\" (U-Boot commands cannot take spaces or special characters).");
            }
        }
        RefreshFileViews(_files.Count > 0 ? _files[_files.Count - 1].Name : null);
    }

    private void RemoveFile()
    {
        int index = _fileList.SelectedIndex;
        if (index < 0 || index >= _files.Count)
        {
            return;
        }
        string keep = _fileToSend.SelectedItem?.ToString();
        _files.RemoveAt(index);
        RefreshFileViews(keep);
    }

    /// <summary>Keeps letters, digits and . _ - + @ , only: the name goes onto a U-Boot command line.</summary>
    private static string SanitizeName(string name)
    {
        StringBuilder builder = new StringBuilder();
        foreach (char c in name)
        {
            bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-' || c == '+' || c == '@' || c == ',';
            builder.Append(ok ? c : '_');
        }
        string result = builder.ToString().TrimStart('-');
        return result.Length == 0 ? "firmware.bin" : (result.Length > 200 ? result.Substring(result.Length - 200) : result);
    }

    private void RefreshFileViews(string selectName)
    {
        _fileList.Items.Clear();
        _fileToSend.Items.Clear();
        foreach (ServedFile file in _files)
        {
            _fileList.Items.Add(file);
            _fileToSend.Items.Add(file.Name);
        }
        int index = selectName == null ? -1 : _fileToSend.Items.IndexOf(selectName);
        if (index < 0 && _fileToSend.Items.Count > 0)
        {
            index = 0;
        }
        _fileToSend.SelectedIndex = index;
        ApplyFilesToRunningServer();
        UpdateUi();
    }

    private void ApplyFilesToRunningServer()
    {
        if (_server == null || !_server.IsRunning)
        {
            return;
        }
        _server.ClearFiles();
        foreach (ServedFile file in _files)
        {
            _server.AddFile(file.Name, file.FullPath);
        }
    }

    // ---------- the server ----------

    private void StartServerFromUi()
    {
        try
        {
            EnsureServerRunning();
        }
        catch (SequenceFailure ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Starts the server if it is not running. Throws a <see cref="SequenceFailure"/> that says what is wrong.</summary>
    private void EnsureServerRunning()
    {
        if (_server != null && _server.IsRunning)
        {
            return;
        }
        if (_files.Count == 0)
        {
            throw new SequenceFailure("Add the firmware file first.");
        }
        foreach (ServedFile file in _files)
        {
            if (!File.Exists(file.FullPath))
            {
                throw new SequenceFailure("The file " + file.FullPath + " does not exist any more.");
            }
        }
        string serverText = _serverIp.Text.Trim();
        if (!UBootFirmwareScript.IsValidIPv4(serverText))
        {
            throw new SequenceFailure("Enter the address of this computer on the network the board is connected to (for example 192.168.1.2).");
        }
        IPAddress allowed = null;
        if (_restrictClient.Checked && !string.IsNullOrWhiteSpace(_deviceIp.Text))
        {
            if (!UBootFirmwareScript.IsValidIPv4(_deviceIp.Text))
            {
                throw new SequenceFailure("The board's address is not a valid IPv4 address.");
            }
            allowed = IPAddress.Parse(_deviceIp.Text.Trim());
        }

        TftpServer server = new TftpServer(IPAddress.Parse(serverText), TftpPort) { AllowedClient = allowed };
        foreach (ServedFile file in _files)
        {
            server.AddFile(file.Name, file.FullPath);
        }
        server.Log += message => OnUi(() => Log(message));
        server.TransferProgress += transfer => OnUi(() => ShowTransferProgress(transfer));
        server.TransferEnded += transfer => OnUi(() => TransferEnded(transfer));
        try
        {
            server.Start();
        }
        catch (SocketException ex)
        {
            server.Dispose();
            throw new SequenceFailure("The TFTP server cannot listen on " + serverText + ", UDP port " + TftpPort + ": " + ex.Message
                + "\r\n\r\nAnother TFTP server (Tftpd64, tftpd-hpa, ...) may be running, or this is not an address of this computer.");
        }
        _server = server;
        UpdateUi();
    }

    private void StopServer()
    {
        TftpServer server = _server;
        _server = null;
        if (server != null)
        {
            try
            {
                server.Dispose();
            }
            catch (Exception)
            {
            }
        }
        if (!IsDisposed)
        {
            UpdateUi();
        }
    }

    private void ShowTransferProgress(TftpTransfer transfer)
    {
        if (transfer.Length > 0)
        {
            _progress.Value = (int)Math.Min(100, transfer.BytesAcknowledged * 100 / transfer.Length);
        }
        SetStep("Sending " + transfer.FileName + ": " + StringUtils.FormatSize(transfer.BytesAcknowledged) + "B of " + StringUtils.FormatSize(transfer.Length) + "B");
    }

    private void TransferEnded(TftpTransfer transfer)
    {
        if (transfer.Succeeded)
        {
            _progress.Value = 100;
            _completedTransfers[transfer.FileName] = CompletedCount(transfer.FileName) + 1;
        }
        else
        {
            _progress.Value = 0;
        }
    }

    private int CompletedCount(string name)
    {
        return _completedTransfers.TryGetValue(name, out int count) ? count : 0;
    }

    // ---------- the commands ----------

    private string PromptText()
    {
        string text = _prompt.Text.Trim();
        return text == AutoPrompt ? string.Empty : text;
    }

    private UBootFirmwareSettings ReadSettings()
    {
        return new UBootFirmwareSettings
        {
            ServerIp = _serverIp.Text.Trim(),
            DeviceIp = _deviceIp.Text.Trim(),
            LoadAddress = _loadAddress.Text.Trim(),
            FileName = _fileToSend.SelectedItem?.ToString(),
            AfterCommands = _after.Text
        };
    }

    private void CancelSend()
    {
        _cts?.Cancel();
    }

    private async Task SendToBoardAsync()
    {
        if (_busy)
        {
            return;
        }
        try
        {
            await RunSequenceAsync();
        }
        catch (OperationCanceledException)
        {
            if (_commandInFlight)
            {
                _terminal.SendConsoleBreak(); // a download or a long command is still running on the board
            }
            Log("Cancelled.");
            SetStep("Cancelled.");
        }
        catch (SequenceFailure ex)
        {
            Log("Stopped: " + ex.Message);
            SetStep("Stopped: " + ex.Message);
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            Log("Error: " + ex.Message);
            SetStep("Error: " + ex.Message);
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _commandInFlight = false;
            _busy = false;
            _cts?.Dispose();
            _cts = null;
            if (!IsDisposed)
            {
                UpdateUi();
            }
        }
    }

    private async Task RunSequenceAsync()
    {
        UBootFirmwareSettings settings = ReadSettings();
        if (settings.FileName == null)
        {
            MessageBox.Show(this, "Add the firmware file and choose the one to download.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        string problem = UBootFirmwareScript.Validate(settings);
        if (problem != null)
        {
            MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!_terminal.IsDeviceConnected)
        {
            MessageBox.Show(this, "The terminal is not connected to the board.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_terminal.IsLineOwnedByTransfer)
        {
            MessageBox.Show(this, "A file transfer is using the connection. Wait for it to finish or cancel it first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        EnsureServerRunning();

        ServedFile served = _files.Find(f => f.Name == settings.FileName);
        long fileLength = served != null && File.Exists(served.FullPath) ? new FileInfo(served.FullPath).Length : 0;
        List<UBootStep> steps = UBootFirmwareScript.Build(settings);
        if (!ConfirmSteps(steps))
        {
            return;
        }

        _busy = true;
        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        _progress.Value = 0;
        UpdateUi();
        string prompt = PromptText();

        SetStep("Waiting for the U-Boot prompt...");
        Log("Checking that the board is at its prompt...");
        _watcher.Reset();
        SendLine(string.Empty);
        if (await WaitForPromptAsync(PromptTimeoutMs, prompt, token) == null)
        {
            throw new SequenceFailure("The board does not show a U-Boot prompt. Stop the autoboot (press a key while it counts down), "
                + "make sure the prompt is the last thing on the terminal, and try again. If the prompt is unusual, type its ending in \"Prompt ends with\".");
        }

        for (int i = 0; i < steps.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            UBootStep step = steps[i];
            SetStep("Step " + (i + 1) + " of " + steps.Count + ": " + step.Command);
            Log("> " + step.Command);

            _watcher.Reset();
            int completedBefore = CompletedCount(settings.FileName);
            SendLine(step.Command);

            if (UBootFirmwareScript.NeverReturns(step.Command))
            {
                Log("The board carries on by itself now; U-Boot's prompt does not come back.");
                SetStep("Done: " + step.Command + " was sent.");
                return;
            }

            _commandInFlight = true;
            int timeoutMs = TimeoutFor(step, fileLength);
            string output = await WaitForPromptAsync(timeoutMs, prompt, token);
            _commandInFlight = false;
            if (output == null)
            {
                _terminal.SendConsoleBreak();
                throw new SequenceFailure("The board did not come back to the prompt after \"" + step.Command + "\" within " + (timeoutMs / 1000) + " s. Ctrl+C was sent.");
            }
            string body = StripEcho(output);

            if (step.Kind == UBootStepKind.Download)
            {
                bool confirmed = CompletedCount(settings.FileName) > completedBefore || UBootFirmwareScript.DownloadReported(body);
                for (int wait = 0; !confirmed && wait < 15; wait++)
                {
                    await Task.Delay(100, token); // the server's report may still be on its way to this thread
                    confirmed = CompletedCount(settings.FileName) > completedBefore;
                }
                if (!confirmed)
                {
                    string why = UBootFirmwareScript.FindDownloadProblem(body) ?? "The board did not report the download as complete.";
                    throw new SequenceFailure("The download failed: " + why
                        + "\r\n\r\nCheck the cable, the addresses and that nothing (a firewall, another TFTP server) blocks UDP port " + TftpPort + ".");
                }
                Log("Download finished.");
            }
            else
            {
                string failure = UBootFirmwareScript.FindCommandProblem(body);
                if (failure != null)
                {
                    throw new SequenceFailure("\"" + step.Command + "\" reported a problem: " + failure + "\r\n\r\nThe remaining commands were not sent.");
                }
            }
        }

        SetStep("Done: all " + steps.Count + " commands finished.");
        Log("Finished.");
    }

    private bool ConfirmSteps(List<UBootStep> steps)
    {
        bool hasAfter = false;
        bool destructive = false;
        StringBuilder text = new StringBuilder("These commands will be typed on the board, one after the other:\r\n\r\n");
        foreach (UBootStep step in steps)
        {
            text.Append("    ").Append(step.Command).Append("\r\n");
            if (step.Kind == UBootStepKind.After)
            {
                hasAfter = true;
                destructive |= UBootFirmwareScript.IsDestructive(step.Command);
            }
        }
        if (!hasAfter)
        {
            return true; // only the download: nothing on the board is changed
        }
        if (destructive)
        {
            text.Append("\r\nSome of them change what is stored on the board. A wrong address or offset can make it unable to start.");
        }
        return MessageBox.Show(this, text.ToString(), Text, MessageBoxButtons.OKCancel, destructive ? MessageBoxIcon.Warning : MessageBoxIcon.Question) == DialogResult.OK;
    }

    private static int TimeoutFor(UBootStep step, long fileLength)
    {
        switch (step.Kind)
        {
            case UBootStepKind.Setup:
                return 5000;
            case UBootStepKind.Download:
                // A minute, and more for a big file as if it came at 20 KB/s, which no working link does; at most half an hour.
                return 60000 + (int)Math.Min(1740000L, fileLength / 20);
            default:
                return 600000; // erasing and writing a flash chip takes a while
        }
    }

    private void SendLine(string line)
    {
        if (!_terminal.SendConsoleLine(line))
        {
            throw new SequenceFailure("The command could not be sent: the connection to the board is closed, or a file transfer is using it.");
        }
    }

    /// <summary>Waits for the board's prompt. Returns what the board printed meanwhile, or null when the time ran out.</summary>
    private async Task<string> WaitForPromptAsync(int timeoutMs, string prompt, CancellationToken token)
    {
        int start = Environment.TickCount;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (_watcher.IsPromptReady(prompt, Environment.TickCount, QuietMs))
            {
                return _watcher.Text;
            }
            if (unchecked(Environment.TickCount - start) > timeoutMs)
            {
                return null;
            }
            if (!_terminal.IsDeviceConnected)
            {
                throw new SequenceFailure("The connection to the board was closed.");
            }
            await Task.Delay(40, token);
        }
    }

    /// <summary>The board echoes the command it is given; its first line is that echo, not an answer to look for errors in.</summary>
    private static string StripEcho(string output)
    {
        int index = output.IndexOfAny(new[] { '\r', '\n' });
        return index < 0 ? string.Empty : output.Substring(index);
    }

    // ---------- the command list ----------

    private void PresetChanged()
    {
        if (_updatingPreset || !(_preset.SelectedItem is Preset preset) || preset.Commands == null)
        {
            return;
        }
        _updatingPreset = true;
        try
        {
            _after.Text = preset.Commands.Replace("\n", "\r\n");
        }
        finally
        {
            _updatingPreset = false;
        }
    }

    private void AfterTextChanged()
    {
        if (_updatingPreset)
        {
            return;
        }
        // Typing in the box makes it a custom list, unless it still equals a preset.
        string normalized = _after.Text.Replace("\r\n", "\n");
        Preset match = null;
        foreach (Preset preset in Presets)
        {
            if (preset.Commands != null && preset.Commands == normalized)
            {
                match = preset;
                break;
            }
        }
        _updatingPreset = true;
        try
        {
            _preset.SelectedItem = match ?? Presets[Presets.Length - 1];
        }
        finally
        {
            _updatingPreset = false;
        }
    }

    // ---------- remembered settings ----------

    private static List<string> LocalAddresses()
    {
        List<string> list = new List<string>();
        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }
                foreach (UnicastIPAddressInformation address in nic.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        list.Add(address.Address.ToString());
                    }
                }
            }
        }
        catch (Exception)
        {
        }
        return list;
    }

    private void LoadSettings()
    {
        string savedServer = null;
        string savedDevice = string.Empty;
        string savedLoad = "${loadaddr}";
        string savedPrompt = AutoPrompt;
        string savedAfter = string.Empty;
        string savedFileToSend = null;
        bool restrict = true;
        try
        {
            using RegistryKey root = AppEnvironment.OpenSettingsKey();
            using RegistryKey key = root.OpenSubKey(RegistrySubKey);
            if (key != null)
            {
                if (key.GetValue("Files") is string[] entries)
                {
                    foreach (string entry in entries)
                    {
                        int tab = entry.IndexOf('\t');
                        if (tab > 0 && File.Exists(entry.Substring(tab + 1)))
                        {
                            _files.Add(new ServedFile { Name = entry.Substring(0, tab), FullPath = entry.Substring(tab + 1) });
                        }
                    }
                }
                savedServer = key.GetValue("ServerIp") as string;
                savedDevice = key.GetValue("DeviceIp") as string ?? savedDevice;
                savedLoad = key.GetValue("LoadAddress") as string ?? savedLoad;
                savedPrompt = key.GetValue("Prompt") as string ?? savedPrompt;
                savedAfter = key.GetValue("After") as string ?? savedAfter;
                savedFileToSend = key.GetValue("FileToSend") as string;
                restrict = !(key.GetValue("RestrictClient") is int flag) || flag != 0;
            }
        }
        catch (Exception)
        {
            // Nothing remembered: the defaults do.
        }

        List<string> local = LocalAddresses();
        foreach (string address in local)
        {
            _serverIp.Items.Add(address);
        }
        _serverIp.Text = savedServer != null && local.Contains(savedServer) ? savedServer : (local.Count > 0 ? local[0] : savedServer ?? string.Empty);
        _deviceIp.Text = savedDevice;
        _restrictClient.Checked = restrict;
        _loadAddress.Text = savedLoad;
        _prompt.Text = string.IsNullOrEmpty(savedPrompt) ? AutoPrompt : savedPrompt;
        _after.Text = savedAfter.Replace("\r\n", "\n").Replace("\n", "\r\n");
        AfterTextChanged();
        RefreshFileViews(savedFileToSend);
    }

    private void SaveSettings()
    {
        try
        {
            using RegistryKey root = AppEnvironment.OpenSettingsKey();
            using RegistryKey key = root.CreateSubKey(RegistrySubKey);
            string[] entries = new string[_files.Count];
            for (int i = 0; i < entries.Length; i++)
            {
                entries[i] = _files[i].Name + "\t" + _files[i].FullPath;
            }
            key.SetValue("Files", entries, RegistryValueKind.MultiString);
            key.SetValue("FileToSend", _fileToSend.SelectedItem?.ToString() ?? string.Empty);
            key.SetValue("ServerIp", _serverIp.Text.Trim());
            key.SetValue("DeviceIp", _deviceIp.Text.Trim());
            key.SetValue("LoadAddress", _loadAddress.Text.Trim());
            key.SetValue("Prompt", _prompt.Text.Trim());
            key.SetValue("After", _after.Text);
            key.SetValue("RestrictClient", _restrictClient.Checked ? 1 : 0);
        }
        catch (Exception)
        {
            // Settings that cannot be saved are only forgotten.
        }
    }
}
