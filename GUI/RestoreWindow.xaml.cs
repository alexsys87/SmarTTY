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
/// Tools > Restore from tar.gz: the reverse of <see cref="BackupWindow"/>. A tar.gz (or tar) archive of this computer is read and
/// shown as a tree with check boxes; the checked members are written into a new tar stream here (block for block as in the
/// archive, see <see cref="TarArchiveReader.Filter"/>) and sent to the standard input of <c>tar -x</c> on the server, so the archive
/// is never stored on the server. md5sum on the server then checks every restored file against the MD5 computed while sending.
/// </summary>
public partial class RestoreWindow : Window
{
    private static readonly Dictionary<SessionWrapper, RestoreWindow> _Open = new Dictionary<SessionWrapper, RestoreWindow>();

    private static string _LastFolder;

    private readonly SessionWrapper _Session;

    private readonly DispatcherTimer _ProgressTimer;

    private RemoteRestoreHostInfo _HostInfo;

    private BackupNode _Root;

    private string _ArchivePath;

    private List<TarEntry> _Entries;

    private bool _Running;

    private bool _Closed;

    private bool _Loading;

    // The state of the running restore, written by the worker threads and shown by the timer.
    private volatile bool _CancelRequested;

    private SshShellChannel _Channel;

    private long _ArchiveRead;

    private long _ArchiveLength;

    private long _Members;

    private long _Checked;

    private long _ToCheck;

    private string _CurrentMember;

    private string _Phase;

    private Stopwatch _Clock;

    /// <summary>Opens the window for the session, or brings the one that is open to the front.</summary>
    public static void ShowFor(SessionWrapper session, Window owner)
    {
        if (_Open.TryGetValue(session, out RestoreWindow open))
        {
            open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }
        RestoreWindow window = new RestoreWindow(session) { Owner = owner };
        _Open[session] = window;
        window.Show();
    }

    private RestoreWindow(SessionWrapper session)
    {
        InitializeComponent();
        _Session = session;
        Title = "Restore from tar.gz - " + (session.Title ?? "SSH");
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
        UpdateButtons();
    }

