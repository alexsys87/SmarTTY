using System;
using System.Text;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.Controls;

// The TFTP server window (Tools menu): it serves a firmware file to a board that waits at the U-Boot prompt on this COM/TCP
// connection and types the U-Boot commands that fetch it. The window belongs to one terminal; it reads what the board prints
// through a tap on the received data and writes its commands through the connection like typed text.
//
// The window is the themed WPF GUI/TftpFirmwareWindow, which this harness-compiled control cannot reference: App injects it
// through TftpFirmwareWindowFactory.
public partial class DeviceTerminalControl
{
    /// <summary>
    /// Creates the TFTP/U-Boot window of this terminal (this terminal, its title). Injected by <c>App</c>: this control
    /// is compiled by the Mono test harness, which cannot reference the WPF dialogs. Null when the window is unavailable.
    /// </summary>
    public static Func<DeviceTerminalControl, string, ITftpFirmwareWindow> TftpFirmwareWindowFactory;

    private ITftpFirmwareWindow _tftpWindow;

    // Set while the TFTP window listens; called with every block of received bytes, before the terminal handles it.
    private Action<byte[], int> _consoleTap;

    private sealed class TapSubscription : IDisposable
    {
        private Action _release;

        public TapSubscription(Action release)
        {
            _release = release;
        }

        public void Dispose()
        {
            Action release = _release;
            _release = null;
            release?.Invoke();
        }
    }

    private void InitializeTftpSupport()
    {
        // The window cannot outlive the connection it types into.
        Disposed += (sender, e) => CloseTftpWindow();
    }

    /// <summary>Opens the TFTP server window of this terminal, or brings it forward when it is open already. Call on the UI thread.</summary>
    public void ShowTftpFirmwareWindow()
    {
        Func<DeviceTerminalControl, string, ITftpFirmwareWindow> factory = TftpFirmwareWindowFactory;
        if (factory == null)
        {
            return;
        }
        ITftpFirmwareWindow existing = _tftpWindow;
        if (existing != null && existing.IsOpen)
        {
            existing.Activate();
            return;
        }
        _tftpWindow = factory(this, GetConnectionTitle(_deviceTerminalSettings.Connection));
        _tftpWindow.Show();
    }

    private void CloseTftpWindow()
    {
        ITftpFirmwareWindow window = _tftpWindow;
        _tftpWindow = null;
        if (window != null && window.IsOpen)
        {
            window.CloseWithTerminal();
        }
    }

    /// <summary>True while the connection to the device is open.</summary>
    internal bool IsDeviceConnected => _connectionState == ConnectionStateEnum.Connected && _asyncTransportConnection != null;

    /// <summary>True while a file transfer protocol (XMODEM, YMODEM, ZMODEM, Kermit) or a file being sent owns the line.</summary>
    internal bool IsLineOwnedByTransfer => IsLineClaimed;

    /// <summary>
    /// Reports what the device sends, as text, until the returned object is disposed. The text is the same the terminal
    /// shows; the calls come on the UI thread, in order.
    /// </summary>
    internal IDisposable ListenToConsole(Action<string> onText)
    {
        Decoder decoder = _textEncoding.GetDecoder();
        Encoding encoding = _textEncoding;
        Action<byte[], int> tap = (buffer, count) =>
        {
            char[] chars = new char[encoding.GetMaxCharCount(count) + 4];
            int length = decoder.GetChars(buffer, 0, count, chars, 0);
            if (length > 0)
            {
                onText(new string(chars, 0, length));
            }
        };
        _consoleTap += tap;
        return new TapSubscription(() => _consoleTap -= tap);
    }

    /// <summary>Types a line followed by Enter (a carriage return, which every U-Boot takes). False when it could not be sent.</summary>
    internal bool SendConsoleLine(string line)
    {
        return SendConsoleBytes(_textEncoding.GetBytes((line ?? string.Empty) + "\r"));
    }

    /// <summary>Types Ctrl+C, which stops a running U-Boot command such as a download.</summary>
    internal bool SendConsoleBreak()
    {
        return SendConsoleBytes(new byte[] { 0x03 });
    }

    private bool SendConsoleBytes(byte[] bytes)
    {
        if (!IsDeviceConnected || IsLineClaimed)
        {
            return false;
        }
        try
        {
            if (!SendToDevice(bytes, bytes.Length))
            {
                return false;
            }
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        if (HexMode && _deviceTerminalSettings.EchoTypedCharacters)
        {
            hexTerminalControl1.AppendData(bytes, bytes.Length, isEcho: true);
        }
        return true;
    }
}
