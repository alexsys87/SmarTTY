using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SmarTTY.GUI.DevicePanel;

// The tool bar and the context menu of the device panel, WPF since stage 4A (they were the WinForms items of the old designer). The items
// keep the names the designer gave them, so that the code of the partial files reads as before; the look is the theme's (ToolBar, its
// button styles, ContextMenu and MenuItem in GUI/Styles/Controls.xaml).
public partial class WpfDeviceTerminal
{
    // The WinForms timers of the partial files (the line status, the quick script, the listing) are made in it.
    private IContainer components = new Container();

    private Grid _toolBarRow;

    private ToolBar toolStrip1;

    private ToolBar _toolBarRight;

    private TextBlock lblConnection;

    private Button btnDisconnect;

    private Button btnReconnect;

    private ToolBarMenuButton btnSend;

    private TextBox txtHexInput;

    private TextBlock lblSend;

    private MenuItem sendAsHEXToolStripMenuItem;

    private MenuItem sendImmediatelyToolStripMenuItem;

    private Button toolStripButton1;

    private ToolBarMenuButton toolStripButton3;

    private MenuItem sendBinaryFileToolStripMenuItem;

    private MenuItem sendFileViaXMODEMToolStripMenuItem;

    private MenuItem sendFileViaXMODEM1KToolStripMenuItem;

    private MenuItem receiveFileViaXMODEMToolStripMenuItem;

    private MenuItem sendFilesViaYMODEMToolStripMenuItem;

    private MenuItem receiveFilesViaYMODEMToolStripMenuItem;

    private MenuItem receiveFilesViaYMODEMGToolStripMenuItem;

    private MenuItem sendFilesViaZModemToolStripMenuItem;

    private MenuItem receiveFilesViaZModemToolStripMenuItem;

    private MenuItem autoReceiveZModemToolStripMenuItem;

    private MenuItem escapeZModemControlToolStripMenuItem;

    private MenuItem sendFilesViaKermitToolStripMenuItem;

    private MenuItem receiveFilesViaKermitToolStripMenuItem;

    private Button toolStripButton2;

    private ContextMenu contextMenuStrip1;

    private MenuItem copyToolStripMenuItem;

    private MenuItem copyAllToolStripMenuItem;

    private MenuItem pasteToolStripMenuItem;

    private ToolBarMenuButton toolStripDropDownButton1;

    private MenuItem terminalTypeToolStripMenuItem;

    private MenuItem cbASCII;

    private MenuItem cbHEX;

    private MenuItem cbBoth;

    private MenuItem echoTypedCharactersToolStripMenuItem;

    private MenuItem changeSettingsToolStripMenuItem;

    private TextBlock lblBaudRate;

    private ComboBox cbBaudRate;

    private TextBlock lblEncoding;

    private ComboBox cbEncoding;

    private Button btnLog;

    private Button btnReplay;

    private void DisposeComponents(bool disposing)
    {
        if (disposing && components != null)
        {
            components.Dispose();
        }
    }

