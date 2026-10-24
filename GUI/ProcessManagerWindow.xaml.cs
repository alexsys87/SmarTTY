using SmarTTY.Forms;
using SmarTTY.GUI.Backend;
using SmarTTY.IO;
using SmarTTY.Ssh;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SmarTTY.GUI;

/// <summary>
/// Tools > Process manager: the processes of the server of the active SSH session (see <see cref="RemoteProcessList"/>), with
/// the processor share and the memory of each shown as bars, the load of the whole server (processor, memory, disk activity
/// and disk space) above the list, a filter, and commands to end or kill the selected one. The list is read in
/// the background every few seconds while the window is open; the rows are updated in place, so the selection and the scroll
/// position stay where they are.
/// </summary>
public partial class ProcessManagerWindow : Window
{
    /// <summary>The colours of the bars: green while there is room, amber when it gets full, red when it is nearly all used.</summary>
    private static class MeterBrushes
    {
        public static readonly Brush Low = Make(0x3F, 0xA3, 0x5B);

        public static readonly Brush Mid = Make(0xE0, 0xA1, 0x1B);

        public static readonly Brush High = Make(0xD9, 0x4F, 0x4A);

        public static readonly Brush Memory = Make(0x4A, 0x90, 0xD9);

        public static Brush ForLoad(double percent)
        {
            return percent >= 85 ? High : percent >= 60 ? Mid : Low;
        }

        private static Brush Make(byte red, byte green, byte blue)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>A row of the list; the numbers are separate properties so that the list sorts by them, not by their text.</summary>
    public class ProcessRow : INotifyPropertyChanged
    {
        private string _User;

        private string _State;

        private string _Command;

        private double? _Cpu;

        private long _MemoryKb;

        private long _MemoryTotalKb;

        public int Pid { get; }

        public string Name { get; private set; }

        public string User => _User;

        public string State => _State;

        public string StateDescription => RemoteProcessList.DescribeState(_State);

        public string Command => _Command;

        /// <summary>Sorted as a number; a process without a value yet (the first list) counts as 0.</summary>
        public double CpuPercent => _Cpu ?? 0;

        public string CpuText => _Cpu.HasValue ? _Cpu.Value.ToString("0.0", CultureInfo.InvariantCulture) : "";

        /// <summary>The length of the bar: 100 is one whole core (a process that uses several cores shows a full bar, the text has the number).</summary>
        public double CpuBar => Math.Min(100.0, _Cpu ?? 0);

        public Brush CpuBrush => MeterBrushes.ForLoad(CpuBar);

        public string CpuToolTip => _Cpu.HasValue ? CpuText + " % of one processor core (100 is one whole core)" : "Measured at the next reading";

        public long MemoryKb => _MemoryKb;

        public string MemoryText => _MemoryKb > 0 ? RemoteProcessList.FormatKb(_MemoryKb) : "";

        /// <summary>The share of all the memory of the server that the process holds, 0 to 100.</summary>
        public double MemoryPercent => _MemoryTotalKb > 0 ? Math.Min(100.0, _MemoryKb * 100.0 / _MemoryTotalKb) : 0;

        public Brush MemoryBrush => MeterBrushes.Memory;

        public string MemoryToolTip => _MemoryKb > 0 && _MemoryTotalKb > 0
            ? RemoteProcessList.FormatKb(_MemoryKb) + " in memory, " + MemoryPercent.ToString("0.0", CultureInfo.InvariantCulture) + " % of " + RemoteProcessList.FormatKb(_MemoryTotalKb)
            : "";

        public event PropertyChangedEventHandler PropertyChanged;

        public ProcessRow(RemoteProcess process, long? memoryTotalKb)
        {
            Pid = process.Pid;
            Update(process, memoryTotalKb);
        }

