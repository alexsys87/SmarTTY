using SmarTTY.GUI.Backend;
using SmarTTY.Ssh;
using SmarTTY.Transport;
using SmarTTY.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace SmarTTY.GUI;

/// <summary>
/// Tools > Flash (MTD) backup and restore: the partitions of /proc/mtd of the server. The backup reads the checked partitions (or the
/// whole flash as one image) into files on this computer and writes a description with their checksums (name.mtdinfo); the restore
/// writes images back, each through the checked sequence of <see cref="MtdRestoreJob"/>. The commands are those of
/// <see cref="MtdFlash"/>, run over the SSH session by <see cref="SshRemoteShell"/>.
/// </summary>
public partial class MtdFlashWindow : Window
{
    /// <summary>A partition in the backup list.</summary>
    public sealed class PartitionRow : INotifyPropertyChanged
    {
        private bool _IsChecked;

        public MtdPartition Partition { get; }

        public PartitionRow(MtdPartition partition)
        {
            Partition = partition;
            _IsChecked = CanRead;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public bool IsChecked
        {
            get => _IsChecked;
            set
            {
                _IsChecked = value && CanRead;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
                Changed?.Invoke();
            }
        }

        public Action Changed;

        public bool CanRead => Partition.Device != null && Partition.Size > 0;

        public string ReadProblem => CanRead ? null : "The server has no device node for this partition";

        public string Device => "mtd" + Partition.Index.ToString(CultureInfo.InvariantCulture);

        public string Name => Partition.Name;

        public string SizeText => MtdFlash.FormatBytes(Partition.Size);

        public string OffsetText => Partition.Offset.HasValue ? "0x" + Partition.Offset.Value.ToString("x", CultureInfo.InvariantCulture) : "?";

        public string EraseText => MtdFlash.FormatBytes(Partition.EraseSize);

        public string TypeText => Partition.Type ?? "?";

        public string Notes
        {
            get
            {
                List<string> notes = new List<string>();
                if (Partition.Writable == false)
                {
                    notes.Add("read-only");
                }
                if (Partition.Device == null)
                {
                    notes.Add("no device node");
                }
                else if (!Partition.HasCharDevice)
                {
                    notes.Add("block device only");
                }
                if (Partition.InUse != null)
                {
                    notes.Add(Partition.InUse);
                }
                string risk = MtdFlash.RiskNote(Partition);
                if (risk != null && Partition.InUse == null)
                {
                    notes.Add(risk);
                }
                return string.Join("; ", notes);
            }
        }
    }

    /// <summary>A partition offered as the target of an image (or none).</summary>
    public sealed class TargetOption
    {
        public MtdPartition Partition { get; set; }

        public string Text { get; set; }
    }

    /// <summary>An image in the restore list.</summary>
    public sealed class ImageRow : INotifyPropertyChanged
    {
        private TargetOption _Target;

        private bool _IsChecked = true;

        public MtdImagePart Part { get; }

        /// <summary>The image file on this computer.</summary>
        public string ImagePath { get; }

        /// <summary>Why the image file cannot be used at all (missing, too short); null when it can.</summary>
        public string FileProblem { get; }

        public List<TargetOption> Targets { get; }

        public List<string> Errors { get; } = new List<string>();

        public List<string> Warnings { get; } = new List<string>();

        private readonly Func<MtdHostInfo> _info;

        public event PropertyChangedEventHandler PropertyChanged;

        public ImageRow(MtdImagePart part, string imagePath, string fileProblem, List<TargetOption> targets, Func<MtdHostInfo> info)
        {
            Part = part;
            ImagePath = imagePath;
            FileProblem = fileProblem;
            Targets = targets;
            _info = info;
            MtdPartition match = MtdFlash.MatchPartition(part, targets.Where(t => t.Partition != null).Select(t => t.Partition).ToList());
            _Target = targets.FirstOrDefault(t => t.Partition == match) ?? targets[0];
            Recheck();
        }

        public bool IsChecked
        {
            get => _IsChecked;
            set
            {
                _IsChecked = value;
                Raise(nameof(IsChecked));
            }
        }

