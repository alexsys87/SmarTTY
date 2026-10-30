using SmarTTY.GUI.Backend;
using SmarTTY.Ssh;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace SmarTTY.GUI;

/// <summary>
/// Tools > Port forwarding: the local (ssh -L) and remote (ssh -R) forwardings of the active SSH session (see
/// <see cref="PortForwardHost"/>). One window per session; the forwardings keep running after it is closed, until they are
/// stopped or the session ends. The list is saved with the profile whenever it changes.
/// </summary>
public partial class PortForwardingWindow : Window
{
    /// <summary>One line of the list.</summary>
    public sealed class Row : INotifyPropertyChanged
    {
        private readonly PortForwardingWindow _Window;

        public Row(PortForwardingWindow window, PortForwardHost.Entry entry)
        {
            _Window = window;
            Entry = entry;
        }

        public PortForwardHost.Entry Entry { get; }

        public event PropertyChangedEventHandler PropertyChanged;

        public bool StartOnConnect
        {
            get
            {
                return Entry.Rule.StartOnConnect;
            }
            set
            {
                if (value != Entry.Rule.StartOnConnect)
                {
                    _Window.SetStartOnConnect(this, value);
                }
            }
        }

        public string KindText => Entry.Rule.Kind == PortForwardKind.Local ? "Local (-L)" : "Remote (-R)";

        public string ListenText => Entry.Rule.ListenText;

        public string TargetText => Entry.Rule.TargetText;

        public string Description => Entry.Rule.Description;

        public string StatusText => Entry.StatusText;

        public bool IsActive => Entry.IsActive;

        public bool IsFailed => !Entry.IsActive && !Entry.Wanted && !string.IsNullOrEmpty(Entry.LastError);

        /// <summary>Tells the list that the entry may have changed (its state follows the connection).</summary>
        public void Refresh()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
    }

    private const int MaxLogCharacters = 200000;

    private static readonly Dictionary<SessionWrapper, PortForwardingWindow> _Open = new Dictionary<SessionWrapper, PortForwardingWindow>();

    private readonly SessionWrapper _Session;

    private readonly PortForwardHost _Host;

    private readonly ObservableCollection<Row> _Rows = new ObservableCollection<Row>();

    private readonly DispatcherTimer _Timer;

    private bool _Busy;

    /// <summary>Opens the window for the session, or brings the one that is open to the front.</summary>
    public static void ShowFor(SessionWrapper session, Window owner)
    {
        if (_Open.TryGetValue(session, out PortForwardingWindow open))
        {
            open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }
        PortForwardingWindow window = new PortForwardingWindow(session) { Owner = owner };
        _Open[session] = window;
        window.Show();
    }