    private void InitializeComponent()
    {
        lblConnection = new TextBlock { Text = "Connected", Margin = new Thickness(2, 0, 4, 0) };
        btnDisconnect = ImageButton("terminal-disconnect", "Disconnect", btnDisconnect_Click);
        btnReconnect = ImageButton("terminal-reconnect", "Reconnect", btnReconnect_Click);

        sendAsHEXToolStripMenuItem = new MenuItem { Header = "Send as HEX", IsChecked = true };
        sendAsHEXToolStripMenuItem.Click += sendAsHEXToolStripMenuItem_Click;
        sendImmediatelyToolStripMenuItem = new MenuItem { Header = "Send as you type", IsChecked = true };
        sendImmediatelyToolStripMenuItem.Click += sendImmediatelyToolStripMenuItem_Click;
        btnSend = new ToolBarMenuButton(WpfMenus.Icon("terminal-send"), "Send", split: true);
        btnSend.Items.Add(sendAsHEXToolStripMenuItem);
        btnSend.Items.Add(sendImmediatelyToolStripMenuItem);
        btnSend.ButtonClick += btnSend_ButtonClick;
        txtHexInput = new TextBox { Width = 100, VerticalContentAlignment = VerticalAlignment.Center };
        txtHexInput.PreviewKeyDown += txtHexInput_KeyDown;
        txtHexInput.PreviewTextInput += txtHexInput_KeyPress;
        txtHexInput.TextChanged += txtHexInput_TextChanged;
        lblSend = new TextBlock { Text = "Send values:", Margin = new Thickness(4, 0, 2, 0) };

        toolStripButton1 = ImageButton("terminal-clear", "Clear contents", toolStripButton1_Click);

        sendBinaryFileToolStripMenuItem = MenuText("Send binary file", toolStripButton3_Click);
        sendFileViaXMODEMToolStripMenuItem = MenuText("Send file via XMODEM...", sendFileViaXMODEMToolStripMenuItem_Click);
        sendFileViaXMODEM1KToolStripMenuItem = MenuText("Send file via XMODEM-1K...", sendFileViaXMODEM1KToolStripMenuItem_Click);
        receiveFileViaXMODEMToolStripMenuItem = MenuText("Receive file via XMODEM...", receiveFileViaXMODEMToolStripMenuItem_Click);
        sendFilesViaYMODEMToolStripMenuItem = MenuText("Send file(s) via YMODEM...", null,
            "The receiver chooses the variant: YMODEM (CRC-16), the checksum variant or YMODEM-G (streaming).");
        receiveFilesViaYMODEMToolStripMenuItem = MenuText("Receive file(s) via YMODEM...", null,
            "Asks for CRC-16 and falls back to the checksum variant when the sender does not answer.");
        receiveFilesViaYMODEMGToolStripMenuItem = MenuText("Receive file(s) via YMODEM-G...", null,
            "Streaming without acknowledgements: for error-free links only; a damaged block cancels the transfer.");
        sendFilesViaZModemToolStripMenuItem = MenuText("Send files via ZMODEM...", null);
        receiveFilesViaZModemToolStripMenuItem = MenuText("Receive files via ZMODEM...", null);
        autoReceiveZModemToolStripMenuItem = new MenuItem { Header = "Start ZMODEM receive automatically", IsCheckable = true, IsChecked = true };
        escapeZModemControlToolStripMenuItem = new MenuItem { Header = "ZMODEM: escape control characters", IsCheckable = true };
        sendFilesViaKermitToolStripMenuItem = MenuText("Send files via Kermit...", null);
        receiveFilesViaKermitToolStripMenuItem = MenuText("Receive files via Kermit...", null);
        toolStripButton3 = new ToolBarMenuButton(WpfMenus.Icon("terminal-transfer"), "File transfer", split: true);
        foreach (MenuItem item in new[]
        {
            sendBinaryFileToolStripMenuItem, sendFileViaXMODEMToolStripMenuItem, sendFileViaXMODEM1KToolStripMenuItem, receiveFileViaXMODEMToolStripMenuItem,
            sendFilesViaYMODEMToolStripMenuItem, receiveFilesViaYMODEMToolStripMenuItem, receiveFilesViaYMODEMGToolStripMenuItem, sendFilesViaZModemToolStripMenuItem,
            receiveFilesViaZModemToolStripMenuItem, autoReceiveZModemToolStripMenuItem, escapeZModemControlToolStripMenuItem, sendFilesViaKermitToolStripMenuItem,
            receiveFilesViaKermitToolStripMenuItem
        })
        {
            toolStripButton3.Items.Add(item);
        }
        toolStripButton3.ButtonClick += toolStripButton3_Click;

        toolStripButton2 = ImageButton("terminal-save", "Save output to file", toolStripButton2_Click);
        btnLog = TextButton("Start log", "Continuously record received data to a file", btnLog_Click);
        btnReplay = TextButton("Replay", "Play a recorded log of received data back into this terminal", btnReplay_Click);

        cbASCII = MenuText("ASCII", cbASCII_Click);
        cbHEX = MenuText("HEX", cbHEX_Click);
        cbBoth = MenuText("Text + HEX", cbBoth_Click);
        terminalTypeToolStripMenuItem = MenuText("Terminal type", null);
        terminalTypeToolStripMenuItem.Items.Add(cbASCII);
        terminalTypeToolStripMenuItem.Items.Add(cbHEX);
        terminalTypeToolStripMenuItem.Items.Add(cbBoth);
        echoTypedCharactersToolStripMenuItem = MenuText("Echo typed characters", echoTypedCharactersToolStripMenuItem_Click);
        changeSettingsToolStripMenuItem = MenuText("Connection settings...", changeSettingsToolStripMenuItem_Click);
        toolStripDropDownButton1 = new ToolBarMenuButton(WpfMenus.Icon("terminal-settings"), "Settings", split: false);
        toolStripDropDownButton1.Items.Add(terminalTypeToolStripMenuItem);
        toolStripDropDownButton1.Items.Add(echoTypedCharactersToolStripMenuItem);
        toolStripDropDownButton1.Items.Add(changeSettingsToolStripMenuItem);

        lblBaudRate = new TextBlock { Text = "Baud rate:", Margin = new Thickness(6, 0, 2, 0) };
        cbBaudRate = new ComboBox { IsEditable = true, Width = 100 };
        foreach (string rate in new[] { "1200", "2400", "4800", "9600", "19200", "38400", "57600", "74880", "115200", "230400", "460800", "921600", "1000000", "2000000" })
        {
            cbBaudRate.Items.Add(rate);
        }
        cbBaudRate.SelectionChanged += cbBaudRate_SelectedIndexChanged;
        cbBaudRate.LostKeyboardFocus += cbBaudRate_Leave;
        cbBaudRate.PreviewKeyDown += cbBaudRate_KeyDown;
        lblEncoding = new TextBlock { Text = "Encoding:", Margin = new Thickness(6, 0, 2, 0) };
        cbEncoding = new ComboBox { Width = 170, ToolTip = "Text encoding used to decode received data and to encode typed and pasted text" };
        cbEncoding.SelectionChanged += cbEncoding_SelectedIndexChanged;

        toolStrip1 = new ToolBar();
        foreach (UIElement item in new UIElement[]
        {
            lblConnection, btnDisconnect, btnReconnect, toolStripButton1, toolStripButton3, toolStripButton2, btnLog, btnReplay, toolStripDropDownButton1,
            lblBaudRate, cbBaudRate, lblEncoding, cbEncoding
        })
        {
            toolStrip1.Items.Add(item);
        }
        // The "Send values" box and its button are at the right end, as the WinForms items aligned to the right were.
        _toolBarRight = new ToolBar();
        _toolBarRight.Items.Add(lblSend);
        _toolBarRight.Items.Add(txtHexInput);
        _toolBarRight.Items.Add(btnSend);
        _toolBarRow = new Grid();
        _toolBarRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _toolBarRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_toolBarRight, 1);
        _toolBarRow.Children.Add(toolStrip1);
        _toolBarRow.Children.Add(_toolBarRight);

