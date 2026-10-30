using SmarTTY.IO;
using SmarTTY.Transport;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.Controls;

// File transfer through the device's shell: the file is packed with gzip and typed into the shell as base64 (or uuencode)
// text, the device decodes and unpacks it and the checksums are compared (IO/GzipBase64Transfer.cs holds the protocol). It
// needs nothing on the device but busybox tools, so it works on any Linux (OpenWrt included) that has a shell on this COM/TCP
// connection. A transfer owns the line exactly like an XMODEM or Kermit one (StartLineTransfer is shared, with the WPF
// progress window of the other protocols). The window that asks what to transfer is WPF (GUI/GzipBase64TransferWindow.xaml);
// this file is compiled by the Mono test harness, which cannot reference GUI/, so App injects it through
// GzipBase64RequestPrompt.
public partial class DeviceTerminalControl
{
    /// <summary>
    /// Asks what to transfer: the first argument is true to send a file, false to receive one; the second lists a folder of the
    /// device for the window's browse button. Returns null when the user cancelled. Injected by <c>App</c> (the window is WPF).
    /// </summary>
    public static Func<bool, GzipBase64Transfer.DirectoryLister, GzipBase64Transfer.Request> GzipBase64RequestPrompt;

    // A transfer that had to stop after its connection was gone (a reconnect, a cable pulled) leaves the device's console in the
    // middle of it: the shell script there waits for the rest of the data and eats whatever is typed next. What ends that is kept
    // here and sent when the connection to the same device is open again.
    private readonly object _recoveryGate = new object();
    private byte[] _pendingDeviceRecovery;
    private string _pendingRecoveryTarget;

    private void InitializeGzipBase64Menu()
    {
        ToolStripMenuItem send = new ToolStripMenuItem("Send file via gzip + base64 (shell)...")
        {
            ToolTipText = "Sends a file through the shell of a Linux device (OpenWrt too): packed with gzip, typed as base64 text, unpacked there and checked with a checksum. Nothing is needed on the device but busybox tools."
        };
        ToolStripMenuItem receive = new ToolStripMenuItem("Receive file via gzip + base64 (shell)...")
        {
            ToolTipText = "Fetches a file through the shell of a Linux device (OpenWrt too): packed there with gzip, sent as base64 text, unpacked here and checked with a checksum."
        };
        Icons.Apply(send, "upload");
        Icons.Apply(receive, "download");
        send.Click += (sender, e) => StartGzipBase64Transfer(sending: true);
        receive.Click += (sender, e) => StartGzipBase64Transfer(sending: false);
        toolStripButton3.DropDownItems.Add(send);
        toolStripButton3.DropDownItems.Add(receive);
    }