        public TargetOption Target
        {
            get => _Target;
            set
            {
                _Target = value;
                Raise(nameof(Target));
                Recheck();
            }
        }

        public string Source => Part.Name != null ? "mtd" + Part.MtdIndex.ToString(CultureInfo.InvariantCulture) + " \"" + Part.Name + "\"" : Path.GetFileName(ImagePath);

        public string SizeText => MtdFlash.FormatBytes(Part.Size);

        public string ChecksumText => Part.Sha256 != null ? "SHA-256" : Part.Md5 != null ? "MD5" : "none stored";

        public bool IsReady => Errors.Count == 0;

        public string Verdict => Errors.Count > 0 ? Errors[0] : Warnings.Count > 0 ? "ready; " + Warnings[0] : "ready";

        public string VerdictDetails => string.Join(Environment.NewLine, Errors.Concat(Warnings));

        public Brush VerdictBrush => Errors.Count > 0 ? Brushes.IndianRed : Warnings.Count > 0 ? Brushes.DarkGoldenrod : Brushes.SeaGreen;

        public void Recheck()
        {
            Errors.Clear();
            Warnings.Clear();
            if (FileProblem != null)
            {
                Errors.Add(FileProblem);
            }
            else
            {
                MtdFlash.CheckRestore(Part, _Target?.Partition, _info() ?? new MtdHostInfo(), Errors, Warnings);
            }
            Raise(nameof(Verdict));
            Raise(nameof(VerdictDetails));
            Raise(nameof(VerdictBrush));
            Raise(nameof(IsReady));
        }

        private void Raise(string property)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }

    private static readonly Dictionary<SessionWrapper, MtdFlashWindow> _Open = new Dictionary<SessionWrapper, MtdFlashWindow>();

    private static string _LastFolder;

    private readonly SessionWrapper _Session;

    private readonly ObservableCollection<PartitionRow> _PartitionRows = new ObservableCollection<PartitionRow>();

    private readonly ObservableCollection<ImageRow> _ImageRows = new ObservableCollection<ImageRow>();

    private readonly DispatcherTimer _ProgressTimer;

    private MtdHostInfo _Info;

    private List<MtdPartition> _Layout;

    private bool _UseSudo;

    private bool _Running;

    private bool _Closed;

    private string _SavedFolder;

    // The state of the running job, written by the worker and shown by the timer.
    private volatile bool _CancelRequested;

    private volatile bool _Writing;

    private SshRemoteShell _Shell;

    private string _Phase;

    private long _Done;

    private long _Total;

    private Stopwatch _Clock;

    /// <summary>Opens the window for the session, or brings the one that is open to the front.</summary>
    public static void ShowFor(SessionWrapper session, Window owner)
    {
        if (_Open.TryGetValue(session, out MtdFlashWindow open))
        {
            open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }
        MtdFlashWindow window = new MtdFlashWindow(session) { Owner = owner };
        _Open[session] = window;
        window.Show();
    }

    private MtdFlashWindow(SessionWrapper session)
    {
        InitializeComponent();
        _Session = session;
        Title = "Flash (MTD) backup and restore - " + (session.Title ?? "SSH");
        PartitionList.ItemsSource = _PartitionRows;
        ImageList.ItemsSource = _ImageRows;
        FolderBox.Text = Directory.Exists(_LastFolder) ? _LastFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        _ProgressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _ProgressTimer.Tick += (sender, e) => ShowProgress();
        Closing += Window_Closing;
        Closed += (sender, e) =>
        {
            _Closed = true;
            _ProgressTimer.Stop();
            _Open.Remove(_Session);
        };
        Loaded += (sender, e) => ReadHost(null);
        UpdateButtons();
    }

    #region The server