        copyToolStripMenuItem = new MenuItem { Header = "Copy", Icon = WpfMenus.Icon("copy"), InputGestureText = "Ctrl+Shift+C" };
        copyToolStripMenuItem.Click += copyToolStripMenuItem_Click;
        copyAllToolStripMenuItem = new MenuItem { Header = "Copy all", Icon = WpfMenus.Icon("copy") };
        copyAllToolStripMenuItem.Click += copyAllToolStripMenuItem_Click;
        pasteToolStripMenuItem = new MenuItem { Header = "Paste", Icon = WpfMenus.Icon("paste"), InputGestureText = "Ctrl+Shift+V" };
        pasteToolStripMenuItem.Click += pasteToolStripMenuItem_Click;
        contextMenuStrip1 = new ContextMenu();
        contextMenuStrip1.Items.Add(copyToolStripMenuItem);
        contextMenuStrip1.Items.Add(copyAllToolStripMenuItem);
        contextMenuStrip1.Items.Add(pasteToolStripMenuItem);
    }

    private static Button ImageButton(string icon, string toolTip, RoutedEventHandler click)
    {
        Button button = new Button { Content = WpfMenus.Icon(icon), ToolTip = toolTip };
        button.Click += click;
        return button;
    }

    private static Button TextButton(string text, string toolTip, RoutedEventHandler click)
    {
        Button button = new Button { Content = text, ToolTip = toolTip };
        button.Click += click;
        return button;
    }

    private static MenuItem MenuText(string text, RoutedEventHandler click, string toolTip = null)
    {
        MenuItem item = new MenuItem { Header = text };
        if (toolTip != null)
        {
            item.ToolTip = toolTip;
        }
        if (click != null)
        {
            item.Click += click;
        }
        return item;
    }
}