    /// <summary>Asks what to transfer and runs the transfer over the shared line runner. Must be called on the UI thread.</summary>
    private void StartGzipBase64Transfer(bool sending)
    {
        string caption = sending ? "gzip + base64 Send" : "gzip + base64 Receive";
        if (IsLineBusy())
        {
            return;
        }
        if (_connectionState != ConnectionStateEnum.Connected || _asyncTransportConnection == null)
        {
            ThemedMessageBox.Show("Please connect first before using gzip + base64 transfer.", caption, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Func<bool, GzipBase64Transfer.DirectoryLister, GzipBase64Transfer.Request> prompt = GzipBase64RequestPrompt;
        if (prompt == null)
        {
            ThemedMessageBox.Show("The gzip + base64 transfer window is not available.", caption, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        GzipBase64Transfer.Request request = prompt(sending, ListDeviceDirectory);
        if (request == null)
        {
            return;
        }

        string target = _transportDisplayName;
        request.Options.RecoveryRequired = bytes => RememberDeviceRecovery(bytes, target);

        if (sending)
        {
            StartLineTransfer("Send via gzip + base64 - " + Path.GetFileName(request.LocalPath), "gzip + base64", "send",
                (sendData, progress, token) => RunWithDebugLog(request, "send",
                    () => GzipBase64Transfer.SendFile(request.LocalPath, request.RemotePath, request.Options, sendData, ReadXModemByte, token, progress)),
                () => "The file arrived on the device and its checksum matches the original:\r\n" + request.RemotePath,
                AfterGzipBase64Transfer);
        }
        else
        {
            StartLineTransfer("Receive via gzip + base64 - " + Path.GetFileName(request.RemotePath), "gzip + base64", "receive",
                (sendData, progress, token) => RunWithDebugLog(request, "receive",
                    () => GzipBase64Transfer.ReceiveFile(request.RemotePath, request.LocalPath, request.Options, sendData, ReadXModemByte, token, progress)),
                () => "The file was saved and its checksum matches the original on the device:\r\n" + request.LocalPath,
                AfterGzipBase64Transfer);
        }
    }

    /// <summary>
    /// Lists a folder of the device through its shell, for the browse button of the transfer window. The line is claimed only
    /// while the listing runs (a transfer or another listing in progress makes it fail). Must be called on the UI thread.
    /// </summary>
    private Task<GzipBase64Transfer.DirectoryListing> ListDeviceDirectory(string path)
    {
        TaskCompletionSource<GzipBase64Transfer.DirectoryListing> completion =
            new TaskCompletionSource<GzipBase64Transfer.DirectoryListing>(TaskCreationOptions.RunContinuationsAsynchronously);
        AsyncTransportConnection owner = _asyncTransportConnection;
        if (IsLineClaimed || _connectionState != ConnectionStateEnum.Connected || owner == null)
        {
            completion.SetException(new IOException("The connection is closed or the line is busy with a file transfer."));
            return completion.Task;
        }

        // The same claim as a transfer makes: what the device sends goes to the listing and not to the screen.
        while (_xmodemRxQueue.TryTake(out _)) { }
        CancellationTokenSource cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        _lineOwner = owner;
        _cancelLineTransfer = cancel.Cancel;
        _isXModemActive = true;

        ThreadPool.QueueUserWorkItem(state =>
        {
            GzipBase64Transfer.DirectoryListing listing = null;
            Exception error = null;
            try
            {
                XModemTransfer.SendDataHandler sendData = (data, offset, length) =>
                {
                    string writeError = owner.WriteError;
                    if (writeError != null)
                    {
                        throw new IOException(writeError);
                    }
                    byte[] copy = new byte[length];
                    Array.Copy(data, offset, copy, 0, length);
                    if (!SendToConnection(owner, copy, length))
                    {
                        throw new IOException("The connection is closed.");
                    }
                };
                listing = GzipBase64Transfer.ListDirectory(path, null, sendData, ReadXModemByte, cancel.Token);
            }
            catch (OperationCanceledException)
            {
                error = new IOException("The device did not answer in time.");
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                _isXModemActive = false;
                _lineOwner = null;
                _cancelLineTransfer = null;
                while (_xmodemRxQueue.TryTake(out _)) { }
                cancel.Dispose();
            }
            if (error != null)
            {
                completion.SetException(error);
            }
            else
            {
                completion.SetResult(listing);
            }
        });
        return completion.Task;
    }

    /// <summary>
    /// Runs the transfer and, when the user asked for it, writes a log of it to the Temp folder (what was typed to the device,
    /// what it answered, what was decided). A failure then carries the path of the log in its message, so that the final
    /// message tells where to look.
    /// </summary>
    private static bool RunWithDebugLog(GzipBase64Transfer.Request request, string what, Func<bool> transfer)
    {
        StreamWriter log = null;
        string logPath = null;
        if (request.DebugLog)
        {
            try
            {
                logPath = Path.Combine(Path.GetTempPath(), "SmarTTY-gzip-base64-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
                log = new StreamWriter(logPath, false, new UTF8Encoding(false)) { AutoFlush = true };
                log.WriteLine("SmarTTY gzip + base64 transfer (" + what + "), " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + ", " + Environment.OSVersion);
                StreamWriter target = log;
                request.Options.Log = line =>
                {
                    lock (target)
                    {
                        target.WriteLine(line);
                    }
                };
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log?.Dispose();
                log = null;
                logPath = null;
            }
        }
        try
        {
            return transfer();
        }
        catch (Exception ex) when (logPath != null)
        {
            throw new IOException(ex.Message + "\r\n\r\nDebug log: " + logPath, ex);
        }
        finally
        {
            request.Options.Log = null;
            log?.Dispose();
        }
    }

    /// <summary>
    /// Called by a transfer that could not tell the device to stop because its connection was gone (any thread). The bytes that
    /// bring the console back are kept for the next connection to <paramref name="target"/>; if it is already open, they go now.
    /// </summary>
    private void RememberDeviceRecovery(byte[] bytes, string target)
    {
        lock (_recoveryGate)
        {
            _pendingDeviceRecovery = bytes;
            _pendingRecoveryTarget = target;
        }
        try
        {
            BeginInvoke((Action)DeliverPendingGzipBase64Recovery);
        }
        catch (InvalidOperationException)
        {
            // The control is gone: the recovery stays unsent, there is no terminal to send it from.
        }
    }

    /// <summary>
    /// Sends the pending recovery to the device when a connection to the same device is open and free. UI thread. Called when a
    /// connection is established and when a recovery is handed over.
    /// </summary>
    private void DeliverPendingGzipBase64Recovery()
    {
        AsyncTransportConnection connection = _asyncTransportConnection;
        byte[] bytes;
        lock (_recoveryGate)
        {
            if (_pendingDeviceRecovery == null || connection == null || _connectionState != ConnectionStateEnum.Connected || IsLineClaimed)
            {
                return; // stays pending: the connection is not open yet, or another transfer has the line
            }
            if (!string.Equals(_pendingRecoveryTarget, _transportDisplayName, StringComparison.Ordinal))
            {
                // Another device (other port or address): nothing was left waiting on it, and what is typed there is not ours to type.
                _pendingDeviceRecovery = null;
                _pendingRecoveryTarget = null;
                return;
            }
            bytes = _pendingDeviceRecovery;
            _pendingDeviceRecovery = null;
            _pendingRecoveryTarget = null;
        }
        // Give the console a moment after the connection came up (a serial port may still be settling), then type it.
        ThreadPool.QueueUserWorkItem(state =>
        {
            Thread.Sleep(400);
            try
            {
                BeginInvoke((Action)(() =>
                {
                    if (!IsLineClaimed)
                    {
                        SendToConnection(connection, bytes, bytes.Length);
                    }
                }));
            }
            catch (InvalidOperationException)
            {
            }
        });
    }

    /// <summary>
    /// Runs on the UI thread when a transfer has ended and the line is free again. A recovery that was handed over while the
    /// transfer was still ending (the connection was already open again) could not be sent then; it is sent now. The shell prompt
    /// that appeared during the transfer was taken by it; Enter makes the device print a fresh one in the terminal.
    /// </summary>
    private void AfterGzipBase64Transfer()
    {
        DeliverPendingGzipBase64Recovery();
        SendToDevice(new byte[] { (byte)'\r' }, 1);
    }
}
