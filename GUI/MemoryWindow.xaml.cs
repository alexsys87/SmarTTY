using SmarTTY.GUI.Backend;
using SmarTTY.Ssh;
using SmarTTY.Transport;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using VisualTTY;

namespace SmarTTY.GUI;

/// <summary>
/// Tools > Memory (devmem): a table of registers of the physical address space of the server, read and written with devmem
/// (BusyBox) or devmem2 (see <see cref="DevMem"/>). Every value is shown in the forms chosen (hex, decimal, signed, binary,
/// ASCII, float); the checked rows are read again by a timer and a value that changes is highlighted for a while and counted.
/// The selected register is written as a whole, by a bit field, by a mask (set, clear, toggle) or bit by bit; every write is read
/// back, logged and can be undone. The list, the names of the registers and the settings are kept in MemoryViewer.xml.
/// Made for the debugging of drivers and of the PL of a Zynq (or any SoC).
/// </summary>
public partial class MemoryWindow : Window
{
    private static readonly Dictionary<SessionWrapper, MemoryWindow> _Open = new Dictionary<SessionWrapper, MemoryWindow>();

    /// <summary>Addresses read by one command: the command line stays far below the limits of the SSH server.</summary>
    private const int Batch = 256;

    private readonly SessionWrapper _Session;

    private readonly DevMemTool _Tool;

    private readonly ObservableCollection<MemoryRow> _Rows = new ObservableCollection<MemoryRow>();

    private readonly DispatcherTimer _RefreshTimer;

    private readonly DispatcherTimer _HighlightTimer;

    // One command on the server at a time: a write never runs in the middle of a refresh.
    private readonly object _CommandLock = new object();

    private readonly Stack<WriteRecord> _Undo = new Stack<WriteRecord>();

    private readonly Dictionary<string, GridViewColumn> _OptionalColumns = new Dictionary<string, GridViewColumn>();

    private readonly List<GridViewColumn> _FixedColumns = new List<GridViewColumn>();

    private GridViewColumn _ErrorColumn;

    private bool _UseSudo;

    private bool _Closed;

    private volatile bool _Refreshing;

    private MemoryRow _BitRow;

    private string _ListFolder;

    private sealed class WriteRecord
    {
        public MemoryRow Row;

        public ulong OldValue;
    }

    private sealed class RegionItem
    {
        public MemoryRegion Region;

        public string Text;

        public override string ToString()
        {
            return Text;
        }
    }

    /// <summary>Opens the window for the session, or brings the one that is open to the front.</summary>
    public static void ShowFor(SessionWrapper session, Window owner)
    {
        if (_Open.TryGetValue(session, out MemoryWindow open))
        {
            open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }
        MemoryWindow window = new MemoryWindow(session) { Owner = owner };
        _Open[session] = window;
        window.Show();
    }

    private MemoryWindow(SessionWrapper session)
    {
        InitializeComponent();
        _Session = session;
        _Tool = session.DevMemTool;
        Title = "Memory (devmem) - " + (session.Title ?? "SSH");
        foreach (int width in DevMem.Widths(_Tool))
        {
            WidthBox.Items.Add(width + " bits");
        }
        WidthBox.SelectedIndex = Array.IndexOf(DevMem.Widths(_Tool), 32);
        BuildColumns();
        RowList.ItemsSource = _Rows;
        _RefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        _RefreshTimer.Tick += (sender, e) => RefreshWatched();
        _HighlightTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _HighlightTimer.Tick += (sender, e) => UpdateHighlights();
        _HighlightTimer.Start();
        FieldBox.TextChanged += (sender, e) => ShowFieldInfo();
        PreviewKeyDown += Window_PreviewKeyDown;
        Closed += Window_Closed;
        Loaded += (sender, e) => Start();
        AddRegions(null);
    }

    #region Start and end

    private string WorkspacePath => Path.Combine(GlobalSettings.DataDirectory, MemoryWorkspace.FileName);