/// <summary>
/// A button of a WPF tool bar that opens a menu below it: split (its main part runs <see cref="ButtonClick"/>, the arrow opens the menu), or
/// a drop-down whose whole face opens the menu. What the WinForms <c>ToolStripSplitButton</c> and <c>ToolStripDropDownButton</c> were.
/// </summary>
internal sealed class ToolBarMenuButton : StackPanel
{
    private readonly Button _main;

    private readonly Button _arrow;

    public ToolBarMenuButton(object content, string toolTip, bool split)
    {
        Orientation = Orientation.Horizontal;
        ToolTip = toolTip;
        Menu = new ContextMenu();
        _main = new Button { Content = split ? content : DropDownFace(content) };
        _main.SetResourceReference(StyleProperty, ToolBar.ButtonStyleKey);
        Children.Add(_main);
        if (split)
        {
            _arrow = new Button { Content = Arrow(), Padding = new Thickness(1, 3, 1, 3) };
            _arrow.SetResourceReference(StyleProperty, ToolBar.ButtonStyleKey);
            _arrow.Click += (sender, e) => OpenMenu();
            Children.Add(_arrow);
            _main.Click += (sender, e) => ButtonClick?.Invoke(this, e);
        }
        else
        {
            _main.Click += (sender, e) => OpenMenu();
        }
    }

    /// <summary>The menu that opens below the button.</summary>
    public ContextMenu Menu { get; }

    public ItemCollection Items => Menu.Items;

    /// <summary>The main part of a split button was clicked.</summary>
    public event RoutedEventHandler ButtonClick;

    /// <summary>The menu is about to open (its items can be brought up to date).</summary>
    public event EventHandler Opening;

    private void OpenMenu()
    {
        Opening?.Invoke(this, EventArgs.Empty);
        WpfMenus.OpenBelow(Menu, this);
    }

    private static object DropDownFace(object content)
    {
        StackPanel face = new StackPanel { Orientation = Orientation.Horizontal };
        if (content is UIElement element)
        {
            face.Children.Add(element);
        }
        else
        {
            face.Children.Add(new TextBlock { Text = Convert.ToString(content), VerticalAlignment = VerticalAlignment.Center });
        }
        UIElement arrow = Arrow();
        ((FrameworkElement)arrow).Margin = new Thickness(3, 0, 0, 0);
        face.Children.Add(arrow);
        return face;
    }

    private static UIElement Arrow()
    {
        System.Windows.Shapes.Path path = new System.Windows.Shapes.Path
        {
            Data = System.Windows.Media.Geometry.Parse("M 0,0 L 6,0 L 3,3 Z"),
            VerticalAlignment = VerticalAlignment.Center
        };
        path.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Theme.TextSecondary");
        return path;
    }
}