        public void Update(RemoteProcess process, long? memoryTotalKb)
        {
            _MemoryTotalKb = memoryTotalKb ?? 0;
            Name = process.Name;
            _User = process.User;
            _State = process.State;
            _Command = process.Command;
            _Cpu = process.CpuPercent;
            _MemoryKb = process.MemoryKb;
            foreach (string property in new[] { "Name", "User", "State", "StateDescription", "Command", "CpuPercent", "CpuText", "CpuBar", "CpuBrush", "CpuToolTip", "MemoryKb", "MemoryText", "MemoryPercent", "MemoryToolTip" })
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
            }
        }
    }

    private static readonly Dictionary<SessionWrapper, ProcessManagerWindow> _Open = new Dictionary<SessionWrapper, ProcessManagerWindow>();

    private static readonly Dictionary<string, string> _SortProperties = new Dictionary<string, string>
    {
        ["PID"] = nameof(ProcessRow.Pid),
        ["User"] = nameof(ProcessRow.User),
        ["CPU %"] = nameof(ProcessRow.CpuPercent),
        ["Memory"] = nameof(ProcessRow.MemoryKb),
        ["State"] = nameof(ProcessRow.State),
        ["Name"] = nameof(ProcessRow.Name),
        ["Command"] = nameof(ProcessRow.Command)
    };

    private readonly SessionWrapper _Session;

    private readonly ObservableCollection<ProcessRow> _Rows = new ObservableCollection<ProcessRow>();

    private readonly Dictionary<int, ProcessRow> _ByPid = new Dictionary<int, ProcessRow>();

    private readonly ListCollectionView _View;

    private readonly DispatcherTimer _Timer;

    private ProcessSnapshot _Snapshot;

    private bool _Busy;

    private bool _Closed;

    private string _Filter = "";

    /// <summary>Opens the window for the session, or brings the one that is open to the front.</summary>
    public static void ShowFor(SessionWrapper session, Window owner)
    {
        if (_Open.TryGetValue(session, out ProcessManagerWindow open))
        {
            open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }
        ProcessManagerWindow window = new ProcessManagerWindow(session) { Owner = owner };
        _Open[session] = window;
        window.Show();
    }

    private ProcessManagerWindow(SessionWrapper session)
    {
        InitializeComponent();
        _Session = session;
        Title = "Process manager - " + (session.Title ?? "SSH");
        _View = (ListCollectionView)CollectionViewSource.GetDefaultView(_Rows);
        _View.IsLiveSorting = true;
        _View.LiveSortingProperties.Add(nameof(ProcessRow.CpuPercent));
        _View.LiveSortingProperties.Add(nameof(ProcessRow.MemoryKb));
        _View.LiveSortingProperties.Add(nameof(ProcessRow.User));
        _View.LiveSortingProperties.Add(nameof(ProcessRow.State));
        _View.LiveSortingProperties.Add(nameof(ProcessRow.Name));
        _View.LiveSortingProperties.Add(nameof(ProcessRow.Command));
        _View.SortDescriptions.Add(new SortDescription(nameof(ProcessRow.CpuPercent), ListSortDirection.Descending));
        _View.SortDescriptions.Add(new SortDescription(nameof(ProcessRow.Pid), ListSortDirection.Ascending));
        _View.Filter = MatchesFilter;
        ProcessList.ItemsSource = _View;
        _Timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _Timer.Tick += (sender, e) => Refresh();
        _Timer.Start();
        Closed += (sender, e) =>
        {
            _Closed = true;
            _Timer.Stop();
            _Open.Remove(_Session);
        };
        Loaded += (sender, e) => Refresh();
    }

    private bool MatchesFilter(object item)
    {
        if (_Filter.Length == 0 || !(item is ProcessRow row))
        {
            return true;
        }
        return row.Pid.ToString(CultureInfo.InvariantCulture).Contains(_Filter)
            || Contains(row.User, _Filter) || Contains(row.Name, _Filter) || Contains(row.Command, _Filter);
    }

