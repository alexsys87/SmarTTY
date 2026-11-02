using SmarTTY.GUI.Backend;
using SmarTTY.Ssh;
using SmarTTY.Transport;
using SmarTTY.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace SmarTTY.GUI;

/// <summary>
/// Tools > Backup to tar.gz: a tree of the files and folders of the server with check boxes, and the backup of the checked ones (by
/// default the whole root file system without the virtual and memory file systems) into a tar.gz archive on this computer.
/// The commands are those of <see cref="RemoteBackup"/>: tar writes the archive to the standard output of an SSH exec channel, which
/// is copied into the local file as it arrives, so the server needs no free space at all. The window reads the mount table first,
/// so that /proc, /sys and the other virtual file systems of the kernel are never archived, wherever they are mounted.
/// </summary>
public partial class BackupWindow : Window
{
    private static readonly Dictionary<SessionWrapper, BackupWindow> _Open = new Dictionary<SessionWrapper, BackupWindow>();

    /// <summary>The folder of the last archive, offered for the next one (for the time the program runs).</summary>
    private static string _LastFolder;

    private readonly SessionWrapper _Session;

    private readonly BackupNode _Root = BackupNode.CreateRoot();

    private readonly DispatcherTimer _ProgressTimer;

    private RemoteBackupHostInfo _HostInfo;

    private bool _Ready;

    private bool _Running;

    private bool _Closed;

    private bool _FileNameEdited;

    private bool _SettingFileName;

    private long? _EstimatedBytes;

    private string _SavedFile;

    // The state of the running backup, written by the copying and the stderr threads and shown by the timer.
    private volatile bool _CancelRequested;

    private SshShellChannel _Channel;

    private long _ReceivedBytes;

    private long _Members;

    private string _CurrentMember;

    private Stopwatch _Clock;

    private bool _CompressingLocally;

    private string _Phase;

    /// <summary>Opens the window for the session, or brings the one that is open to the front.</summary>
    public static void ShowFor(SessionWrapper session, Window owner)
    {
        if (_Open.TryGetValue(session, out BackupWindow open))
        {
            open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }
        BackupWindow window = new BackupWindow(session) { Owner = owner };
        _Open[session] = window;
        window.Show();
    }

    private BackupWindow(SessionWrapper session)
    {
        InitializeComponent();
        _Session = session;
        Title = "Backup to tar.gz - " + (session.Title ?? "SSH");
        _Root.LoadHandler = LoadChildren;
        _Root.SelectionChanged += OnSelectionChanged;
        FolderTree.ItemsSource = new[] { _Root };
        FolderTree.IsEnabled = false;
        FileBox.TextChanged += (sender, e) =>
        {
            if (!_SettingFileName)
            {
                _FileNameEdited = true;
            }
        };
        _ProgressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _ProgressTimer.Tick += (sender, e) => ShowProgress();
        Closing += Window_Closing;
        Closed += (sender, e) =>
        {
            _Closed = true;
            _ProgressTimer.Stop();
            _Open.Remove(_Session);
        };
        Loaded += (sender, e) => StartReadingHost();
    }

    #region The tree

    /// <summary>Asks the server who the user is, what is mounted where and what the root holds, in the background.</summary>
    private void StartReadingHost()
    {
        SshSession session = _Session.Session;
        if (session == null)
        {
            ShowLoadingError("The SSH session is not connected.");
            return;
        }
        Task.Run(() =>
        {
            string error = null;
            RemoteBackupHostInfo info = null;
            List<RemoteEntry> entries = null;
            try
            {
                info = RemoteBackup.ParseHostInfo(session.RunCommand(PosixShell.Wrap(RemoteBackup.HostInfoCommand, addAdminDirectoriesToPath: true), out _, timeout: TimeSpan.FromSeconds(30)));
                entries = ReadDirectory(session, "/", out error);
            }
            catch (Exception exception)
            {
                error = exception.Message;
            }
            Dispatcher.BeginInvoke((Action)(() =>
            {
                if (_Closed)
                {
                    return;
                }
                if (entries == null)
                {
                    ShowLoadingError("Could not read the root folder of the server: " + error);
                    return;
                }
                _HostInfo = info;
                _Root.ApplyChildren(MakeSpecs("/", entries));
                _Root.IsExpanded = true;
                LoadingPanel.Visibility = Visibility.Collapsed;
                FolderTree.IsEnabled = true;
                _Ready = true;
                ShowUser();
                OnSelectionChanged();
                UpdateButtons();
            }));
        });
    }

