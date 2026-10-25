using Microsoft.Win32;
using SmarTTY.Controls;
using SmarTTY.IO;
using SmarTTY.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace SmarTTY.GUI;

/// <summary>
/// Tools -&gt; TFTP server / U-Boot upload. Runs a TFTP server for the firmware file and, on request, types the U-Boot
/// commands on the terminal's COM (or TCP) connection that make the board download it: it sets the network variables,
/// runs tftpboot, waits for the prompt after every command, checks that the download really finished, and only then
/// runs the commands the user chose for the image (boot it, write it to flash, ...). The server runs only while the
/// window is open.
/// </summary>
public partial class TftpFirmwareWindow : Window, ITftpFirmwareWindow
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
    private TftpTransfer _currentTransfer; // the transfer the progress bar shows: the latest one that started
    private readonly Dictionary<string, int> _completedTransfers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    private IDisposable _consoleListener;
    private TftpServer _server;
    private CancellationTokenSource _cts;
    private bool _busy;
    private bool _commandInFlight;
    private bool _updatingPreset;
    private bool _closingWithTerminal;
    private bool _closed;

    public TftpFirmwareWindow(DeviceTerminalControl terminal, string terminalTitle)
    {
        _terminal = terminal;
        InitializeComponent();
        Title = "TFTP server / U-Boot upload - " + terminalTitle;
        PromptBox.ItemsSource = new[] { AutoPrompt, "=>", ">", "#" };
        PresetBox.ItemsSource = Presets;
        AfterBox.FontFamily = new System.Windows.Media.FontFamily("Consolas, Courier New");

        LoadSettings();
        _consoleListener = _terminal.ListenToConsole(text => _watcher.Append(text, Environment.TickCount));
        UpdateUi();
    }

    /// <summary>True while the window is open.</summary>
    public bool IsOpen => !_closed;

    // Show and Activate are implemented explicitly so they do not hide the identically named Window members.
    void ITftpFirmwareWindow.Show()
    {
        WindowOwnership.Attach(this, WindowOwnership.ActiveWindow());
        Show();
    }

    void ITftpFirmwareWindow.Activate()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
    }

    /// <summary>Closes the window because the terminal it belongs to is gone: nobody is left to ask.</summary>
    public void CloseWithTerminal()
    {
        _closingWithTerminal = true;
        Close();
    }

    // ---------- the window ----------

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        // The WinForms window had MaximizeBox off; a maximized window here would cover the terminal it types into.
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_busy && !_closingWithTerminal)
        {
            if (!MessageBoxes.AskWarn("The board is being sent commands. Stop this and close the window?"))
            {
                e.Cancel = true;
                return;
            }
        }
        if (_busy)
        {
            _cts?.Cancel();
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        SaveSettings();
        StopServer();
        _consoleListener?.Dispose();
        _consoleListener = null;
        base.OnClosed(e);
    }

    private void UpdateUi()
    {
        bool running = _server != null && _server.IsRunning;
        StartServerButton.IsEnabled = !running && !_busy;
        StopServerButton.IsEnabled = running && !_busy;
        SendButton.IsEnabled = !_busy;
        CancelButton.IsEnabled = _busy;
        ServerIpBox.IsEnabled = !running && !_busy;
        DeviceIpBox.IsEnabled = !running && !_busy;
        RestrictClientBox.IsEnabled = !running && !_busy;
        LoadAddressBox.IsEnabled = !_busy;
        PromptBox.IsEnabled = !_busy;
        PresetBox.IsEnabled = !_busy;
        AfterBox.IsEnabled = !_busy;
        AddFileButton.IsEnabled = !_busy;
        RemoveFileButton.IsEnabled = !_busy && FileList.SelectedIndex >= 0;
        FileToSendBox.IsEnabled = !_busy && _files.Count > 0;
        ServerStatusText.Text = running ? "Listening on " + _server.LocalEndPoint + ", only while this window is open." : "Server stopped.";
    }

    private void Log(string message)
    {
        if (_closed)
        {
            return;
        }
        if (LogBox.Text.Length > 100000)
        {
            LogBox.Text = LogBox.Text.Substring(LogBox.Text.Length - 50000);
            LogBox.CaretIndex = LogBox.Text.Length;
        }
        LogBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + "\r\n");
        LogBox.ScrollToEnd();
    }

    private void SetStep(string text)
    {
        if (!_closed)
        {
            StepText.Text = text;
        }
    }

    /// <summary>Runs <paramref name="action"/> on the window's thread; the server's events come on its own threads.</summary>
    private void OnUi(Action action)
    {
        if (_closed || !IsLoaded)
        {
            return;
        }
        Dispatcher.BeginInvoke(action);
    }

    // ---------- files ----------

    private void AddFile_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new OpenFileDialog
        {
            Title = "Select the firmware (or any file the board should fetch over TFTP)",
            Filter = "All files (*.*)|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true)
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

    private void RemoveFile_Click(object sender, RoutedEventArgs e)
    {
        int index = FileList.SelectedIndex;
        if (index < 0 || index >= _files.Count)
        {
            return;
        }
        string keep = FileToSendBox.SelectedItem?.ToString();
        _files.RemoveAt(index);
        RefreshFileViews(keep);
    }

    private void FileList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateUi();
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
        FileList.Items.Clear();
        foreach (ServedFile file in _files)
        {
            FileList.Items.Add(file);
        }
        List<string> names = new List<string>();
        foreach (ServedFile file in _files)
        {
            names.Add(file.Name);
        }
        FileToSendBox.ItemsSource = names;
        int index = selectName == null ? -1 : names.IndexOf(selectName);
        if (index < 0 && names.Count > 0)
        {
            index = 0;
        }
        FileToSendBox.SelectedIndex = index;
        ApplyFilesToRunningServer();
        UpdateUi();
    }

    private void ApplyFilesToRunningServer()
    {
        if (_server == null || !_server.IsRunning)
        {
            return;
        }
        // In one step: a request from the board between a clearing and the adding would get "File not found", and U-Boot
        // does not ask again after that.
        List<KeyValuePair<string, string>> served = new List<KeyValuePair<string, string>>();
        foreach (ServedFile file in _files)
        {
            served.Add(new KeyValuePair<string, string>(file.Name, file.FullPath));
        }
        _server.SetFiles(served);
    }

    // ---------- the server ----------

    private void StartServer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            EnsureServerRunning();
        }
        catch (SequenceFailure ex)
        {
            MessageBoxes.Warning(ex.Message);
        }
    }

    private void StopServer_Click(object sender, RoutedEventArgs e)
    {
        StopServer();
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
        string serverText = ServerIpBox.Text.Trim();
        if (!UBootFirmwareScript.IsValidIPv4(serverText))
        {
            throw new SequenceFailure("Enter the address of this computer on the network the board is connected to (for example 192.168.1.2).");
        }
        IPAddress allowed = null;
        if (RestrictClientBox.IsChecked == true && !string.IsNullOrWhiteSpace(DeviceIpBox.Text))
        {
            if (!UBootFirmwareScript.IsValidIPv4(DeviceIpBox.Text))
            {
                throw new SequenceFailure("The board's address is not a valid IPv4 address.");
            }
            allowed = IPAddress.Parse(DeviceIpBox.Text.Trim());
        }

        TftpServer server = new TftpServer(IPAddress.Parse(serverText), TftpPort) { AllowedClient = allowed };
        foreach (ServedFile file in _files)
        {
            server.AddFile(file.Name, file.FullPath);
        }
        server.Log += message => OnUi(() => Log(message));
        _currentTransfer = null;
        server.TransferStarted += transfer => OnUi(() => _currentTransfer = transfer);
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
        if (!_closed)
        {
            UpdateUi();
        }
    }

    // A transfer that a newer one of the same client replaced (the board started over) still reports its end; the bar
    // is the new transfer's by then.
    private bool IsReplaced(TftpTransfer transfer)
    {
        return _currentTransfer != null && !ReferenceEquals(_currentTransfer, transfer);
    }

    private void ShowTransferProgress(TftpTransfer transfer)
    {
        if (IsReplaced(transfer))
        {
            return;
        }
        if (transfer.Length > 0)
        {
            Progress.Value = (int)Math.Min(100, transfer.BytesAcknowledged * 100 / transfer.Length);
        }
        SetStep("Sending " + transfer.FileName + ": " + StringUtils.FormatSize(transfer.BytesAcknowledged) + "B of " + StringUtils.FormatSize(transfer.Length) + "B");
    }

    private void TransferEnded(TftpTransfer transfer)
    {
        if (transfer.Succeeded)
        {
            Progress.Value = 100;
            _completedTransfers[transfer.FileName] = CompletedCount(transfer.FileName) + 1;
        }
        else if (!IsReplaced(transfer))
        {
            Progress.Value = 0;
        }
    }

    private int CompletedCount(string name)
    {
        return _completedTransfers.TryGetValue(name, out int count) ? count : 0;
    }

    // ---------- the commands ----------

    private string PromptText()
    {
        string text = PromptBox.Text.Trim();
        return text == AutoPrompt ? string.Empty : text;
    }

    private UBootFirmwareSettings ReadSettings()
    {
        return new UBootFirmwareSettings
        {
            ServerIp = ServerIpBox.Text.Trim(),
            DeviceIp = DeviceIpBox.Text.Trim(),
            LoadAddress = LoadAddressBox.Text.Trim(),
            FileName = FileToSendBox.SelectedItem?.ToString(),
            AfterCommands = AfterBox.Text
        };
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
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
            MessageBoxes.Warning(ex.Message);
        }
        catch (Exception ex)
        {
            Log("Error: " + ex.Message);
            SetStep("Error: " + ex.Message);
            MessageBoxes.Error(ex.Message);
        }
        finally
        {
            _commandInFlight = false;
            _busy = false;
            _cts?.Dispose();
            _cts = null;
            if (!_closed)
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
            MessageBoxes.Info("Add the firmware file and choose the one to download.");
            return;
        }
        string problem = UBootFirmwareScript.Validate(settings);
        if (problem != null)
        {
            MessageBoxes.Warning(problem);
            return;
        }
        if (!_terminal.IsDeviceConnected)
        {
            MessageBoxes.Warning("The terminal is not connected to the board.");
            return;
        }
        if (_terminal.IsLineOwnedByTransfer)
        {
            MessageBoxes.Info("A file transfer is using the connection. Wait for it to finish or cancel it first.");
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
        Progress.Value = 0;
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
        return destructive
            ? MessageDialog.Show(text.ToString(), "SmarTTY", MessageDialogIcon.Warning, MessageDialogButtons.OkCancel) == MessageBoxResult.OK
            : MessageDialog.Show(text.ToString(), "SmarTTY", MessageDialogIcon.Question, MessageDialogButtons.OkCancel) == MessageBoxResult.OK;
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

    private void Preset_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_updatingPreset || !(PresetBox.SelectedItem is Preset preset) || preset.Commands == null)
        {
            return;
        }
        _updatingPreset = true;
        try
        {
            AfterBox.Text = preset.Commands.Replace("\n", "\r\n");
        }
        finally
        {
            _updatingPreset = false;
        }
    }

    private void After_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_updatingPreset)
        {
            return;
        }
        // Typing in the box makes it a custom list, unless it still equals a preset.
        string normalized = AfterBox.Text.Replace("\r\n", "\n");
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
            PresetBox.SelectedItem = match ?? Presets[Presets.Length - 1];
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
            using (RegistryKey root = AppEnvironment.OpenSettingsKey())
            using (RegistryKey key = root.OpenSubKey(RegistrySubKey))
            {
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
        }
        catch (Exception)
        {
            // Nothing remembered: the defaults do.
        }

        List<string> local = LocalAddresses();
        ServerIpBox.ItemsSource = local;
        ServerIpBox.Text = savedServer != null && local.Contains(savedServer) ? savedServer : (local.Count > 0 ? local[0] : savedServer ?? string.Empty);
        DeviceIpBox.Text = savedDevice;
        RestrictClientBox.IsChecked = restrict;
        LoadAddressBox.Text = savedLoad;
        PromptBox.Text = string.IsNullOrEmpty(savedPrompt) ? AutoPrompt : savedPrompt;
        AfterBox.Text = savedAfter.Replace("\r\n", "\n").Replace("\n", "\r\n");
        After_TextChanged(null, null);
        RefreshFileViews(savedFileToSend);
    }

    private void SaveSettings()
    {
        try
        {
            using (RegistryKey root = AppEnvironment.OpenSettingsKey())
            using (RegistryKey key = root.CreateSubKey(RegistrySubKey))
            {
                string[] entries = new string[_files.Count];
                for (int i = 0; i < entries.Length; i++)
                {
                    entries[i] = _files[i].Name + "\t" + _files[i].FullPath;
                }
                key.SetValue("Files", entries, RegistryValueKind.MultiString);
                key.SetValue("FileToSend", FileToSendBox.SelectedItem?.ToString() ?? string.Empty);
                key.SetValue("ServerIp", ServerIpBox.Text.Trim());
                key.SetValue("DeviceIp", DeviceIpBox.Text.Trim());
                key.SetValue("LoadAddress", LoadAddressBox.Text.Trim());
                key.SetValue("Prompt", PromptBox.Text.Trim());
                key.SetValue("After", AfterBox.Text);
                key.SetValue("RestrictClient", RestrictClientBox.IsChecked == true ? 1 : 0);
            }
        }
        catch (Exception)
        {
            // Settings that cannot be saved are only forgotten.
        }
    }
}