    private static bool Contains(string text, string part)
    {
        return text != null && text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>Reads the list on a pool thread (it takes a moment on a small device) unless that is still going on.</summary>
    private void Refresh()
    {
        if (_Busy || _Closed)
        {
            return;
        }
        SshSession session = _Session.Session;
        if (session == null)
        {
            StatusText.Text = "The SSH session is not connected.";
            return;
        }
        _Busy = true;
        ProcessSnapshot previous = _Snapshot;
        Task.Run(() =>
        {
            List<string> lines = null;
            string error = null;
            try
            {
                lines = session.RunCommand(RemoteProcessList.ListCommand, out int? exitStatus);
                if (lines == null)
                {
                    session.ThrowIfConnectionLost();
                    error = "The command could not be run on the server.";
                }
            }
            catch (Exception exception)
            {
                error = exception.Message;
            }
            ProcessSnapshot snapshot = error == null ? RemoteProcessList.Parse(lines, previous) : null;
            if (snapshot != null && snapshot.Processes.Count == 0)
            {
                snapshot = null;
                error = "The server did not list any process (is /proc available?).";
            }
            Dispatcher.BeginInvoke((Action)(() =>
            {
                _Busy = false;
                if (_Closed)
                {
                    return;
                }
                if (snapshot == null)
                {
                    StatusText.Text = error;
                    return;
                }
                Apply(snapshot);
            }));
        });
    }

    private void Apply(ProcessSnapshot snapshot)
    {
        bool first = _Snapshot == null;
        _Snapshot = snapshot;
        HashSet<int> alive = new HashSet<int>();
        foreach (RemoteProcess process in snapshot.Processes)
        {
            alive.Add(process.Pid);
            if (_ByPid.TryGetValue(process.Pid, out ProcessRow row))
            {
                row.Update(process, snapshot.MemTotalKb);
            }
            else
            {
                row = new ProcessRow(process, snapshot.MemTotalKb);
                _ByPid[process.Pid] = row;
                _Rows.Add(row);
            }
        }
        foreach (int gone in _ByPid.Keys.Where(pid => !alive.Contains(pid)).ToList())
        {
            _Rows.Remove(_ByPid[gone]);
            _ByPid.Remove(gone);
        }
        UpdateMeters();
        UpdateStatus();
        if (first)
        {
            // The shares are a difference of two readings: take the second soon instead of leaving the bars empty for the whole interval.
            DispatcherTimer soon = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            soon.Tick += (sender, e) =>
            {
                soon.Stop();
                Refresh();
            };
            soon.Start();
        }
    }

    /// <summary>Fills the meters of the whole server above the list from the last reading.</summary>
    private void UpdateMeters()
    {
        ProcessSnapshot s = _Snapshot;
        if (s == null)
        {
            return;
        }
        string measuring = "measuring...";

        if (s.CpuTotalPercent.HasValue)
        {
            SetMeter(CpuMeter, CpuValueText, s.CpuTotalPercent.Value, MeterBrushes.ForLoad(s.CpuTotalPercent.Value));
        }
        else
        {
            SetMeter(CpuMeter, CpuValueText, null, MeterBrushes.Low);
        }
        CpuDetailText.Text = s.CpuCount + (s.CpuCount == 1 ? " core" : " cores") + (s.LoadAverage != null ? "  |  load " + s.LoadAverage : "");

        if (s.MemTotalKb.HasValue && s.MemTotalKb.Value > 0)
        {
            long used = Math.Max(0, s.MemTotalKb.Value - (s.MemFreeKb ?? 0));
            double percent = used * 100.0 / s.MemTotalKb.Value;
            SetMeter(MemoryMeter, MemoryValueText, percent, MeterBrushes.ForLoad(percent));
            MemoryDetailText.Text = RemoteProcessList.FormatKb(used) + " of " + RemoteProcessList.FormatKb(s.MemTotalKb.Value)
                + (s.MemCachedKb.HasValue ? "  |  cache " + RemoteProcessList.FormatKb(s.MemCachedKb.Value) : "");
        }
        else
        {
            SetMeter(MemoryMeter, MemoryValueText, null, MeterBrushes.Low, "n/a");
            MemoryDetailText.Text = "the server does not tell";
        }

        if (s.DiskBusyPercent.HasValue)
        {
            SetMeter(DiskMeter, DiskValueText, s.DiskBusyPercent.Value, MeterBrushes.ForLoad(s.DiskBusyPercent.Value));
            DiskDetailText.Text = "read " + RemoteProcessList.FormatRate(s.DiskReadBytesPerSecond ?? 0) + "  |  write " + RemoteProcessList.FormatRate(s.DiskWriteBytesPerSecond ?? 0)
                + (s.BusiestDisk != null ? "  (" + s.BusiestDisk + ")" : "");
        }
        else if (s.Disks.Count == 0)
        {
            SetMeter(DiskMeter, DiskValueText, null, MeterBrushes.Low, "n/a");
            DiskDetailText.Text = "no disks reported";
        }
        else
        {
            SetMeter(DiskMeter, DiskValueText, null, MeterBrushes.Low);
            DiskDetailText.Text = measuring;
        }

        if (s.StorageTotalKb.HasValue && s.StorageTotalKb.Value > 0 && s.StorageUsedKb.HasValue)
        {
            double percent = Math.Min(100.0, s.StorageUsedKb.Value * 100.0 / s.StorageTotalKb.Value);
            SetMeter(StorageMeter, StorageValueText, percent, MeterBrushes.ForLoad(percent));
            StorageDetailText.Text = "/  |  " + RemoteProcessList.FormatKb(s.StorageUsedKb.Value) + " of " + RemoteProcessList.FormatKb(s.StorageTotalKb.Value);
        }
        else
        {
            SetMeter(StorageMeter, StorageValueText, null, MeterBrushes.Low, "n/a");
            StorageDetailText.Text = "df did not answer";
        }
    }

    /// <summary>Sets a bar and the value above it; no value (the first reading) shows an empty bar and the placeholder.</summary>
    private static void SetMeter(ProgressBar bar, TextBlock valueText, double? percent, Brush brush, string placeholder = "...")
    {
        bar.Value = percent ?? 0;
        bar.Foreground = brush;
        valueText.Text = percent.HasValue ? percent.Value.ToString("0.0", CultureInfo.InvariantCulture) + " %" : placeholder;
    }

    private void UpdateStatus()
    {
        if (_Snapshot == null)
        {
            return;
        }
        List<string> parts = new List<string>
        {
            _View.Count == _Rows.Count ? _Rows.Count + " processes" : _View.Count + " of " + _Rows.Count + " processes"
        };
        if (_Snapshot.LoadAverage != null)
        {
            parts.Add("load " + _Snapshot.LoadAverage);
        }
        if (_Snapshot.UptimeSeconds.HasValue)
        {
            TimeSpan uptime = TimeSpan.FromSeconds(_Snapshot.UptimeSeconds.Value);
            parts.Add("up " + (uptime.Days > 0 ? uptime.Days + " d " : "") + uptime.Hours + " h " + uptime.Minutes + " min");
        }
        StatusText.Text = string.Join("  |  ", parts);
    }

    private ProcessRow Selected => ProcessList.SelectedItem as ProcessRow;

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        Refresh();
    }