    private void ShowLoadingError(string message)
    {
        LoadingText.Text = message;
        LoadingPanel.Children.OfType<System.Windows.Controls.ProgressBar>().ToList().ForEach(bar => bar.Visibility = Visibility.Collapsed);
        StatusText.Text = message;
    }

    /// <summary>The entries of a folder of the server, or null with the reason when it cannot be read.</summary>
    private static List<RemoteEntry> ReadDirectory(SshSession session, string path, out string error)
    {
        error = null;
        List<string> lines = session.RunCommand(PosixShell.Wrap(RemoteBackup.ListCommand(path)), out int? exitStatus, timeout: TimeSpan.FromSeconds(60));
        if (lines == null)
        {
            session.ThrowIfConnectionLost();
            error = "the command could not be run";
            return null;
        }
        if (exitStatus == 2)
        {
            error = "cannot be opened (permission denied?)";
            return null;
        }
        return RemoteBackup.ParseListing(lines);
    }

    private List<BackupNode.ChildSpec> MakeSpecs(string directory, List<RemoteEntry> entries)
    {
        List<BackupNode.ChildSpec> specs = new List<BackupNode.ChildSpec>();
        foreach (RemoteEntry entry in entries)
        {
            string path = RemoteBackup.Combine(directory, entry.Name);
            BackupMountRole role = RemoteBackup.Classify(path, _HostInfo, out string note);
            if (entry.Kind == RemoteEntryKind.SymbolicLink && role == BackupMountRole.NeverArchived)
            {
                // A link named like /sys is just a link: tar stores it without following it.
                role = BackupMountRole.Ordinary;
                note = null;
            }
            specs.Add(new BackupNode.ChildSpec { Entry = entry, Role = role, Note = note });
        }
        return specs;
    }

    /// <summary>Reads the children of a folder the first time it is expanded.</summary>
    private void LoadChildren(BackupNode node)
    {
        SshSession session = _Session.Session;
        if (session == null || node.ChildrenLoaded || node.IsLoading)
        {
            return;
        }
        node.IsLoading = true;
        Task.Run(() =>
        {
            string error;
            List<RemoteEntry> entries;
            try
            {
                entries = ReadDirectory(session, node.FullPath, out error);
            }
            catch (Exception exception)
            {
                entries = null;
                error = exception.Message;
            }
            Dispatcher.BeginInvoke((Action)(() =>
            {
                if (entries == null)
                {
                    node.SetLoadFailed(error);
                }
                else
                {
                    node.ApplyChildren(MakeSpecs(node.FullPath, entries));
                }
            }));
        });
    }

    /// <summary>Reads a folder now, on the calling pool thread, and applies the children on the UI thread (before the archive is made).</summary>
    private void LoadChildrenNow(SshSession session, BackupNode node)
    {
        List<RemoteEntry> entries = ReadDirectory(session, node.FullPath, out string error);
        Dispatcher.Invoke(() =>
        {
            if (entries == null)
            {
                node.SetLoadFailed(error);
            }
            else if (!node.ChildrenLoaded)
            {
                node.ApplyChildren(MakeSpecs(node.FullPath, entries));
            }
        });
    }

    private void ShowUser()
    {
        RemoteBackupHostInfo info = _HostInfo;
        if (info?.IsRoot == true)
        {
            UserText.Text = "Logged in as root: every file can be read.";
            SudoBox.Visibility = Visibility.Collapsed;
            return;
        }
        if (info?.CanSudo == true)
        {
            SudoBox.Visibility = Visibility.Visible;
            SudoBox.IsChecked = true;
            UserText.Text = "The user of this connection is not root; passwordless sudo is available.";
            return;
        }
        SudoBox.Visibility = Visibility.Collapsed;
        UserText.Text = info?.IsRoot == false
            ? "The user of this connection is not root and has no passwordless sudo: the files it cannot read are left out (tar names them in the log). Connect as root for a complete backup."
            : "";
    }

    private bool UseSudo => SudoBox.Visibility == Visibility.Visible && SudoBox.IsChecked == true;

    private void OnSelectionChanged()
    {
        if (!_Ready)
        {
            return;
        }
        _EstimatedBytes = null;
        EstimateText.Text = "";
        if (!_FileNameEdited)
        {
            SetSuggestedFileName();
        }
        UpdateButtons();
    }