    private PortForwardingWindow(SessionWrapper session)
    {
        InitializeComponent();
        _Session = session;
        _Host = session.PortForwards;
        Title = "Port forwarding - " + (session.Title ?? "SSH");
        // The profile's list, also before the first connection has loaded it (the window may be opened while connecting).
        _Host.Load(session.Settings?.PortForwards);
        ForwardList.ItemsSource = _Rows;

        StringBuilder history = new StringBuilder();
        foreach (string line in _Host.GetHistory())
        {
            history.AppendLine(line);
        }
        LogBox.Text = history.ToString();
        LogBox.ScrollToEnd();

        _Host.Log += Host_Log;
        _Host.Changed += Host_Changed;
        // A lost connection changes the state of the forwardings without an event of the host.
        _Timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (sender, e) => RefreshRows(), Dispatcher);
        _Timer.Start();
        Closed += (sender, e) =>
        {
            _Timer.Stop();
            _Host.Log -= Host_Log;
            _Host.Changed -= Host_Changed;
            _Open.Remove(_Session);
        };
        SyncRows();
    }

    private Row Selected => ForwardList.SelectedItem as Row;

    private void Host_Log(string line)
    {
        Dispatcher.BeginInvoke((Action)(() =>
        {
            if (LogBox.Text.Length > MaxLogCharacters)
            {
                // Whole lines are dropped, so that the log does not start in the middle of one.
                string text = LogBox.Text;
                int cut = text.IndexOf('\n', text.Length - MaxLogCharacters / 2);
                LogBox.Text = cut < 0 ? "" : text.Substring(cut + 1);
            }
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        }));
    }

    private void Host_Changed()
    {
        Dispatcher.BeginInvoke((Action)SyncRows);
    }

    // Brings the rows in line with the entries of the host (added, removed, changed), keeping the selection.
    private void SyncRows()
    {
        PortForwardHost.Entry[] entries = _Host.Entries;
        PortForwardHost.Entry selected = Selected?.Entry;
        for (int i = _Rows.Count - 1; i >= 0; i--)
        {
            if (!entries.Contains(_Rows[i].Entry))
            {
                _Rows.RemoveAt(i);
            }
        }
        for (int i = 0; i < entries.Length; i++)
        {
            int index = IndexOf(entries[i]);
            if (index < 0)
            {
                _Rows.Insert(i, new Row(this, entries[i]));
            }
            else if (index != i)
            {
                _Rows.Move(index, i);
            }
        }
        if (selected != null && Selected?.Entry != selected)
        {
            int index = IndexOf(selected);
            ForwardList.SelectedIndex = index;
        }
        RefreshRows();
    }

    private int IndexOf(PortForwardHost.Entry entry)
    {
        for (int i = 0; i < _Rows.Count; i++)
        {
            if (_Rows[i].Entry == entry)
            {
                return i;
            }
        }
        return -1;
    }

    private void RefreshRows()
    {
        foreach (Row row in _Rows)
        {
            row.Refresh();
        }
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        Row row = Selected;
        bool idle = !_Busy;
        bool connected = _Session.Session != null && !_Session.Session.IsConnectionClosed;
        EditButton.IsEnabled = row != null && idle;
        RemoveButton.IsEnabled = row != null && idle;
        StartButton.IsEnabled = row != null && idle && connected && !row.Entry.Wanted;
        StopButton.IsEnabled = row != null && idle && row.Entry.Wanted;
        CopyButton.IsEnabled = row != null;
        int active = _Rows.Count(r => r.IsActive);
        if (_Busy)
        {
            StatusText.Text = "Working...";
        }
        else if (!connected)
        {
            StatusText.Text = "The SSH connection is down: running forwardings are restored when it is re-established.";
        }
        else
        {
            StatusText.Text = _Rows.Count == 0 ? "No forwardings yet: Add creates one." : active + " of " + _Rows.Count + " forwarding(s) active.";
        }
    }

    private void ForwardList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateButtons();
    }

    private void ForwardList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected != null && EditButton.IsEnabled)
        {
            Edit_Click(sender, e);
        }
    }

    private void ForwardList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && RemoveButton.IsEnabled)
        {
            e.Handled = true;
            Remove_Click(sender, e);
        }
        else if (e.Key == Key.Enter && EditButton.IsEnabled)
        {
            e.Handled = true;
            Edit_Click(sender, e);
        }
    }

    internal void SetStartOnConnect(Row row, bool value)
    {
        _Host.SetStartOnConnect(row.Entry, value);
        Save();
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        PortForwardRule rule = PortForwardEditWindow.Edit(this, new PortForwardRule(), isNew: true);
        if (rule == null)
        {
            return;
        }
        PortForwardHost.Entry entry = _Host.Add(rule);
        Save();
        SyncRows();
        ForwardList.SelectedIndex = IndexOf(entry);
        // A new forwarding is meant to be used: start it at once when connected.
        await StartAsync(entry);
    }

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        Row row = Selected;
        if (row == null)
        {
            return;
        }
        PortForwardRule rule = PortForwardEditWindow.Edit(this, row.Entry.Rule, isNew: false);
        if (rule == null)
        {
            return;
        }
        bool wasRunning = row.Entry.Wanted;
        await RunAsync(() => _Host.Replace(row.Entry, rule));
        Save();
        if (wasRunning)
        {
            await StartAsync(row.Entry);
        }
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        Row row = Selected;
        if (row == null || !MessageBoxes.Ask("Remove the forwarding " + row.Entry.Rule.SshOption + "?" + (row.Entry.Wanted ? " It is stopped and the connections it carries are closed." : "")))
        {
            return;
        }
        await RunAsync(() => _Host.Remove(row.Entry));
        Save();
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        Row row = Selected;
        if (row != null)
        {
            await StartAsync(row.Entry);
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        Row row = Selected;
        if (row != null)
        {
            await RunAsync(() => _Host.Stop(row.Entry));
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Row row = Selected;
        if (row == null)
        {
            return;
        }
        try
        {
            Clipboard.SetText(row.Entry.Rule.SshOption);
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

    private async Task StartAsync(PortForwardHost.Entry entry)
    {
        SshSession ssh = _Session.Session;
        if (ssh == null || ssh.IsConnectionClosed)
        {
            return;
        }
        try
        {
            await RunAsync(() => _Host.Start(ssh, entry), showErrors: false);
        }
        catch (Exception exception)
        {
            // The reason is also shown in the list and the log.
            MessageBoxes.Error("The forwarding " + entry.Rule.SshOption + " could not be started:\n\n" + exception.Message);
        }
    }

    // Runs a blocking operation of the host off the UI thread (stopping waits for the forwarded connections to close).
    private async Task RunAsync(Action action, bool showErrors = true)
    {
        _Busy = true;
        UpdateButtons();
        try
        {
            await Task.Run(action);
        }
        catch (Exception exception) when (showErrors)
        {
            MessageBoxes.Exception(exception);
        }
        finally
        {
            _Busy = false;
            SyncRows();
        }
    }

    private void Save()
    {
        try
        {
            _Session.SavePortForwards();
        }
        catch (Exception exception)
        {
            _Host.Report("The list could not be saved with the profile: " + exception.Message);
        }
    }
}