    private void AutoRefresh_Changed(object sender, RoutedEventArgs e)
    {
        // The checkbox raises Checked while the window is still being built.
        if (_Timer == null)
        {
            return;
        }
        if (AutoRefreshBox.IsChecked == true)
        {
            _Timer.Start();
        }
        else
        {
            _Timer.Stop();
        }
    }

    private void Filter_TextChanged(object sender, TextChangedEventArgs e)
    {
        _Filter = FilterBox.Text.Trim();
        _View.Refresh();
        UpdateStatus();
    }

    private void Header_Click(object sender, RoutedEventArgs e)
    {
        if (!(e.OriginalSource is GridViewColumnHeader header) || header.Role == GridViewColumnHeaderRole.Padding
            || !(header.Content is string title) || !_SortProperties.TryGetValue(title, out string property))
        {
            return;
        }
        // The same column again turns the order round; a new one starts with the largest numbers and the first letters.
        SortDescription current = _View.SortDescriptions.Count > 0 ? _View.SortDescriptions[0] : default;
        bool numeric = property == nameof(ProcessRow.CpuPercent) || property == nameof(ProcessRow.MemoryKb);
        ListSortDirection direction = current.PropertyName == property
            ? (current.Direction == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending)
            : (numeric ? ListSortDirection.Descending : ListSortDirection.Ascending);
        _View.SortDescriptions.Clear();
        _View.SortDescriptions.Add(new SortDescription(property, direction));
        if (property != nameof(ProcessRow.Pid))
        {
            _View.SortDescriptions.Add(new SortDescription(nameof(ProcessRow.Pid), ListSortDirection.Ascending));
        }
    }