    private void SetSuggestedFileName()
    {
        string folder = Directory.Exists(_LastFolder) ? _LastFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        _SettingFileName = true;
        FileBox.Text = Path.Combine(folder, RemoteBackup.DefaultFileName(_Session.Title, _Root.IsWholeSystemSelected(), DateTime.Now));
        _SettingFileName = false;
    }

    private void WholeSystem_Click(object sender, RoutedEventArgs e)
    {
        _Root.SelectWholeSystem();
    }

    private void ConfigOnly_Click(object sender, RoutedEventArgs e)
    {
        _Root.SetChecked(false);
        BackupNode etc = _Root.Find("/etc");
        if (etc == null)
        {
            StatusText.Text = "The server has no /etc folder.";
        }
        else
        {
            etc.SetChecked(true);
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _Root.SetChecked(false);
    }

    private void Estimate_Click(object sender, RoutedEventArgs e)
    {
        SshSession session = _Session.Session;
        List<string> members = _Root.CollectMembers();
        if (session == null || members.Count == 0)
        {
            EstimateText.Text = members.Count == 0 ? "nothing is checked" : "not connected";
            return;
        }
        EstimateButton.IsEnabled = false;
        EstimateText.Text = "counting (du)...";
        string command = RemoteBackup.BuildSizeCommand(members, UseSudo);
        Task.Run(() =>
        {
            long? bytes = null;
            try
            {
                bytes = RemoteBackup.ParseSizeOutput(session.RunCommand(command, out _, timeout: TimeSpan.FromMinutes(10)));
            }
            catch
            {
            }
            Dispatcher.BeginInvoke((Action)(() =>
            {
                if (_Closed)
                {
                    return;
                }
                EstimateButton.IsEnabled = !_Running;
                _EstimatedBytes = bytes;
                EstimateText.Text = bytes.HasValue ? "about " + FormatBytes(bytes.Value) + " before compression" : "du did not answer";
            }));
        });
    }

    #endregion

    #region The backup

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        string current = FileBox.Text.Trim();
        string folder = null;
        try
        {
            folder = Path.GetDirectoryName(current);
        }
        catch (ArgumentException)
        {
        }
        string chosen = LocalFileDialogWindow.SaveFile(this, "Save the backup as", "tar.gz archives (*.tar.gz)|*.tar.gz|All files (*.*)|*.*",
            fileName: SafeFileName(current), overwritePrompt: false, addExtension: true, initialDirectory: Directory.Exists(folder) ? folder : null);
        if (chosen != null)
        {
            FileBox.Text = chosen;
        }
    }

    private static string SafeFileName(string path)
    {
        try
        {
            return Path.GetFileName(path);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        SshSession session = _Session.Session;
        if (session == null || session.IsConnectionClosed)
        {
            StatusText.Text = "The SSH session is not connected.";
            return;
        }
        string target = FileBox.Text.Trim();
        string error = CheckTarget(target);
        if (error != null)
        {
            MessageBoxes.Error(error);
            return;
        }
        if (File.Exists(target) && !MessageBoxes.Ask(target + " exists already. Replace it?"))
        {
            return;
        }
        if (_Root.CollectMembers().Count == 0)
        {
            MessageBoxes.Error("Nothing is checked: check the folders to archive, or press \"Whole system\".");
            return;
        }
        _LastFolder = Path.GetDirectoryName(target);
        bool compressOnServer = CompressionBox.SelectedIndex == 0;
        bool useSudo = UseSudo;
        bool verify = VerifyBox.IsChecked == true;
        _SavedFile = null;
        _CancelRequested = false;
        _ReceivedBytes = 0;
        _Members = 0;
        _CurrentMember = null;
        _Phase = "Preparing...";
        LogBox.Clear();
        SetRunning(true);
        Task.Run(() => RunBackup(session, target, compressOnServer, useSudo, verify));
    }

    /// <summary>Null when the archive can be written there, otherwise what is wrong.</summary>
    private static string CheckTarget(string target)
    {
        if (target.Length == 0)
        {
            return "Enter the file to save the archive to.";
        }
        try
        {
            string folder = Path.GetDirectoryName(Path.GetFullPath(target));
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                return "The folder " + folder + " does not exist.";
            }
            if (Directory.Exists(target))
            {
                return target + " is a folder: enter the name of the archive file.";
            }
        }
        catch (Exception exception) when (exception is ArgumentException || exception is NotSupportedException || exception is PathTooLongException)
        {
            return "\"" + target + "\" is not a valid file name: " + exception.Message;
        }
        return null;
    }