    /// <summary>Asks the server for its partitions, tools and memory (on a pool thread) and fills the lists.</summary>
    private void ReadHost(Action done)
    {
        SshSession session = _Session.Session;
        if (session == null)
        {
            HostText.Text = "The SSH session is not connected.";
            return;
        }
        Task.Run(() =>
        {
            MtdHostInfo info = null;
            string error = null;
            try
            {
                info = QueryInfo(new SshRemoteShell(session));
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
                if (info == null)
                {
                    HostText.Text = "The flash partitions could not be read: " + error;
                    return;
                }
                ApplyInfo(info);
                done?.Invoke();
            }));
        });
    }

    private static MtdHostInfo QueryInfo(IRemoteShell shell)
    {
        RemoteCommandResult result = shell.Run(PosixShell.Wrap(MtdFlash.InfoCommand, addAdminDirectoriesToPath: true));
        if (result.ConnectionLost)
        {
            throw new IOException("The connection to the server was lost.");
        }
        return MtdFlash.ParseInfo(result.Output);
    }

    private void ApplyInfo(MtdHostInfo info)
    {
        _Info = info;
        _UseSudo = info.IsRoot == false && info.CanSudo;
        // A refresh (after a job) keeps the check boxes as they were.
        Dictionary<int, bool> previous = _PartitionRows.ToDictionary(r => r.Partition.Index, r => r.IsChecked);
        _PartitionRows.Clear();
        foreach (MtdPartition partition in info.Partitions)
        {
            PartitionRow row = new PartitionRow(partition);
            if (previous.TryGetValue(partition.Index, out bool wasChecked))
            {
                row.IsChecked = wasChecked;
            }
            row.Changed = UpdateButtons;
            _PartitionRows.Add(row);
        }
        _Layout = MtdFlash.FullFlashLayout(info.Partitions, out string layoutReason);
        if (_Layout != null)
        {
            FullFlashMode.IsEnabled = true;
            FullFlashText.Text = MtdFlash.FormatBytes(_Layout.Sum(p => p.Size)) + " from " + string.Join(" + ", _Layout.Select(p => p.Name)) + ", in the order of the flash";
        }
        else
        {
            FullFlashMode.IsEnabled = false;
            PartitionsMode.IsChecked = true;
            FullFlashText.Text = "Not available: " + layoutReason + ".";
        }

        List<string> facts = new List<string>
        {
            info.Partitions.Count + (info.Partitions.Count == 1 ? " partition" : " partitions")
        };
        string tools = string.Join(", ", MtdFlash.ToolNames.Where(info.Tools.Contains));
        facts.Add("tools: " + (tools.Length > 0 ? tools : "none"));
        if (info.UsableMemoryBytes.HasValue)
        {
            facts.Add("memory available: " + MtdFlash.FormatBytes(info.UsableMemoryBytes.Value));
        }
        foreach (MtdRamDirectory directory in info.RamDirectories.Where(d => d.Path == "/tmp" || d.Path == "/dev/shm"))
        {
            facts.Add(directory.Path + " (" + directory.Type + "): " + (directory.FreeBytes.HasValue ? MtdFlash.FormatBytes(directory.FreeBytes.Value) + " free" : "no limit"));
        }
        if (info.IsRoot == false)
        {
            facts.Add(_UseSudo ? "not root, sudo -n is used" : "not root and no passwordless sudo: the flash may not be readable");
        }
        if (info.HashAlgorithm == null)
        {
            facts.Add("no sha256sum or md5sum: backups are not compared with the flash, restores are not possible");
        }
        HostText.Text = string.Join("  |  ", facts) + ".";

        foreach (ImageRow row in _ImageRows)
        {
            row.Recheck();
        }
        UpdateButtons();
    }

    #endregion

    #region Backup

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        UpdateButtons();
    }

    private void CheckAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (PartitionRow row in _PartitionRows)
        {
            row.IsChecked = true;
        }
    }

    private void CheckNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (PartitionRow row in _PartitionRows)
        {
            row.IsChecked = false;
        }
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        string chosen = LocalFolderBrowserWindow.Browse(FolderBox.Text, "Folder for the flash backup", "The images and their description are saved here.");
        if (chosen != null)
        {
            FolderBox.Text = chosen;
        }
    }

    private void Backup_Click(object sender, RoutedEventArgs e)
    {
        SshSession session = _Session.Session;
        if (session == null || session.IsConnectionClosed || _Info == null)
        {
            StatusText.Text = "The SSH session is not connected.";
            return;
        }
        string folder = FolderBox.Text.Trim();
        if (!Directory.Exists(folder))
        {
            MessageBoxes.Error("The folder " + folder + " does not exist.");
            return;
        }
        bool fullFlash = FullFlashMode.IsChecked == true && _Layout != null;
        List<MtdPartition> partitions = fullFlash ? _Layout : _PartitionRows.Where(r => r.IsChecked).Select(r => r.Partition).ToList();
        if (partitions.Count == 0)
        {
            MessageBoxes.Error("No partition is checked.");
            return;
        }
        if (fullFlash && partitions.Any(p => p.Device == null))
        {
            MessageBoxes.Error("The whole flash cannot be read: the server has no device node for " + string.Join(", ", partitions.Where(p => p.Device == null).Select(p => p.Label)) + ".");
            return;
        }
        _LastFolder = folder;
        string prefix = MtdFlash.FilePrefix(_Session.Title, DateTime.Now);
        StartJob(partitions.Sum(p => p.Size), () => RunBackup(session, folder, prefix, partitions, fullFlash));
    }

    private void RunBackup(SshSession session, string folder, string prefix, List<MtdPartition> partitions, bool fullFlash)
    {
        List<string> written = new List<string>();
        string result;
        bool ok = false;
        try
        {
            MtdManifest manifest = new MtdManifest { Host = _Session.Title, Created = DateTime.UtcNow, Kind = fullFlash ? "fullflash" : "partitions" };
            long before = 0;
            if (fullFlash)
            {
                string name = prefix + "-fullflash.bin";
                string path = Path.Combine(folder, name);
                written.Add(path + ".part");
                using (FileStream file = new FileStream(path + ".part", FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    foreach (MtdPartition partition in partitions)
                    {
                        long offset = file.Position;
                        long start = before;
                        manifest.Parts.Add(MtdBackupJob.BackUp(_Shell, _Info, partition, file, name, offset, _UseSudo, SetPhase, done => Interlocked.Exchange(ref _Done, start + done), () => _CancelRequested, Log));
                        before += partition.Size;
                    }
                }
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                File.Move(path + ".part", path);
                written[written.Count - 1] = path;
            }
            else
            {
                foreach (MtdPartition partition in partitions)
                {
                    string name = MtdFlash.ImageFileName(prefix, partition);
                    string path = Path.Combine(folder, name);
                    written.Add(path + ".part");
                    long start = before;
                    using (FileStream file = new FileStream(path + ".part", FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                    {
                        manifest.Parts.Add(MtdBackupJob.BackUp(_Shell, _Info, partition, file, name, 0, _UseSudo, SetPhase, done => Interlocked.Exchange(ref _Done, start + done), () => _CancelRequested, Log));
                    }
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                    File.Move(path + ".part", path);
                    written[written.Count - 1] = path;
                    before += partition.Size;
                }
            }
            string manifestPath = Path.Combine(folder, prefix + ".mtdinfo");
            File.WriteAllText(manifestPath, manifest.Write(), new UTF8Encoding(false));
            written.Add(manifestPath);
            _SavedFolder = folder;
            ok = true;
            result = "Saved " + manifest.Parts.Count + (manifest.Parts.Count == 1 ? " partition" : " partitions") + " (" + MtdFlash.FormatBytes(partitions.Sum(p => p.Size)) + ") to " + folder
                + (_Info.HashAlgorithm != null ? ", each checked against the flash" : "") + ". The description with the checksums is " + Path.GetFileName(manifestPath) + ".";
        }
        catch (OperationCanceledException)
        {
            result = "Stopped: the files of this backup were deleted.";
        }
        catch (Exception exception)
        {
            result = exception.Message + Environment.NewLine + "The files of this backup were deleted.";
        }
        if (!ok)
        {
            foreach (string path in written)
            {
                TryDelete(path);
            }
        }
        FinishJob(result, ok, ok);
    }

    #endregion

    #region Restore

    private void OpenImage_Click(object sender, RoutedEventArgs e)
    {
        string chosen = LocalFileDialogWindow.OpenFile(this, "Open a flash backup or an image", "Flash backups (*.mtdinfo)|*.mtdinfo|Images (*.bin;*.img)|*.bin;*.img|All files (*.*)|*.*",
            initialDirectory: Directory.Exists(_LastFolder) ? _LastFolder : null);
        if (chosen == null)
        {
            return;
        }
        _LastFolder = Path.GetDirectoryName(chosen);
        LoadImages(chosen);
    }

    private void LoadImages(string path)
    {
        _ImageRows.Clear();
        ImageBox.Text = path;
        List<TargetOption> Targets()
        {
            List<TargetOption> targets = new List<TargetOption> { new TargetOption { Text = "(choose a partition)" } };
            targets.AddRange((_Info?.Partitions ?? new List<MtdPartition>()).Select(p => new TargetOption { Partition = p, Text = p.Label + ", " + MtdFlash.FormatBytes(p.Size) }));
            return targets;
        }
        try
        {
            bool isManifest = File.ReadAllText(path, Encoding.UTF8).StartsWith(MtdManifest.Signature, StringComparison.Ordinal);
            if (isManifest)
            {
                MtdManifest manifest = MtdManifest.Parse(File.ReadAllText(path, Encoding.UTF8));
                string folder = Path.GetDirectoryName(path);
                foreach (MtdImagePart part in manifest.Parts)
                {
                    string image = Path.Combine(folder, part.File);
                    string problem = null;
                    if (!File.Exists(image))
                    {
                        problem = "the image file " + part.File + " is missing";
                    }
                    else if (new FileInfo(image).Length < part.FileOffset + part.Size)
                    {
                        problem = "the image file " + part.File + " is shorter than the description says";
                    }
                    _ImageRows.Add(new ImageRow(part, image, problem, Targets(), () => _Info) { IsChecked = false });
                }
                StatusText.Text = "Backup of " + (manifest.Host ?? "?") + " from " + manifest.Created.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) + ": check the images to write.";
            }
            else
            {
                long length = new FileInfo(path).Length;
                MtdImagePart part = new MtdImagePart { File = path, Size = length };
                _ImageRows.Add(new ImageRow(part, path, length == 0 ? "the file is empty" : null, Targets(), () => _Info));
                StatusText.Text = "An image without a description: choose the partition to write it to. Its checksum is computed here and checked in RAM and in the flash.";
            }
        }
        catch (Exception exception) when (exception is IOException || exception is InvalidDataException || exception is UnauthorizedAccessException)
        {
            MessageBoxes.Error(exception.Message);
        }
        foreach (ImageRow row in _ImageRows)
        {
            row.PropertyChanged += (sender, e) => UpdateButtons();
        }
        UpdateButtons();
    }

    private void Write_Click(object sender, RoutedEventArgs e)
    {
        SshSession session = _Session.Session;
        if (session == null || session.IsConnectionClosed || _Info == null)
        {
            StatusText.Text = "The SSH session is not connected.";
            return;
        }
        List<ImageRow> rows = _ImageRows.Where(r => r.IsChecked).ToList();
        foreach (ImageRow row in rows)
        {
            row.Recheck();
        }
        List<ImageRow> blocked = rows.Where(r => !r.IsReady).ToList();
        if (rows.Count == 0)
        {
            MessageBoxes.Error("No image is checked.");
            return;
        }
        if (blocked.Count > 0)
        {
            MessageBoxes.Error("These images cannot be written:" + Environment.NewLine + string.Join(Environment.NewLine, blocked.Select(r => r.Source + ": " + string.Join("; ", r.Errors))));
            return;
        }
        if (rows.Select(r => r.Target.Partition.Index).Distinct().Count() != rows.Count)
        {
            MessageBoxes.Error("Two images are to be written into the same partition.");
            return;
        }
        StringBuilder question = new StringBuilder("Write into the flash of " + (_Session.Title ?? "the server") + ":" + Environment.NewLine);
        foreach (ImageRow row in rows)
        {
            question.Append(Environment.NewLine).Append("  ").Append(row.Source).Append(" (").Append(row.SizeText).Append(")  ->  ").Append(row.Target.Partition.Label);
        }
        List<string> warnings = rows.SelectMany(r => r.Warnings).Distinct().ToList();
        if (warnings.Count > 0)
        {
            question.Append(Environment.NewLine).Append(Environment.NewLine).Append("Mind:");
            foreach (string warning in warnings)
            {
                question.Append(Environment.NewLine).Append("  - ").Append(warning);
            }
        }
        question.Append(Environment.NewLine).Append(Environment.NewLine).Append("Each image is checked here, in RAM and after the write. Do not switch the device off until all of them are done.");
        if (!MessageBoxes.AskWarn(question.ToString()))
        {
            return;
        }
        List<ImageRow> dangerous = rows.Where(r => MtdFlash.RiskNote(r.Target.Partition) is string risk && (risk.Contains("boot loader:") || risk.Contains("calibration"))).ToList();
        if (dangerous.Count > 0 && !MessageBoxes.AskWarn("Really write " + string.Join(", ", dangerous.Select(r => r.Target.Partition.Label)) + "?" + Environment.NewLine + Environment.NewLine
            + string.Join(Environment.NewLine, dangerous.Select(r => r.Target.Partition.Label + ": " + MtdFlash.RiskNote(r.Target.Partition)))))
        {
            return;
        }
        StartJob(rows.Sum(r => r.Part.Size), () => RunRestore(session, rows));
    }

    private void RunRestore(SshSession session, List<ImageRow> rows)
    {
        string result;
        bool ok = false;
        int done = 0;
        try
        {
            foreach (ImageRow row in rows)
            {
                MtdImagePart part = row.Part;
                MtdPartition partition = row.Target.Partition;
                Stream Open()
                {
                    FileStream file = new FileStream(row.ImagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                    file.Seek(part.FileOffset, SeekOrigin.Begin);
                    return file;
                }
                Interlocked.Exchange(ref _Total, part.Size);
                MtdRestoreJob.Restore(_Shell, () => QueryInfo(_Shell), part, Open, partition, _UseSudo, Guid.NewGuid().ToString("N").Substring(0, 12),
                    (step, text) =>
                    {
                        // From the write on, the job runs to its end: the read-back and the removal of the RAM image are part of it.
                        _Writing = step >= MtdRestoreStep.Writing;
                        Interlocked.Exchange(ref _Done, 0);
                        SetPhase(text);
                        Dispatcher.BeginInvoke((Action)UpdateButtons);
                    },
                    bytes => Interlocked.Exchange(ref _Done, bytes), () => _CancelRequested && !_Writing, Log);
                _Writing = false;
                done++;
            }
            ok = true;
            result = "Written and checked: " + string.Join(", ", rows.Select(r => r.Target.Partition.Label)) + ". Restart the device when you are ready.";
        }
        catch (OperationCanceledException)
        {
            result = "Stopped before the write: " + (done > 0 ? done + " of " + rows.Count + " images were written and checked; " : "") + "the next partition was not touched.";
        }
        catch (Exception exception)
        {
            result = exception.Message + (done > 0 ? Environment.NewLine + done + " of " + rows.Count + " images were written and checked before." : "");
        }
        _Writing = false;
        FinishJob(result, ok, false);
    }

    #endregion

    #region Running a job

    private void StartJob(long total, Action job)
    {
        _CancelRequested = false;
        _Writing = false;
        _Shell = new SshRemoteShell(_Session.Session);
        Interlocked.Exchange(ref _Done, 0);
        Interlocked.Exchange(ref _Total, Math.Max(1, total));
        _Phase = "Starting...";
        _Clock = Stopwatch.StartNew();
        LogBox.Clear();
        OpenFolderButton.IsEnabled = false;
        _Running = true;
        _ProgressTimer.Start();
        UpdateButtons();
        Task.Run(job);
    }

    private void FinishJob(string result, bool ok, bool savedFiles)
    {
        Dispatcher.BeginInvoke((Action)(() =>
        {
            if (_Closed)
            {
                return;
            }
            _Running = false;
            _ProgressTimer.Stop();
            Progress.IsIndeterminate = false;
            Progress.Value = ok ? 100 : 0;
            RateText.Text = _Clock != null ? FormatElapsed(_Clock.Elapsed) : "";
            Log(result);
            StatusText.Text = result.Split('\n')[0].Trim();
            OpenFolderButton.IsEnabled = savedFiles && _SavedFolder != null;
            UpdateButtons();
            if (!ok && !result.StartsWith("Stopped", StringComparison.Ordinal))
            {
                MessageBoxes.Error(result);
            }
            else if (ok)
            {
                MessageBoxes.Info(result);
            }
            // The memory and the RAM file systems have changed: show them as they are now.
            ReadHost(null);
        }));
    }

    private void SetPhase(string text)
    {
        _Phase = text;
    }

    private void ShowProgress()
    {
        if (!_Running)
        {
            return;
        }
        StatusText.Text = _Phase;
        long done = Interlocked.Read(ref _Done);
        long total = Interlocked.Read(ref _Total);
        if (done > 0)
        {
            Progress.IsIndeterminate = false;
            Progress.Value = Math.Min(100.0, done * 100.0 / Math.Max(1, total));
            RateText.Text = MtdFlash.FormatBytes(done) + " of " + MtdFlash.FormatBytes(total) + ", " + FormatElapsed(_Clock.Elapsed);
        }
        else
        {
            Progress.IsIndeterminate = true;
            RateText.Text = FormatElapsed(_Clock.Elapsed);
        }
        StopButton.IsEnabled = _Running && !_CancelRequested && !_Writing;
    }

    private void UpdateButtons()
    {
        if (CloseButton == null)
        {
            // Called by the Checked event of a radio button while InitializeComponent is still creating the window.
            return;
        }
        bool idle = !_Running && _Info != null;
        BackupButton.IsEnabled = idle && (FullFlashMode.IsChecked == true ? _Layout != null : _PartitionRows.Any(r => r.IsChecked));
        WriteButton.IsEnabled = idle && _ImageRows.Any(r => r.IsChecked) && _Info?.HashAlgorithm != null;
        foreach (UIElement element in new UIElement[] { PartitionList, PartitionsMode, FullFlashMode, FolderBox, FolderButton, CheckAllButton, CheckNoneButton, ImageList, OpenImageButton })
        {
            element.IsEnabled = !_Running;
        }
        if (_Layout == null)
        {
            FullFlashMode.IsEnabled = false;
        }
        StopButton.IsEnabled = _Running && !_CancelRequested && !_Writing;
        StopButton.ToolTip = _Writing ? "The flash is being written: that is never interrupted" : "Stop the running job; a write into the flash itself is never interrupted";
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        RequestCancel();
    }

    private void RequestCancel()
    {
        if (!_Running || _Writing)
        {
            return;
        }
        _CancelRequested = true;
        _Phase = "Stopping...";
        StopButton.IsEnabled = false;
        _Shell?.Abandon();
    }

    private void Log(string line)
    {
        Dispatcher.BeginInvoke((Action)(() =>
        {
            if (_Closed)
            {
                return;
            }
            LogBox.AppendText((LogBox.Text.Length > 0 ? Environment.NewLine : "") + line);
            LogBox.ScrollToEnd();
        }));
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_SavedFolder == null)
        {
            return;
        }
        try
        {
            Process.Start("explorer.exe", "\"" + _SavedFolder + "\"");
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

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (!_Running)
        {
            return;
        }
        if (_Writing)
        {
            MessageBoxes.Warning("The flash is being written right now: the window stays open until the write and its check are done.");
            e.Cancel = true;
            return;
        }
        if (!MessageBoxes.Ask("A job is still running. Stop it?"))
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

    private static string FormatElapsed(TimeSpan time)
    {
        return time.TotalHours >= 1 ? ((int)time.TotalHours).ToString(CultureInfo.InvariantCulture) + time.ToString(@"\:mm\:ss", CultureInfo.InvariantCulture) : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    #endregion
}