    private void ProcessList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool canStop = Selected != null && Selected.Pid > 1;
        EndButton.IsEnabled = canStop;
        KillButton.IsEnabled = canStop;
    }

    private void ContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        bool canStop = Selected != null && Selected.Pid > 1;
        EndMenuItem.IsEnabled = canStop;
        KillMenuItem.IsEnabled = canStop;
        CopyMenuItem.IsEnabled = Selected != null;
    }

    private void ProcessList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            Refresh();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && Selected != null)
        {
            Stop(Selected, force: (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
            e.Handled = true;
        }
    }

    private void End_Click(object sender, RoutedEventArgs e)
    {
        Stop(Selected, force: false);
    }

    private void Kill_Click(object sender, RoutedEventArgs e)
    {
        Stop(Selected, force: true);
    }

    private void CopyCommand_Click(object sender, RoutedEventArgs e)
    {
        if (Selected != null)
        {
            try
            {
                Clipboard.SetText(Selected.Command);
            }
            catch (Exception)
            {
                // The clipboard is in use by another program.
            }
        }
    }

    /// <summary>Asks, then sends the signal on a pool thread and reads the list again; a refusal is shown with the server's words.</summary>
    private void Stop(ProcessRow row, bool force)
    {
        SshSession session = _Session.Session;
        if (row == null || session == null)
        {
            return;
        }
        if (row.Pid <= 1)
        {
            MessageBoxes.Info("Process 1 starts the system; it cannot be ended from here.");
            return;
        }
        string what = "process " + row.Pid + " (" + row.Name + ", user " + row.User + ")";
        string question = force
            ? "Kill " + what + " at once?\n\nIt gets no chance to save anything or to clean up (kill -9)."
            : "End " + what + "?\n\nThe program is asked to stop (kill -15) and may save its work first.";
        if (!MessageBoxes.AskWarn(question))
        {
            return;
        }
        int pid = row.Pid;
        Task.Run(() =>
        {
            string error = null;
            try
            {
                List<string> output = session.RunCommand(RemoteProcessList.SignalCommand(pid, force), out int? exitStatus);
                if (exitStatus != 0)
                {
                    // No exit status because the connection went away: the signal may or may not have been sent.
                    session.ThrowIfConnectionLost();
                    string text = string.Join("\n", (output ?? new List<string>()).Where(line => line.Trim().Length > 0));
                    error = text.Length > 0 ? text : "The server refused (exit status " + (exitStatus?.ToString(CultureInfo.InvariantCulture) ?? "unknown") + ").";
                    if (text.IndexOf("not permitted", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        error += "\n\nThe process belongs to another user: only that user or root can stop it.";
                    }
                }
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
                if (error != null)
                {
                    MessageBoxes.Error("Cannot stop process " + pid + ":\n" + error);
                }
                // A short wait: a program that was asked to end needs a moment to do it.
                DispatcherTimer once = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(force ? 300 : 900) };
                once.Tick += (sender, e) =>
                {
                    once.Stop();
                    Refresh();
                };
                once.Start();
            }));
        });
    }
}