    private void RunBackup(SshSession session, string target, bool compressOnServer, bool useSudo, bool verify)
    {
        string partPath = target + ".part";
        string result;
        bool success = false;
        bool keepPart = false;
        try
        {
            // The virtual file systems mounted deeper inside a checked folder (/sys/fs/cgroup is under /sys, but a container or a
            // chroot may have /proc mounted anywhere) only show up as locked entries once the folders above them are read.
            List<string> locked = RemoteBackup.NeverArchivedPaths(_HostInfo);
            for (int round = 0; round < 64; round++)
            {
                List<BackupNode> folders = Dispatcher.Invoke(() => _Root.FoldersToReadBeforeArchiving(locked));
                if (folders.Count == 0)
                {
                    break;
                }
                foreach (BackupNode folder in folders)
                {
                    ThrowIfCancelled();
                    _Phase = "Reading " + folder.FullPath + "...";
                    LoadChildrenNow(session, folder);
                }
            }
            List<string> members = Dispatcher.Invoke(() => _Root.CollectMembers());
            if (members.Count == 0)
            {
                throw new IOException("Nothing is left to archive.");
            }
            Log("Archiving " + members.Count + (members.Count == 1 ? " entry" : " entries") + " of / : " + string.Join(" ", members.Take(12).Select(m => m.TrimStart('/'))) + (members.Count > 12 ? " ..." : ""));

            ArchiveRun run = RunTar(session, RemoteBackup.BuildArchiveCommand(members, compressOnServer, useSudo), partPath, compressLocally: !compressOnServer);
            if (compressOnServer && !run.Cancelled && !run.ConnectionLost && run.Received == 0 && run.ExitStatus != 0 && RemoteBackup.LooksLikeMissingGzip(run.Messages))
            {
                Log("tar on the server cannot compress (" + run.Messages.FirstOrDefault() + "): the archive is compressed on this computer instead.");
                run = RunTar(session, RemoteBackup.BuildArchiveCommand(members, compressOnServer: false, useSudo), partPath, compressLocally: true);
            }
            if (run.Cancelled)
            {
                throw new OperationCanceledException();
            }
            if (run.ConnectionLost)
            {
                throw new IOException("The connection to the server was lost: the archive is incomplete and was deleted.");
            }
            if (run.Received == 0)
            {
                string reason = run.Messages.Count > 0 ? string.Join(Environment.NewLine, run.Messages.Take(5)) : "tar printed nothing (exit status " + run.ExitStatusText + ")";
                if (useSudo && run.Messages.Any(m => m.StartsWith("sudo: ", StringComparison.Ordinal)))
                {
                    reason += Environment.NewLine + "sudo -n did not let tar run: allow passwordless sudo for this user, or uncheck sudo.";
                }
                throw new IOException("tar did not make an archive:" + Environment.NewLine + reason);
            }
            string warnings = null;
            if (run.ExitStatus != 0)
            {
                // GNU tar: 1 means that some files changed while they were read, 2 that some could not be read; BusyBox tar says 1
                // for both. The archive holds everything else, so it is kept, with the reason in the log.
                warnings = "tar finished with exit status " + run.ExitStatusText + ": " + (run.Messages.Count > 0
                    ? run.Messages.Count + (run.Messages.Count == 1 ? " message" : " messages") + " in the log (files that could not be read or that changed while being read)"
                    : "see the log");
            }

            TarCheckResult check = null;
            if (verify)
            {
                _Phase = "Checking the archive...";
                check = TarArchiveChecker.CheckGzipFile(partPath, () => _CancelRequested);
                ThrowIfCancelled();
                if (!check.IsComplete)
                {
                    keepPart = true;
                    throw new IOException("The archive is not complete: " + check.Problem + "." + Environment.NewLine + "It was left as " + partPath + ".");
                }
            }
            if (File.Exists(target))
            {
                File.Delete(target);
            }
            File.Move(partPath, target);
            _SavedFile = target;
            success = true;
            long size = new FileInfo(target).Length;
            result = "Saved " + target + " (" + FormatBytes(size) + ")"
                + (check != null ? ": " + check.Entries.ToString("N0", CultureInfo.CurrentCulture) + " entries, " + FormatBytes(check.DataBytes) + " of file data, checked" : "")
                + (warnings != null ? "." + Environment.NewLine + warnings + "." : ".");
            if (warnings != null)
            {
                success = false;
            }
        }
        catch (OperationCanceledException)
        {
            result = "Stopped: the unfinished archive was deleted.";
        }
        catch (Exception exception)
        {
            result = exception.Message;
        }
        if (!keepPart && _SavedFile == null)
        {
            TryDelete(partPath);
        }
        bool saved = _SavedFile != null;
        Dispatcher.BeginInvoke((Action)(() =>
        {
            if (_Closed)
            {
                return;
            }
            SetRunning(false);
            Log(result);
            StatusText.Text = result.Split('\n')[0].Trim();
            if (saved)
            {
                Progress.IsIndeterminate = false;
                Progress.Value = 100;
                OpenFolderButton.IsEnabled = true;
            }
            if (!saved && !result.StartsWith("Stopped", StringComparison.Ordinal))
            {
                MessageBoxes.Error("The backup failed:" + Environment.NewLine + result);
            }
            else if (saved && !success)
            {
                MessageBoxes.Warning(result);
            }
        }));
    }