    /// <summary>Asks the server who the user is and what its tar can do, in the background.</summary>
    private void StartReadingHost()
    {
        SshSession session = _Session.Session;
        if (session == null)
        {
            UserText.Text = "The SSH session is not connected.";
            return;
        }
        Task.Run(() =>
        {
            RemoteRestoreHostInfo info = null;
            try
            {
                info = RemoteRestore.ParseHostInfo(session.RunCommand(PosixShell.Wrap(RemoteRestore.HostInfoCommand, addAdminDirectoriesToPath: true), out _, timeout: TimeSpan.FromSeconds(30)));
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
                _HostInfo = info ?? new RemoteRestoreHostInfo();
                ShowHost();
                UpdateButtons();
            }));
        });
    }

    private void ShowHost()
    {
        RemoteRestoreHostInfo info = _HostInfo;
        List<string> notes = new List<string>();
        if (info.IsRoot == true)
        {
            notes.Add("Logged in as root: every file can be restored with its owner.");
        }
        else if (info.CanSudo)
        {
            SudoBox.Visibility = Visibility.Visible;
            SudoBox.IsChecked = true;
            notes.Add("The user of this connection is not root; passwordless sudo is available.");
        }
        else if (info.IsRoot == false)
        {
            notes.Add("The user of this connection is not root and has no passwordless sudo: files it cannot write are not restored, and the owners stay its own.");
        }
        if (!info.TarHasGzip)
        {
            CompressedItem.IsEnabled = false;
            CompressionBox.SelectedIndex = 1;
            notes.Add("tar on the server cannot decompress gzip: the archive is sent as plain tar.");
        }
        if (!info.HasMd5sum)
        {
            VerifyBox.IsChecked = false;
            VerifyBox.IsEnabled = false;
            VerifyBox.ToolTip = "The server has no md5sum";
            notes.Add("The server has no md5sum: the restored files cannot be checked.");
        }
        UserText.Text = string.Join(" ", notes);
    }

    private bool UseSudo => SudoBox.Visibility == Visibility.Visible && SudoBox.IsChecked == true;

    #region The archive

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        string folder = Directory.Exists(_LastFolder) ? _LastFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string chosen = LocalFileDialogWindow.OpenFile(this, "Open the backup to restore", "tar.gz archives (*.tar.gz;*.tgz)|*.tar.gz;*.tgz|tar archives (*.tar)|*.tar|All files (*.*)|*.*", initialDirectory: folder);
        if (chosen == null)
        {
            return;
        }
        _LastFolder = Path.GetDirectoryName(chosen);
        LoadArchive(chosen);
    }

    /// <summary>Opens the archive for reading: decompressed when it starts with the gzip signature, as it is otherwise.</summary>
    private static Stream OpenArchive(string path, out FileStream file)
    {
        file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        int first = file.ReadByte();
        int second = file.ReadByte();
        file.Seek(0, SeekOrigin.Begin);
        if (first == 0x1F && second == 0x8B)
        {
            return new GZipStream(file, CompressionMode.Decompress, leaveOpen: true);
        }
        return file;
    }

    private void LoadArchive(string path)
    {
        _Loading = true;
        _Root = null;
        _Entries = null;
        _ArchivePath = null;
        FolderTree.ItemsSource = null;
        ArchiveBox.Text = path;
        SummaryText.Text = "";
        LoadingPanel.Visibility = Visibility.Visible;
        LoadingBar.Visibility = Visibility.Visible;
        LoadingText.Text = "Reading the archive...";
        UpdateButtons();
        Task.Run(() =>
        {
            List<TarEntry> entries = null;
            string problem;
            BackupNode root = null;
            try
            {
                using Stream tar = OpenArchive(path, out FileStream file);
                using (file)
                {
                    entries = TarArchiveReader.List(tar, out problem);
                }
                if (problem == null)
                {
                    root = RemoteRestore.BuildTree(entries, FormatBytes);
                }
            }
            catch (Exception exception) when (exception is IOException || exception is InvalidDataException || exception is UnauthorizedAccessException)
            {
                problem = exception.Message;
            }
            Dispatcher.BeginInvoke((Action)(() =>
            {
                if (_Closed)
                {
                    return;
                }
                _Loading = false;
                LoadingBar.Visibility = Visibility.Collapsed;
                if (problem != null)
                {
                    LoadingText.Text = "The archive cannot be restored: " + problem + ".";
                    StatusText.Text = LoadingText.Text;
                    UpdateButtons();
                    return;
                }
                _ArchivePath = path;
                _Entries = entries;
                _Root = root;
                _Root.SelectionChanged += UpdateSummary;
                _Root.IsExpanded = true;
                FolderTree.ItemsSource = new[] { _Root };
                LoadingPanel.Visibility = Visibility.Collapsed;
                UpdateSummary();
                StatusText.Text = "The archive is complete. Check what to restore and press Start.";
                UpdateButtons();
            }));
        });
    }

    private void UpdateSummary()
    {
        if (_Entries == null || _Root == null)
        {
            SummaryText.Text = "";
            return;
        }
        Func<TarEntry, bool> select = RemoteRestore.MakeSelection(_Root.CollectMembers());
        List<TarEntry> selected = _Entries.Where(select).ToList();
        long files = _Entries.Count(e => e.Type == TarEntryType.File);
        SummaryText.Text = _Entries.Count.ToString("N0", CultureInfo.CurrentCulture) + " entries, " + files.ToString("N0", CultureInfo.CurrentCulture) + " files, "
            + FormatBytes(_Entries.Sum(e => e.Size)) + "; checked: " + selected.Count.ToString("N0", CultureInfo.CurrentCulture) + " entries, " + FormatBytes(selected.Sum(e => e.Size));
        UpdateButtons();
    }

    private void All_Click(object sender, RoutedEventArgs e)
    {
        _Root?.SetChecked(true);
    }

    private void ConfigOnly_Click(object sender, RoutedEventArgs e)
    {
        if (_Root == null)
        {
            return;
        }
        _Root.SetChecked(false);
        BackupNode etc = _Root.Find("/etc");
        if (etc == null)
        {
            StatusText.Text = "The archive holds no /etc folder.";
        }
        else
        {
            etc.SetChecked(true);
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _Root?.SetChecked(false);
    }

    #endregion

    #region The restore

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        SshSession session = _Session.Session;
        if (session == null || session.IsConnectionClosed)
        {
            StatusText.Text = "The SSH session is not connected.";
            return;
        }
        if (_Root == null || _ArchivePath == null)
        {
            return;
        }
        string target;
        try
        {
            target = RemoteRestore.NormalizeTarget(TargetBox.Text);
        }
        catch (ArgumentException exception)
        {
            MessageBoxes.Error(exception.Message);
            return;
        }
        List<string> members = _Root.CollectMembers();
        Func<TarEntry, bool> select = RemoteRestore.MakeSelection(members);
        List<TarEntry> selected = _Entries.Where(select).ToList();
        if (selected.Count == 0)
        {
            MessageBoxes.Error("Nothing is checked: check the files and folders to restore.");
            return;
        }
        long dataBytes = selected.Sum(entry => entry.Size);
        string question = "Restore " + selected.Count.ToString("N0", CultureInfo.CurrentCulture) + " entries (" + FormatBytes(dataBytes) + ") into " + target + " on " + (_Session.Title ?? "the server") + "?"
            + Environment.NewLine + Environment.NewLine
            + (KeepBox.IsChecked == true ? "Files that exist on the server are left as they are." : "Files that exist on the server are replaced by the ones of the archive.");
        if (target == "/")
        {
            question += Environment.NewLine + Environment.NewLine + "The files of the running system are written in place: replacing programs or libraries that are in use can make the server unstable until it is restarted.";
        }
        if (!MessageBoxes.AskWarn(question))
        {
            return;
        }
        bool gzip = CompressionBox.SelectedIndex == 0 && _HostInfo?.TarHasGzip == true;
        bool keep = KeepBox.IsChecked == true;
        bool verify = VerifyBox.IsChecked == true && _HostInfo?.HasMd5sum == true;
        bool useSudo = UseSudo;
        _CancelRequested = false;
        _ArchiveRead = 0;
        _ArchiveLength = Math.Max(1, new FileInfo(_ArchivePath).Length);
        _Members = 0;
        _Checked = 0;
        _ToCheck = 0;
        _CurrentMember = null;
        _Phase = "Checking the free space...";
        LogBox.Clear();
        SetRunning(true);
        string archive = _ArchivePath;
        Task.Run(() => RunRestore(session, archive, members, target, dataBytes, gzip, keep, verify, useSudo));
    }

    private void RunRestore(SshSession session, string archive, List<string> members, string target, long dataBytes, bool gzip, bool keep, bool verify, bool useSudo)
    {
        string result;
        bool ok = false;
        try
        {
            long? free = RemoteRestore.ParseFreeSpace(session.RunCommand(RemoteRestore.BuildFreeSpaceCommand(target), out _, timeout: TimeSpan.FromSeconds(30)));
            if (free.HasValue)
            {
                Log("Free space for " + target + " on the server: " + FormatBytes(free.Value) + "; the checked files hold " + FormatBytes(dataBytes) + ".");
                if (free.Value < dataBytes && !keep)
                {
                    bool goOn = Dispatcher.Invoke(() => MessageBoxes.AskWarn("The file system of " + target + " has " + FormatBytes(free.Value) + " free, and the checked files hold " + FormatBytes(dataBytes)
                        + ". Files that replace existing ones need less, so it may still fit. Restore anyway?"));
                    if (!goOn)
                    {
                        throw new OperationCanceledException();
                    }
                }
            }
            ThrowIfCancelled();
            _Phase = "Restoring...";
            UnpackRun run = Unpack(session, archive, members, target, gzip, keep, useSudo);
            if (run.Cancelled)
            {
                throw new OperationCanceledException();
            }
            if (run.ConnectionLost)
            {
                throw new IOException("The connection to the server was lost during the restore: only a part of the files was unpacked.");
            }
            if (run.LocalError != null)
            {
                throw new IOException(run.LocalError);
            }
            List<string> warnings = new List<string>();
            if (run.ExitStatus != 0)
            {
                string reason = run.Messages.Count > 0 ? run.Messages.Count + (run.Messages.Count == 1 ? " message" : " messages") + " in the log" : "see the log";
                if (run.Members == 0)
                {
                    throw new IOException("tar did not unpack anything (exit status " + run.ExitStatusText + "):" + Environment.NewLine + string.Join(Environment.NewLine, run.Messages.Take(5).DefaultIfEmpty(reason)));
                }
                warnings.Add("tar finished with exit status " + run.ExitStatusText + ": " + reason + (keep ? " (with -k, tar reports every file that exists already)" : ""));
            }
            string checkText = "";
            if (verify)
            {
                _Phase = "Checking the restored files (md5sum)...";
                List<string> mismatches = Verify(session, target, run.Files, useSudo);
                ThrowIfCancelled();
                if (mismatches.Count == 0)
                {
                    checkText = ", all " + run.Files.Count.ToString("N0", CultureInfo.CurrentCulture) + " files checked with md5sum";
                }
                else
                {
                    foreach (string mismatch in mismatches.Take(200))
                    {
                        Log("check: " + mismatch);
                    }
                    warnings.Add(mismatches.Count.ToString("N0", CultureInfo.CurrentCulture) + " of " + run.Files.Count.ToString("N0", CultureInfo.CurrentCulture)
                        + " files differ from the archive on the server" + (keep ? " (with -k, the existing files were left as they were)" : "") + ": see the log");
                }
            }
            result = "Restored " + run.Members.ToString("N0", CultureInfo.CurrentCulture) + " entries into " + target + checkText + "."
                + (warnings.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, warnings) + "." : "");
            ok = warnings.Count == 0;
        }
        catch (OperationCanceledException)
        {
            result = "Stopped: the files unpacked so far stay on the server.";
        }
        catch (Exception exception)
        {
            result = exception.Message;
        }
        bool finished = result.StartsWith("Restored", StringComparison.Ordinal);
        Dispatcher.BeginInvoke((Action)(() =>
        {
            if (_Closed)
            {
                return;
            }
            SetRunning(false);
            Log(result);
            StatusText.Text = result.Split('\n')[0].Trim();
            if (finished)
            {
                Progress.Value = 100;
            }
            if (!finished && !result.StartsWith("Stopped", StringComparison.Ordinal))
            {
                MessageBoxes.Error("The restore failed:" + Environment.NewLine + result);
            }
            else if (finished && !ok)
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

    private sealed class UnpackRun
    {
        public int? ExitStatus;

        public bool Cancelled;

        public bool ConnectionLost;

        public string LocalError;

        public long Members;

        public List<TarArchiveReader.FilteredFile> Files = new List<TarArchiveReader.FilteredFile>();

        public readonly List<string> Messages = new List<string>();

        public string ExitStatusText => ExitStatus.HasValue ? ExitStatus.Value.ToString(CultureInfo.InvariantCulture) : "unknown";
    }

    /// <summary>
    /// Runs tar -x on the server and writes the checked members of the archive to its standard input, compressed again when
    /// <paramref name="gzip"/> is set. The member names that -v prints on the standard output are the progress; the standard
    /// error holds the messages of tar.
    /// </summary>
    private UnpackRun Unpack(SshSession session, string archive, List<string> members, string target, bool gzip, bool keep, bool useSudo)
    {
        UnpackRun run = new UnpackRun();
        _Clock = Stopwatch.StartNew();
        try
        {
            using SshShellChannel channel = session.OpenPlainChannel(RemoteRestore.BuildExtractCommand(target, gzip, keep, useSudo));
            _Channel = channel;
            if (_CancelRequested)
            {
                channel.Abandon();
            }
            Thread outputReader = new Thread(() => ReadLines(channel.Output, line =>
            {
                Interlocked.Increment(ref _Members);
                _CurrentMember = line;
            }))
            { IsBackground = true, Name = "Restore stdout" };
            Thread errorReader = new Thread(() => ReadLines(channel.Error, line =>
            {
                lock (run.Messages)
                {
                    run.Messages.Add(line);
                }
                Log(line);
            }))
            { IsBackground = true, Name = "Restore stderr" };
            outputReader.Start();
            errorReader.Start();
            try
            {
                using Stream tar = OpenArchive(archive, out FileStream file);
                using (file)
                {
                    ProgressStream source = new ProgressStream(tar, file, value => Interlocked.Exchange(ref _ArchiveRead, value));
                    using BufferedStream buffered = new BufferedStream(new ChannelWriteStream(channel), 1 << 16);
                    Stream destination = gzip ? new GZipStream(buffered, CompressionLevel.Fastest, leaveOpen: true) : (Stream)buffered;
                    try
                    {
                        run.Files = TarArchiveReader.Filter(source, destination, RemoteRestore.MakeSelection(members), () => _CancelRequested);
                    }
                    finally
                    {
                        if (gzip)
                        {
                            destination.Dispose();
                        }
                    }
                    buffered.Flush();
                }
                channel.SendEof();
            }
            catch (InvalidDataException exception)
            {
                run.LocalError = exception.Message;
                channel.Abandon();
            }
            catch (Exception) when (_CancelRequested)
            {
                run.Cancelled = true;
                return run;
            }
            catch (IOException) when (channel.IsEof || channel.TryWaitClosed())
            {
                // tar ended before it read everything (it failed, or the disk is full): its exit status and messages say why.
            }
            channel.WaitClosed();
            outputReader.Join(TimeSpan.FromSeconds(10));
            errorReader.Join(TimeSpan.FromSeconds(10));
            run.Members = Interlocked.Read(ref _Members);
            run.Cancelled = _CancelRequested;
            run.ConnectionLost = !run.Cancelled && (channel.ClosedByConnectionLoss || session.IsConnectionClosed);
            if (channel.HasExitStatus)
            {
                run.ExitStatus = channel.ExitStatus;
            }
            else if (channel.ExitSignal != null)
            {
                run.ExitStatus = -1;
                run.Messages.Add("tar was ended by signal " + channel.ExitSignal);
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
        }
    }

    /// <summary>Asks md5sum on the server for every restored file and returns the ones that differ from the archive.</summary>
    private List<string> Verify(SshSession session, string target, List<TarArchiveReader.FilteredFile> files, bool useSudo)
    {
        // A name with a line break cannot be sent as one line: such files are not checked.
        List<TarArchiveReader.FilteredFile> checkable = files.Where(f => f.Name.IndexOf('\n') < 0 && f.Name.IndexOf('\r') < 0).ToList();
        Interlocked.Exchange(ref _ToCheck, checkable.Count);
        Interlocked.Exchange(ref _Checked, 0);
        if (checkable.Count == 0)
        {
            return new List<string>();
        }
        List<string> answers = new List<string>();
        try
        {
            using SshShellChannel channel = session.OpenPlainChannel(RemoteRestore.BuildVerifyCommand(target, useSudo));
            _Channel = channel;
            Thread reader = new Thread(() => ReadLines(channel.Output, line =>
            {
                lock (answers)
                {
                    answers.Add(line);
                }
                Interlocked.Increment(ref _Checked);
            }))
            { IsBackground = true, Name = "Restore check" };
            reader.Start();
            using (BufferedStream input = new BufferedStream(new ChannelWriteStream(channel), 1 << 16))
            {
                foreach (TarArchiveReader.FilteredFile file in checkable)
                {
                    ThrowIfCancelled();
                    byte[] line = Encoding.UTF8.GetBytes(file.Name + "\n");
                    input.Write(line, 0, line.Length);
                }
                input.Flush();
            }
            channel.SendEof();
            channel.WaitClosed();
            reader.Join(TimeSpan.FromSeconds(30));
            if (channel.ClosedByConnectionLoss)
            {
                throw new IOException("The connection to the server was lost during the check of the restored files.");
            }
        }
        catch (Exception) when (_CancelRequested)
        {
            throw new OperationCanceledException();
        }
        finally
        {
            _Channel = null;
        }
        lock (answers)
        {
            return RemoteRestore.FindMismatches(checkable, answers);
        }
    }

    private static void ReadLines(Stream stream, Action<string> onLine)
    {
        try
        {
            using StreamReader reader = new StreamReader(stream, new UTF8Encoding(false), false);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.TrimEnd('\r');
                if (line.Length > 0)
                {
                    onLine(line);
                }
            }
        }
        catch
        {
            // The channel was abandoned (Stop): the writing thread reports that.
        }
    }

    /// <summary>Writes to the standard input of a channel (its streams are read-only, the writes go through the channel itself).</summary>
    private sealed class ChannelWriteStream : Stream
    {
        private readonly SshShellChannel _channel;

        public ChannelWriteStream(SshShellChannel channel)
        {
            _channel = channel;
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_channel.IsDisposed || _channel.TryWaitClosed())
            {
                throw new IOException("The command on the server has ended.");
            }
            _channel.Input.Write(buffer, offset, count);
        }
    }

    /// <summary>Reads through <paramref name="inner"/> and reports how far into the archive file the reading is.</summary>
    private sealed class ProgressStream : Stream
    {
        private readonly Stream _inner;

        private readonly FileStream _file;

        private readonly Action<long> _report;

        public ProgressStream(Stream inner, FileStream file, Action<long> report)
        {
            _inner = inner;
            _file = file;
            _report = report;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = _inner.Read(buffer, offset, count);
            _report(_file.Position);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
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
            Progress.Value = 0;
            RateText.Text = "";
            CurrentFileText.Text = "";
            _ProgressTimer.Start();
            ShowProgress();
        }
        else
        {
            _ProgressTimer.Stop();
            Progress.IsIndeterminate = false;
            CurrentFileText.Text = Interlocked.Read(ref _Members) > 0 ? Interlocked.Read(ref _Members).ToString("N0", CultureInfo.CurrentCulture) + " entries unpacked" : "";
        }
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool hasArchive = _Root != null && !_Loading;
        bool idle = !_Running && !_Loading;
        StartButton.IsEnabled = idle && hasArchive && _HostInfo != null && _Root.IsChecked != false;
        StopButton.IsEnabled = _Running && !_CancelRequested;
        OpenButton.IsEnabled = idle;
        FolderTree.IsEnabled = idle && hasArchive;
        foreach (UIElement element in new UIElement[] { AllButton, ConfigOnlyButton, ClearButton })
        {
            element.IsEnabled = idle && hasArchive;
        }
        foreach (UIElement element in new UIElement[] { TargetBox, CompressionBox, KeepBox, SudoBox })
        {
            element.IsEnabled = idle;
        }
        VerifyBox.IsEnabled = idle && (_HostInfo == null || _HostInfo.HasMd5sum);
    }

    private void ShowProgress()
    {
        if (!_Running)
        {
            return;
        }
        long members = Interlocked.Read(ref _Members);
        StatusText.Text = _Phase + (members > 0 ? "  " + members.ToString("N0", CultureInfo.CurrentCulture) + " entries" : "");
        if (_Phase == "Restoring...")
        {
            long read = Interlocked.Read(ref _ArchiveRead);
            double seconds = Math.Max(0.5, (_Clock?.Elapsed ?? TimeSpan.Zero).TotalSeconds);
            Progress.IsIndeterminate = false;
            Progress.Value = Math.Min(100.0, read * 100.0 / _ArchiveLength);
            RateText.Text = FormatBytes(read) + " of " + FormatBytes(_ArchiveLength) + " read, " + FormatBytes((long)(read / seconds)) + "/s";
            CurrentFileText.Text = _CurrentMember != null ? "/" + _CurrentMember.TrimStart('.', '/') : "";
        }
        else if (_Phase.StartsWith("Checking the restored", StringComparison.Ordinal) && Interlocked.Read(ref _ToCheck) > 0)
        {
            long done = Interlocked.Read(ref _Checked);
            long total = Interlocked.Read(ref _ToCheck);
            Progress.IsIndeterminate = false;
            Progress.Value = Math.Min(100.0, done * 100.0 / total);
            RateText.Text = done.ToString("N0", CultureInfo.CurrentCulture) + " of " + total.ToString("N0", CultureInfo.CurrentCulture) + " files checked";
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
                LogBox.Text = LogBox.Text.Substring(LogBox.Text.Length / 2);
            }
            LogBox.AppendText((LogBox.Text.Length > 0 ? Environment.NewLine : "") + line);
            LogBox.ScrollToEnd();
        }));
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
        if (!MessageBoxes.Ask("The restore is still running. Stop it? The files unpacked so far stay on the server."))
        {
            e.Cancel = true;
            return;
        }
        RequestCancel();
    }

    #endregion

    internal static string FormatBytes(long bytes)
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
}