    private void Start()
    {
        // The list of the last time, without reading it: the PL may not be configured yet, and an access to it would hang.
        try
        {
            if (File.Exists(WorkspacePath))
            {
                ApplyWorkspace(MemoryWorkspace.Load(WorkspacePath), "the list of the last time");
            }
        }
        catch (Exception exception)
        {
            Log("The list of the last time cannot be read: " + exception.Message);
        }
        SshSession session = _Session.Session;
        if (session == null)
        {
            HostText.Text = "The SSH session is not connected.";
            return;
        }
        string tool = DevMem.ToolName(_Tool) ?? "none";
        Task.Run(() =>
        {
            bool? isRoot = null;
            bool canSudo = false;
            List<MemoryRegion> regions = null;
            try
            {
                isRoot = DevMem.ParseUser(session.RunCommand(SmarTTY.Utilities.PosixShell.Wrap(DevMem.UserCommand), out _, timeout: TimeSpan.FromSeconds(20)), out canSudo);
                bool useSudo = isRoot == false && canSudo;
                regions = DevMem.ParseIomem(session.RunCommand(DevMem.BuildIomemCommand(useSudo), out _, timeout: TimeSpan.FromSeconds(20)));
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
                _UseSudo = isRoot == false && canSudo;
                string user = isRoot == true ? "as root" : _UseSudo ? "through sudo -n (the user is not root)" : isRoot == false ? "as a user that is not root and has no passwordless sudo: /dev/mem will most likely refuse" : "";
                HostText.Text = "Physical memory is read and written with " + tool + " " + user + ".";
                AddRegions(regions);
            }));
        });
    }

    private void Window_Closed(object sender, EventArgs e)
    {
        _Closed = true;
        _RefreshTimer.Stop();
        _HighlightTimer.Stop();
        _Open.Remove(_Session);
        try
        {
            CurrentWorkspace().Save(WorkspacePath);
        }
        catch
        {
            // Keeping the list is a convenience: a read-only settings folder must not stop the window from closing.
        }
    }

    private MemoryWorkspace CurrentWorkspace()
    {
        MemoryWorkspace workspace = MemoryWorkspace.FromRows(_Rows);
        workspace.RefreshMilliseconds = IntervalMilliseconds();
        workspace.HighlightMilliseconds = (int)HighlightDuration().TotalMilliseconds;
        workspace.Columns = string.Join(",", _OptionalColumns.Keys.Where(IsColumnChecked));
        return workspace;
    }

    private void ApplyWorkspace(MemoryWorkspace workspace, string what)
    {
        List<MemoryRow> rows = workspace.ToRows(out List<string> skipped);
        _Rows.Clear();
        foreach (MemoryRow row in rows)
        {
            _Rows.Add(row);
        }
        IntervalBox.Text = Math.Max(100, workspace.RefreshMilliseconds).ToString(CultureInfo.InvariantCulture);
        HighlightBox.Text = (Math.Max(0, workspace.HighlightMilliseconds) / 1000.0).ToString("0.#", CultureInfo.InvariantCulture);
        HashSet<string> columns = new HashSet<string>(workspace.ColumnNames, StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, CheckBox> pair in ColumnBoxes())
        {
            pair.Value.IsChecked = columns.Contains(pair.Key);
        }
        StatusText.Text = rows.Count + " registers from " + what + (rows.Count > 0 ? ". Press Read all (F5) to read them." : ".");
        if (skipped.Count > 0)
        {
            Log("Left out of " + what + ": " + string.Join(", ", skipped));
        }
    }

    #endregion

    #region Columns

    private Dictionary<string, CheckBox> ColumnBoxes()
    {
        return new Dictionary<string, CheckBox>
        {
            ["Hex"] = ColHex,
            ["Unsigned"] = ColUnsigned,
            ["Signed"] = ColSigned,
            ["Binary"] = ColBinary,
            ["Ascii"] = ColAscii,
            ["Float"] = ColFloat,
            ["Changes"] = ColChanges
        };
    }

    private bool IsColumnChecked(string name)
    {
        return ColumnBoxes().TryGetValue(name, out CheckBox box) && box.IsChecked == true;
    }

    private DataTemplate TextTemplate(string path, bool highlight)
    {
        FrameworkElementFactory text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(path));
        if (!highlight)
        {
            return new DataTemplate { VisualTree = text };
        }
        FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(FrameworkElement.StyleProperty, FindResource("CellBorder"));
        border.AppendChild(text);
        return new DataTemplate { VisualTree = border };
    }

    private GridViewColumn TextColumn(string header, string path, double width, bool highlight)
    {
        return new GridViewColumn { Header = header, Width = width, CellTemplate = TextTemplate(path, highlight) };
    }

    private void BuildColumns()
    {
        FrameworkElementFactory watch = new FrameworkElementFactory(typeof(CheckBox));
        watch.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(MemoryRow.Watch)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        watch.SetValue(FrameworkElement.ToolTipProperty, "Read this register in the automatic refresh");
        _FixedColumns.Add(new GridViewColumn { Header = "Auto", Width = 44, CellTemplate = new DataTemplate { VisualTree = watch } });
        _FixedColumns.Add(TextColumn("Address", nameof(MemoryRow.AddressText), 100, false));
        _FixedColumns.Add(TextColumn("Offset", nameof(MemoryRow.OffsetText), 70, false));
        FrameworkElementFactory name = new FrameworkElementFactory(typeof(TextBox));
        name.SetBinding(TextBox.TextProperty, new Binding(nameof(MemoryRow.Name)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        name.SetValue(FrameworkElement.MinWidthProperty, 110.0);
        name.SetValue(FrameworkElement.ToolTipProperty, "A name of the register (kept in the list)");
        name.SetResourceReference(FrameworkElement.StyleProperty, "ThemedTextBox");
        _FixedColumns.Add(new GridViewColumn { Header = "Name", Width = 130, CellTemplate = new DataTemplate { VisualTree = name } });
        _FixedColumns.Add(TextColumn("Bits", nameof(MemoryRow.WidthText), 40, false));
        _OptionalColumns["Hex"] = TextColumn("Hex", nameof(MemoryRow.Hex), 160, true);
        _OptionalColumns["Unsigned"] = TextColumn("Decimal", nameof(MemoryRow.Unsigned), 150, true);
        _OptionalColumns["Signed"] = TextColumn("Signed", nameof(MemoryRow.Signed), 150, true);
        _OptionalColumns["Binary"] = TextColumn("Binary", nameof(MemoryRow.Binary), 330, true);
        _OptionalColumns["Ascii"] = TextColumn("ASCII", nameof(MemoryRow.Ascii), 70, true);
        _OptionalColumns["Float"] = TextColumn("Float", nameof(MemoryRow.Float), 120, true);
        GridViewColumn changes = TextColumn("Changes", nameof(MemoryRow.Changes), 64, true);
        _OptionalColumns["Changes"] = changes;
        _ErrorColumn = TextColumn("Before / changed bits / at / error", nameof(MemoryRow.Error), 300, false);
        // The last column tells what changed: the value before, the bits that changed and when, or the error.
        FrameworkElementFactory panel = new FrameworkElementFactory(typeof(TextBlock));
        MultiBinding info = new MultiBinding { StringFormat = "{0}  {1}  {2}  {3}" };
        info.Bindings.Add(new Binding(nameof(MemoryRow.PreviousText)));
        info.Bindings.Add(new Binding(nameof(MemoryRow.ChangedBits)));
        info.Bindings.Add(new Binding(nameof(MemoryRow.ChangedAtText)));
        info.Bindings.Add(new Binding(nameof(MemoryRow.Error)));
        panel.SetBinding(TextBlock.TextProperty, info);
        _ErrorColumn.CellTemplate = new DataTemplate { VisualTree = panel };
        UpdateColumns();
    }

    private void UpdateColumns()
    {
        if (RowGrid == null || _ErrorColumn == null)
        {
            return;
        }
        RowGrid.Columns.Clear();
        foreach (GridViewColumn column in _FixedColumns)
        {
            RowGrid.Columns.Add(column);
        }
        foreach (KeyValuePair<string, GridViewColumn> pair in _OptionalColumns)
        {
            if (IsColumnChecked(pair.Key))
            {
                RowGrid.Columns.Add(pair.Value);
            }
        }
        RowGrid.Columns.Add(_ErrorColumn);
    }

    private void Columns_Changed(object sender, RoutedEventArgs e)
    {
        UpdateColumns();
    }

    #endregion

    #region The list

    private int SelectedWidth => WidthBox.SelectedIndex >= 0 ? DevMem.Widths(_Tool)[WidthBox.SelectedIndex] : 32;

    /// <summary>The registers that the address, count and width fields describe; null with a message when they are wrong.</summary>
    private List<MemoryRow> RegionFromFields()
    {
        if (!DevMem.TryParseAddress(AddressBox.Text, out ulong address, out string error))
        {
            MessageBoxes.Error(error);
            return null;
        }
        int width = SelectedWidth;
        string alignment = DevMem.CheckAlignment(address, width);
        if (alignment != null)
        {
            MessageBoxes.Error(alignment);
            return null;
        }
        if (!int.TryParse(CountBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int count) || count < 1 || count > MemoryWorkspace.MaxRows)
        {
            MessageBoxes.Error("The number of registers must be between 1 and " + MemoryWorkspace.MaxRows + ".");
            return null;
        }
        try
        {
            return MemoryRow.Region(address, count, width);
        }
        catch (ArgumentException exception)
        {
            MessageBoxes.Error(exception.Message);
            return null;
        }
    }

    private void ShowRegion_Click(object sender, RoutedEventArgs e)
    {
        List<MemoryRow> rows = RegionFromFields();
        if (rows == null)
        {
            return;
        }
        _Rows.Clear();
        foreach (MemoryRow row in rows)
        {
            _Rows.Add(row);
        }
        ReadRows(rows, "the region");
    }

    private void Append_Click(object sender, RoutedEventArgs e)
    {
        List<MemoryRow> rows = RegionFromFields();
        if (rows == null)
        {
            return;
        }
        if (_Rows.Count + rows.Count > MemoryWorkspace.MaxRows)
        {
            MessageBoxes.Error("The list holds at most " + MemoryWorkspace.MaxRows + " registers.");
            return;
        }
        ulong first = _Rows.Count > 0 ? _Rows[0].BaseAddress : rows[0].Address;
        foreach (MemoryRow row in rows)
        {
            row.BaseAddress = first;
            row.RaiseOffset();
            _Rows.Add(row);
        }
        RowList.ScrollIntoView(rows[0]);
        ReadRows(rows, "the added registers");
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        foreach (MemoryRow row in RowList.SelectedItems.Cast<MemoryRow>().ToList())
        {
            _Rows.Remove(row);
        }
    }

    private void RowList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && !(e.OriginalSource is TextBox))
        {
            Remove_Click(sender, e);
            e.Handled = true;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            Read_Click(sender, e);
            e.Handled = true;
        }
    }

    private void WatchAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (MemoryRow row in _Rows)
        {
            row.Watch = true;
        }
    }

    private void WatchNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (MemoryRow row in _Rows)
        {
            row.Watch = false;
        }
    }

    private void WatchSelected_Click(object sender, RoutedEventArgs e)
    {
        foreach (MemoryRow row in RowList.SelectedItems.Cast<MemoryRow>())
        {
            row.Watch = true;
        }
    }

    private void UnwatchSelected_Click(object sender, RoutedEventArgs e)
    {
        foreach (MemoryRow row in RowList.SelectedItems.Cast<MemoryRow>())
        {
            row.Watch = false;
        }
    }

    private void ResetChanges_Click(object sender, RoutedEventArgs e)
    {
        foreach (MemoryRow row in _Rows)
        {
            row.ResetChanges();
        }
    }

    private void AddRegions(List<MemoryRegion> iomem)
    {
        RegionBox.Items.Clear();
        if (iomem != null && iomem.Count > 0)
        {
            if (DevMem.IomemHidden(iomem))
            {
                RegionBox.Items.Add(new RegionItem { Text = "/proc/iomem: the addresses are hidden (not root)" });
            }
            else
            {
                foreach (MemoryRegion region in iomem.Where(r => r.Name.IndexOf("System RAM", StringComparison.OrdinalIgnoreCase) < 0 || r.Depth == 0))
                {
                    RegionBox.Items.Add(new RegionItem { Region = region, Text = "/proc/iomem  " + region });
                }
            }
        }
        foreach (MemoryRegion region in DevMem.Zynq7000Map)
        {
            RegionBox.Items.Add(new RegionItem { Region = region, Text = "Zynq-7000  " + region });
        }
    }

    private void Region_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!(RegionBox.SelectedItem is RegionItem item) || item.Region == null)
        {
            return;
        }
        int width = SelectedWidth;
        AddressBox.Text = DevMem.AddressText(item.Region.Start);
        ulong registers = item.Region.Size / (ulong)(width / 8);
        CountBox.Text = Math.Max(1, Math.Min(64UL, registers)).ToString(CultureInfo.InvariantCulture);
        StatusText.Text = item.Region.Name + ": " + DevMem.AddressText(item.Region.Start) + " was put into the address field. Nothing is read until you press Show.";
    }

    private void Iomem_Click(object sender, RoutedEventArgs e)
    {
        SshSession session = _Session.Session;
        if (session == null)
        {
            return;
        }
        bool useSudo = _UseSudo;
        Task.Run(() =>
        {
            List<MemoryRegion> regions = null;
            try
            {
                regions = DevMem.ParseIomem(session.RunCommand(DevMem.BuildIomemCommand(useSudo), out _, timeout: TimeSpan.FromSeconds(20)));
            }
            catch
            {
            }
            Dispatcher.BeginInvoke((Action)(() =>
            {
                if (!_Closed)
                {
                    AddRegions(regions);
                    StatusText.Text = regions == null ? "/proc/iomem could not be read." : regions.Count + " regions in /proc/iomem.";
                }
            }));
        });
    }

    #endregion

    #region Reading

    private void Read_Click(object sender, RoutedEventArgs e)
    {
        ReadRows(_Rows.ToList(), "all registers");
    }

    private void ReadSelected_Click(object sender, RoutedEventArgs e)
    {
        ReadRows(RowList.SelectedItems.Cast<MemoryRow>().ToList(), "the selected registers");
    }

    /// <summary>Reads the rows on a pool thread, one command per width and per <see cref="Batch"/> addresses, and applies the values.</summary>
    private void ReadRows(List<MemoryRow> rows, string what, bool quiet = false)
    {
        SshSession session = _Session.Session;
        if (rows.Count == 0 || session == null || _Tool == DevMemTool.None)
        {
            return;
        }
        bool useSudo = _UseSudo;
        if (!quiet)
        {
            StatusText.Text = "Reading " + what + "...";
        }
        _Refreshing = true;
        Task.Run(() =>
        {
            Stopwatch clock = Stopwatch.StartNew();
            List<(MemoryRow Row, DevMemReading Reading)> results = new List<(MemoryRow, DevMemReading)>();
            string failure = null;
            lock (_CommandLock)
            {
                foreach (IGrouping<int, MemoryRow> group in rows.GroupBy(r => r.Width))
                {
                    List<MemoryRow> list = group.ToList();
                    for (int start = 0; start < list.Count && failure == null && !_Closed; start += Batch)
                    {
                        List<MemoryRow> part = list.Skip(start).Take(Batch).ToList();
                        List<string> lines = session.RunCommand(DevMem.BuildReadCommand(_Tool, part.Select(r => r.Address), group.Key, useSudo), out _, timeout: TimeSpan.FromSeconds(30 + part.Count / 10));
                        if (lines == null)
                        {
                            failure = session.IsConnectionClosed ? "the connection to the server was lost" : "the reading did not finish in time (an address that hangs the bus?)";
                            break;
                        }
                        List<DevMemReading> readings = DevMem.ParseReadOutput(lines);
                        for (int i = 0; i < part.Count; i++)
                        {
                            DevMemReading reading = i < readings.Count && readings[i].Address == part[i].Address
                                ? readings[i]
                                : readings.FirstOrDefault(r => r.Address == part[i].Address) ?? new DevMemReading { Address = part[i].Address, Error = "no answer for this address" };
                            results.Add((part[i], reading));
                        }
                    }
                }
            }
            long elapsed = clock.ElapsedMilliseconds;
            Dispatcher.BeginInvoke((Action)(() =>
            {
                _Refreshing = false;
                if (_Closed)
                {
                    return;
                }
                DateTime now = DateTime.Now;
                int changed = 0;
                int errors = 0;
                foreach ((MemoryRow row, DevMemReading reading) in results)
                {
                    if (row.Apply(reading, now))
                    {
                        changed++;
                    }
                    if (reading.Error != null)
                    {
                        errors++;
                    }
                }
                ShowBits();
                if (failure != null)
                {
                    StatusText.Text = "Reading " + what + " failed: " + failure + ".";
                    Log(StatusText.Text);
                    if (AutoBox.IsChecked == true)
                    {
                        AutoBox.IsChecked = false;
                        Log("The automatic refresh was stopped.");
                    }
                    return;
                }
                StatusText.Text = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  read " + results.Count + " registers in " + elapsed + " ms"
                    + (changed > 0 ? ", " + changed + " changed" : "") + (errors > 0 ? ", " + errors + " with errors" : "") + ".";
            }));
        });
    }

    private void RefreshWatched()
    {
        if (_Refreshing || _Closed)
        {
            return;
        }
        List<MemoryRow> rows = _Rows.Where(r => r.Watch).ToList();
        if (rows.Count == 0)
        {
            StatusText.Text = "No row is checked for the automatic refresh.";
            return;
        }
        ReadRows(rows, "the checked registers", quiet: true);
    }

    private int IntervalMilliseconds()
    {
        return int.TryParse(IntervalBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? Math.Max(100, Math.Min(3600000, value)) : 1000;
    }

    private TimeSpan HighlightDuration()
    {
        return double.TryParse(HighlightBox.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds >= 0
            ? TimeSpan.FromSeconds(Math.Min(3600, seconds))
            : TimeSpan.FromSeconds(3);
    }

    private void Auto_Changed(object sender, RoutedEventArgs e)
    {
        if (_RefreshTimer == null)
        {
            return;
        }
        if (AutoBox.IsChecked == true)
        {
            _RefreshTimer.Interval = TimeSpan.FromMilliseconds(IntervalMilliseconds());
            IntervalBox.Text = IntervalMilliseconds().ToString(CultureInfo.InvariantCulture);
            _RefreshTimer.Start();
            RefreshWatched();
        }
        else
        {
            _RefreshTimer.Stop();
        }
    }

    private void Interval_LostFocus(object sender, RoutedEventArgs e)
    {
        IntervalBox.Text = IntervalMilliseconds().ToString(CultureInfo.InvariantCulture);
        if (_RefreshTimer != null)
        {
            _RefreshTimer.Interval = TimeSpan.FromMilliseconds(IntervalMilliseconds());
        }
    }

    private void UpdateHighlights()
    {
        DateTime now = DateTime.Now;
        TimeSpan duration = HighlightDuration();
        foreach (MemoryRow row in _Rows)
        {
            row.UpdateHighlight(now, duration);
        }
    }

    #endregion

    #region Writing

    private MemoryRow SelectedRow => RowList.SelectedItems.Count == 1 ? RowList.SelectedItem as MemoryRow : null;

    private void RowList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        MemoryRow row = SelectedRow;
        WriteGroup.IsEnabled = row != null;
        WriteGroup.Header = row != null ? "Write " + row.AddressText + (string.IsNullOrWhiteSpace(row.Name) ? "" : " (" + row.Name + ")") + ", " + row.Width + " bits" : "Write the selected register";
        if (_BitRow != null)
        {
            _BitRow.PropertyChanged -= BitRow_PropertyChanged;
        }
        _BitRow = row;
        if (row != null)
        {
            row.PropertyChanged += BitRow_PropertyChanged;
            if (row.Value.HasValue)
            {
                ValueBox.Text = row.Hex;
            }
        }
        ShowBits();
    }

    private void BitRow_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MemoryRow.Value))
        {
            ShowBits();
        }
    }

    /// <summary>The buttons of the bits of the selected register, the most significant one first, and the bit field below them.</summary>
    private void ShowBits()
    {
        MemoryRow row = _BitRow;
        if (row == null)
        {
            BitPanel.Children.Clear();
            FieldInfoText.Text = "";
            return;
        }
        if (BitPanel.Children.Count != row.Width || !ReferenceEquals(BitPanel.Tag, row))
        {
            BitPanel.Children.Clear();
            BitPanel.Tag = row;
            for (int bit = row.Width - 1; bit >= 0; bit--)
            {
                int number = bit;
                ToggleButton button = new ToggleButton
                {
                    Width = 30,
                    Height = 34,
                    Margin = new Thickness(bit % 8 == 7 && bit != row.Width - 1 ? 6 : 1, 1, 1, 1),
                    Padding = new Thickness(0),
                    FontFamily = new System.Windows.Media.FontFamily("Consolas")
                };
                button.Click += (sender, e) => FlipBit(number);
                BitPanel.Children.Add(button);
            }
        }
        for (int i = 0; i < BitPanel.Children.Count; i++)
        {
            int bit = row.Width - 1 - i;
            ToggleButton button = (ToggleButton)BitPanel.Children[i];
            bool? on = row.Value.HasValue ? DevMem.GetBit(row.Value.Value, bit) : (bool?)null;
            button.IsChecked = on == true;
            button.Content = bit.ToString(CultureInfo.InvariantCulture) + "\n" + (on.HasValue ? (on.Value ? "1" : "0") : "?");
            button.ToolTip = "Bit " + bit + (on.HasValue ? " = " + (on.Value ? "1" : "0") : "") + ": click to flip it (the register is read, the bit changed and written back)";
        }
        ShowFieldInfo();
    }

    private void ShowFieldInfo()
    {
        MemoryRow row = _BitRow;
        if (row == null || !row.Value.HasValue || !DevMem.TryParseField(FieldBox.Text, out int msb, out int lsb) || msb >= row.Width)
        {
            FieldInfoText.Text = "";
            return;
        }
        ulong field = DevMem.GetField(row.Value.Value, msb, lsb);
        FieldInfoText.Text = "[" + msb + ":" + lsb + "] = 0x" + field.ToString("X", CultureInfo.InvariantCulture) + " = " + field.ToString(CultureInfo.InvariantCulture)
            + " = 0b" + Convert.ToString(unchecked((long)field), 2).PadLeft(msb - lsb + 1, '0');
    }

    private void FlipBit(int bit)
    {
        MemoryRow row = _BitRow;
        // The button shows the state of the register, not of the click: the read-back sets it.
        ShowBits();
        if (row != null)
        {
            Modify(row, value => value ^ (1UL << bit), "bit " + bit + " flipped");
        }
    }

    private void ValueBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Write_Click(sender, e);
            e.Handled = true;
        }
    }

    private void Write_Click(object sender, RoutedEventArgs e)
    {
        MemoryRow row = SelectedRow;
        if (row == null)
        {
            return;
        }
        ulong? value = DevMem.ParseValue(ValueBox.Text, row.Width, out string error);
        if (value == null)
        {
            MessageBoxes.Error(error);
            return;
        }
        ulong newValue = value.Value;
        Modify(row, old => newValue, "value written");
    }

    private void WriteField_Click(object sender, RoutedEventArgs e)
    {
        MemoryRow row = SelectedRow;
        if (row == null)
        {
            return;
        }
        if (!DevMem.TryParseField(FieldBox.Text, out int msb, out int lsb) || msb >= row.Width)
        {
            MessageBoxes.Error("\"" + FieldBox.Text + "\" is not a bit field of a " + row.Width + "-bit register (for example [7:4]).");
            return;
        }
        ulong? field = DevMem.ParseValue(FieldValueBox.Text, Math.Min(64, msb - lsb + 1), out string error);
        if (field == null)
        {
            MessageBoxes.Error("[" + msb + ":" + lsb + "]: " + error);
            return;
        }
        ulong fieldValue = field.Value;
        Modify(row, old => DevMem.SetField(old, msb, lsb, fieldValue), "[" + msb + ":" + lsb + "] = 0x" + fieldValue.ToString("X", CultureInfo.InvariantCulture));
    }

    private ulong? MaskValue(MemoryRow row)
    {
        ulong? mask = DevMem.ParseValue(MaskBox.Text, row.Width, out string error);
        if (mask == null)
        {
            MessageBoxes.Error("Mask: " + error);
        }
        return mask;
    }

    private void SetMask_Click(object sender, RoutedEventArgs e)
    {
        MemoryRow row = SelectedRow;
        ulong? mask = row == null ? null : MaskValue(row);
        if (mask != null)
        {
            Modify(row, old => old | mask.Value, "bits " + DevMem.FormatHex(mask.Value, row.Width) + " set");
        }
    }

    private void ClearMask_Click(object sender, RoutedEventArgs e)
    {
        MemoryRow row = SelectedRow;
        ulong? mask = row == null ? null : MaskValue(row);
        if (mask != null)
        {
            Modify(row, old => old & ~mask.Value, "bits " + DevMem.FormatHex(mask.Value, row.Width) + " cleared");
        }
    }

    private void ToggleMask_Click(object sender, RoutedEventArgs e)
    {
        MemoryRow row = SelectedRow;
        ulong? mask = row == null ? null : MaskValue(row);
        if (mask != null)
        {
            Modify(row, old => old ^ mask.Value, "bits " + DevMem.FormatHex(mask.Value, row.Width) + " toggled");
        }
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_Undo.Count == 0)
        {
            return;
        }
        WriteRecord last = _Undo.Peek();
        Modify(last.Row, old => last.OldValue, "undo: " + DevMem.FormatHex(last.OldValue, last.Row.Width) + " written back", isUndo: true);
    }

    /// <summary>
    /// Reads the register, computes the new value from it, asks (when Ask before each write is checked), writes it and reads it back,
    /// all on a pool thread while no other command runs. The value before the write is kept for Undo.
    /// </summary>
    private void Modify(MemoryRow row, Func<ulong, ulong> change, string what, bool isUndo = false)
    {
        SshSession session = _Session.Session;
        if (session == null || session.IsConnectionClosed)
        {
            StatusText.Text = "The SSH session is not connected.";
            return;
        }
        bool confirm = ConfirmBox.IsChecked == true;
        bool useSudo = _UseSudo;
        StatusText.Text = "Writing " + row.AddressText + "...";
        Task.Run(() =>
        {
            string message;
            ulong? oldValue = null;
            ulong newValue = 0;
            DevMemReading readBack = null;
            bool written = false;
            lock (_CommandLock)
            {
                try
                {
                    List<string> current = session.RunCommand(DevMem.BuildReadCommand(_Tool, new[] { row.Address }, row.Width, useSudo), out _, timeout: TimeSpan.FromSeconds(30));
                    DevMemReading before = current == null ? null : DevMem.ParseReadOutput(current).FirstOrDefault();
                    if (before?.Value == null)
                    {
                        throw new InvalidOperationException("The register could not be read before the write: " + (before?.Error ?? (current == null ? "the command did not finish" : "no answer")) + ". Nothing was written.");
                    }
                    oldValue = before.Value.Value & DevMem.Mask(row.Width);
                    newValue = change(oldValue.Value) & DevMem.Mask(row.Width);
                    ulong oldShown = oldValue.Value;
                    ulong newShown = newValue;
                    if (confirm && !Dispatcher.Invoke(() => MessageBoxes.Ask("Write " + DevMem.FormatHex(newShown, row.Width) + " to " + row.AddressText + (string.IsNullOrWhiteSpace(row.Name) ? "" : " (" + row.Name + ")")
                        + "?\n\nIt holds " + DevMem.FormatHex(oldShown, row.Width) + " now (" + what + ").")))
                    {
                        throw new OperationCanceledException();
                    }
                    List<string> lines = session.RunCommand(DevMem.BuildWriteCommand(_Tool, row.Address, row.Width, newValue, useSudo), out _, timeout: TimeSpan.FromSeconds(30));
                    if (lines == null)
                    {
                        throw new InvalidOperationException("The write did not finish: " + (session.IsConnectionClosed ? "the connection to the server was lost." : "the command hangs (an address where nothing answers?)."));
                    }
                    if (!DevMem.ParseWriteStatus(lines, out string writeError))
                    {
                        throw new InvalidOperationException("The write failed: " + writeError + ".");
                    }
                    written = true;
                    readBack = DevMem.ParseReadOutput(lines).FirstOrDefault();
                    message = DevMem.FormatHex(oldValue.Value, row.Width) + " -> " + DevMem.FormatHex(newValue, row.Width) + " (" + what + ")";
                    if (readBack?.Value != null && (readBack.Value.Value & DevMem.Mask(row.Width)) != newValue)
                    {
                        message += "; reads back " + DevMem.FormatHex(readBack.Value.Value, row.Width) + ": read-only, self-clearing or write-1-to-clear bits";
                    }
                    else if (readBack?.Value == null)
                    {
                        message += "; the read-back failed: " + (readBack?.Error ?? "no answer");
                    }
                }
                catch (OperationCanceledException)
                {
                    message = null;
                }
                catch (Exception exception)
                {
                    message = exception.Message;
                }
            }
            Dispatcher.BeginInvoke((Action)(() =>
            {
                if (_Closed)
                {
                    return;
                }
                if (message == null)
                {
                    StatusText.Text = "Not written.";
                    return;
                }
                if (written)
                {
                    if (isUndo && _Undo.Count > 0)
                    {
                        _Undo.Pop();
                    }
                    else if (!isUndo && oldValue.HasValue)
                    {
                        _Undo.Push(new WriteRecord { Row = row, OldValue = oldValue.Value });
                    }
                    UndoButton.IsEnabled = _Undo.Count > 0;
                    if (readBack != null)
                    {
                        row.Apply(readBack, DateTime.Now);
                    }
                    if (row.Value.HasValue && ReferenceEquals(row, _BitRow))
                    {
                        ValueBox.Text = row.Hex;
                    }
                    Log(row.AddressText + (string.IsNullOrWhiteSpace(row.Name) ? "" : " " + row.Name) + " [" + row.Width + "]: " + message);
                    StatusText.Text = "Written: " + row.AddressText + " " + message;
                }
                else
                {
                    Log(row.AddressText + ": " + message);
                    StatusText.Text = message;
                    MessageBoxes.Error(message);
                }
                ShowBits();
            }));
        });
    }

    #endregion

    #region Files and the clipboard

    private void OpenList_Click(object sender, RoutedEventArgs e)
    {
        string path = LocalFileDialogWindow.OpenFile(this, "Open a list of registers", "Register lists (*.xml)|*.xml|All files (*.*)|*.*", initialDirectory: Directory.Exists(_ListFolder) ? _ListFolder : null);
        if (path == null)
        {
            return;
        }
        _ListFolder = Path.GetDirectoryName(path);
        try
        {
            ApplyWorkspace(MemoryWorkspace.Load(path), Path.GetFileName(path));
        }
        catch (Exception exception)
        {
            MessageBoxes.Error("Cannot open " + path + ": " + (exception.InnerException?.Message ?? exception.Message));
        }
    }

    private void SaveList_Click(object sender, RoutedEventArgs e)
    {
        string path = LocalFileDialogWindow.SaveFile(this, "Save the list of registers", "Register lists (*.xml)|*.xml", fileName: "registers.xml", addExtension: true, initialDirectory: Directory.Exists(_ListFolder) ? _ListFolder : null);
        if (path == null)
        {
            return;
        }
        _ListFolder = Path.GetDirectoryName(path);
        try
        {
            CurrentWorkspace().Save(path);
            StatusText.Text = "Saved " + _Rows.Count + " registers to " + path + ".";
        }
        catch (Exception exception)
        {
            MessageBoxes.Error("Cannot save " + path + ": " + exception.Message);
        }
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        string path = LocalFileDialogWindow.SaveFile(this, "Export the table", "CSV files (*.csv)|*.csv", fileName: "registers.csv", addExtension: true, initialDirectory: Directory.Exists(_ListFolder) ? _ListFolder : null);
        if (path == null)
        {
            return;
        }
        try
        {
            File.WriteAllText(path, Csv(_Rows), new UTF8Encoding(true));
            StatusText.Text = "Exported " + _Rows.Count + " registers to " + path + ".";
        }
        catch (Exception exception)
        {
            MessageBoxes.Error("Cannot write " + path + ": " + exception.Message);
        }
    }

    private static string Csv(IEnumerable<MemoryRow> rows)
    {
        StringBuilder text = new StringBuilder(MemoryRow.CsvHeader).Append("\r\n");
        foreach (MemoryRow row in rows)
        {
            text.Append(row.ToCsv()).Append("\r\n");
        }
        return text.ToString();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        List<MemoryRow> rows = RowList.SelectedItems.Cast<MemoryRow>().OrderBy(r => _Rows.IndexOf(r)).ToList();
        if (rows.Count > 0)
        {
            Clipboard.SetText(Csv(rows));
        }
    }

    private void CopyValue_Click(object sender, RoutedEventArgs e)
    {
        MemoryRow row = RowList.SelectedItem as MemoryRow;
        if (row?.Value != null)
        {
            Clipboard.SetText(row.Hex);
        }
    }

    private void Log(string line)
    {
        if (_Closed)
        {
            return;
        }
        if (LogBox.LineCount > 2000)
        {
            LogBox.Text = LogBox.Text.Substring(LogBox.Text.Length / 2);
        }
        LogBox.AppendText((LogBox.Text.Length > 0 ? Environment.NewLine : "") + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line);
        LogBox.ScrollToEnd();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    #endregion
}