    private void ThrowIfCancelled()
    {
        if (_CancelRequested)
        {
            throw new OperationCanceledException();
        }
    }

    /// <summary>The outcome of one run of tar.</summary>
    private sealed class ArchiveRun
    {
        public long Received;

        public int? ExitStatus;

        public bool Cancelled;

        public bool ConnectionLost;

        public readonly List<string> Messages = new List<string>();

        public string ExitStatusText => ExitStatus.HasValue ? ExitStatus.Value.ToString(CultureInfo.InvariantCulture) : "unknown";
    }

    /// <summary>
    /// Runs tar on the server and copies its standard output into <paramref name="partPath"/> (through gzip when
    /// <paramref name="compressLocally"/> is set), while a second thread reads its standard error: the member names that -v prints
    /// there are the progress, the other lines are the messages of tar.
    /// </summary>
    private ArchiveRun RunTar(SshSession session, string command, string partPath, bool compressLocally)
    {
        ArchiveRun run = new ArchiveRun();
        Interlocked.Exchange(ref _ReceivedBytes, 0);
        Interlocked.Exchange(ref _Members, 0);
        _CompressingLocally = compressLocally;
        _Phase = "Archiving...";
        _Clock = Stopwatch.StartNew();
        session.PtyGate.BeginPlainHelper();
        try
        {
            using SshShellChannel channel = session.OpenShellChannel(command, null, ExtendedDataMode.Normal);
            _Channel = channel;
            if (_CancelRequested)
            {
                channel.Abandon();
            }
            Thread errorReader = new Thread(() => ReadMessages(channel, run)) { IsBackground = true, Name = "Backup stderr" };
            errorReader.Start();
            try
            {
                using FileStream file = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
                using Stream destination = compressLocally ? new GZipStream(file, CompressionLevel.Optimal, leaveOpen: true) : (Stream)file;
                byte[] buffer = new byte[1 << 16];
                while (true)
                {
                    int read = channel.Output.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }
                    destination.Write(buffer, 0, read);
                    Interlocked.Add(ref _ReceivedBytes, read);
                }
            }
            finally
            {
                run.Received = Interlocked.Read(ref _ReceivedBytes);
            }
            channel.WaitClosed();
            errorReader.Join(TimeSpan.FromSeconds(10));
            run.Cancelled = _CancelRequested;
            run.ConnectionLost = !run.Cancelled && (channel.ClosedByConnectionLoss || session.IsConnectionClosed);
            if (channel.HasExitStatus)
            {
                run.ExitStatus = channel.ExitStatus;
            }
            else if (channel.ExitSignal != null)
            {
                run.ExitStatus = -1;
                lock (run.Messages)
                {
                    run.Messages.Add("tar was ended by signal " + channel.ExitSignal);
                }
            }
            return run;
        }
        catch (Exception) when (_CancelRequested)
        {
            run.Cancelled = true;
            return run;
        }
        finally
        {
            _Channel = null;
            session.PtyGate.EndPlainHelper();
        }
    }

    private void ReadMessages(SshShellChannel channel, ArchiveRun run)
    {
        try
        {
            using StreamReader reader = new StreamReader(channel.Error, new UTF8Encoding(false), false);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.TrimEnd('\r');
                if (line.Length == 0)
                {
                    continue;
                }
                if (RemoteBackup.IsTarMessage(line))
                {
                    lock (run.Messages)
                    {
                        run.Messages.Add(line);
                    }
                    Log(line);
                }
                else
                {
                    Interlocked.Increment(ref _Members);
                    _CurrentMember = line;
                }
            }
        }
        catch
        {
            // The channel was abandoned (Stop): the copying thread reports that.
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        RequestCancel();
    }

    private void RequestCancel()
    {
        if (!_Running)
        {
            return;
        }
        _CancelRequested = true;
        _Phase = "Stopping...";
        StopButton.IsEnabled = false;
        try
        {
            // Closing the channel makes the server end tar (it gets SIGPIPE or SIGHUP).
            _Channel?.Abandon();
        }
        catch
        {
        }
    }

    private void SetRunning(bool running)
    {
        _Running = running;
        if (running)
        {
            OpenFolderButton.IsEnabled = false;
            Progress.Value = 0;
            Progress.IsIndeterminate = true;
            RateText.Text = "";
            CurrentFileText.Text = "";
            _ProgressTimer.Start();
            ShowProgress();
        }
        else
        {
            _ProgressTimer.Stop();
            Progress.IsIndeterminate = false;
            RateText.Text = _Clock != null ? FormatBytes(Interlocked.Read(ref _ReceivedBytes)) + " in " + FormatElapsed(_Clock.Elapsed) : "";
            CurrentFileText.Text = Interlocked.Read(ref _Members) > 0 ? Interlocked.Read(ref _Members).ToString("N0", CultureInfo.CurrentCulture) + " entries archived" : "";
        }
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool idle = _Ready && !_Running;
        StartButton.IsEnabled = idle;
        StopButton.IsEnabled = _Running && !_CancelRequested;
        FolderTree.IsEnabled = idle;
        foreach (UIElement element in new UIElement[] { WholeSystemButton, ConfigOnlyButton, ClearButton, EstimateButton, FileBox, BrowseButton, CompressionBox, SudoBox, VerifyBox })
        {
            element.IsEnabled = idle;
        }
    }

    private void ShowProgress()
    {
        if (!_Running)
        {
            return;
        }
        long received = Interlocked.Read(ref _ReceivedBytes);
        long members = Interlocked.Read(ref _Members);
        TimeSpan elapsed = _Clock?.Elapsed ?? TimeSpan.Zero;
        StatusText.Text = _Phase + (members > 0 ? "  " + members.ToString("N0", CultureInfo.CurrentCulture) + " entries" : "");
        double seconds = Math.Max(0.5, elapsed.TotalSeconds);
        RateText.Text = FormatBytes(received) + (_CompressingLocally ? " read" : " received") + ", " + FormatBytes((long)(received / seconds)) + "/s, " + FormatElapsed(elapsed);
        CurrentFileText.Text = _CurrentMember != null ? "/" + _CurrentMember.TrimStart('.', '/') : "";
        long? estimate = _EstimatedBytes;
        if (_CompressingLocally && estimate.HasValue && estimate.Value > 0 && _Phase == "Archiving...")
        {
            // The plain tar stream is a little bigger than the data (512-byte headers and padding), so the bar stops just short of 100.
            Progress.IsIndeterminate = false;
            Progress.Value = Math.Min(99.0, received * 100.0 / estimate.Value);
        }
        else
        {
            Progress.IsIndeterminate = true;
        }
    }

    /// <summary>Adds a line to the log (from any thread).</summary>
    private void Log(string line)
    {
        Dispatcher.BeginInvoke((Action)(() =>
        {
            if (_Closed)
            {
                return;
            }
            if (LogBox.LineCount > 2000)
            {
                // tar of a big tree without root rights may complain about thousands of files: keep the end of it.
                LogBox.Text = LogBox.Text.Substring(LogBox.Text.Length / 2);
            }
            LogBox.AppendText((LogBox.Text.Length > 0 ? Environment.NewLine : "") + line);
            LogBox.ScrollToEnd();
        }));
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_SavedFile == null)
        {
            return;
        }
        try
        {
            Process.Start("explorer.exe", "/select,\"" + _SavedFile + "\"");
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_Running)
        {
            return;
        }
        if (!MessageBoxes.Ask("The backup is still running. Stop it and delete the unfinished archive?"))
        {
            e.Cancel = true;
            return;
        }
        RequestCancel();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    #endregion

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return value.ToString(unit == 0 ? "0" : "0.0", CultureInfo.CurrentCulture) + " " + units[unit];
    }

    private static string FormatElapsed(TimeSpan time)
    {
        return time.TotalHours >= 1 ? ((int)time.TotalHours).ToString(CultureInfo.InvariantCulture) + time.ToString(@"\:mm\:ss", CultureInfo.InvariantCulture) : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }
}
