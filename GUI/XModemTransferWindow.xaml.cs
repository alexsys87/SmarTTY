using System;
using System.Threading;
using System.Windows;
using SmarTTY.Controls;

namespace SmarTTY.GUI;

/// <summary>
/// The progress window of a file transfer (XMODEM, YMODEM, ZMODEM, Kermit). A protocol reports after every block
/// (hundreds of times a second on a fast line); the window shows only the newest values, and at most one update is
/// queued at a time.
/// </summary>
public partial class XModemTransferWindow : Window, ITransferProgressWindow
{
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();

    // Off for a plain file send, which has no retransmissions to count.
    private readonly bool _showErrorCount;

    private volatile string _lastStatus = string.Empty;

    private readonly object _progressLock = new object();

    private long _pendingDone, _pendingTotal;

    private int _pendingErrors;

    private string _pendingStatus;

    private bool _updateQueued;

    private bool _closed;

    private XModemTransferWindow(string title, bool showErrorCount)
    {
        InitializeComponent();
        Title = title;
        _showErrorCount = showErrorCount;
    }

    /// <summary>Creates the window (the title, whether the error count is shown).</summary>
    public static XModemTransferWindow Create(string title, bool showErrorCount)
    {
        return new XModemTransferWindow(title, showErrorCount);
    }

    public CancellationToken CancellationToken => _cts.Token;

    public bool IsCancelled => _cts.IsCancellationRequested;

    /// <summary>The most recent status text reported by the transfer; explains a failure once it has ended.</summary>
    public string LastStatus => _lastStatus;

    // Show and Close are implemented explicitly so they do not hide the identically named Window members.
    void ITransferProgressWindow.Show()
    {
        WindowOwnership.Attach(this, WindowOwnership.ActiveWindow());
        Show();
    }

    public void UpdateProgress(long bytesTransferred, long totalBytes, int errorCount, string status)
    {
        if (!string.IsNullOrEmpty(status))
        {
            _lastStatus = status;
        }
        lock (_progressLock)
        {
            _pendingDone = bytesTransferred;
            _pendingTotal = totalBytes;
            _pendingErrors = errorCount;
            if (!string.IsNullOrEmpty(status))
            {
                _pendingStatus = status;
            }
            if (_updateQueued)
            {
                return;
            }
            _updateQueued = true;
        }
        if (_closed)
        {
            lock (_progressLock) { _updateQueued = false; }
            return;
        }
        Dispatcher.BeginInvoke((Action)ShowProgress);
    }

    private void ShowProgress()
    {
        long bytesTransferred, totalBytes;
        int errorCount;
        string status;
        lock (_progressLock)
        {
            bytesTransferred = _pendingDone;
            totalBytes = _pendingTotal;
            errorCount = _pendingErrors;
            status = _pendingStatus;
            _pendingStatus = null;
            _updateQueued = false;
        }
        if (_closed)
        {
            return;
        }

        if (!string.IsNullOrEmpty(status))
        {
            StatusText.Text = status;
        }

        if (totalBytes > 0)
        {
            Progress.IsIndeterminate = false;
            Progress.Value = Math.Min(100, (bytesTransferred * 100) / totalBytes);
            InfoText.Text = $"Transferred: {FormatByteCount(bytesTransferred)} / {FormatByteCount(totalBytes)}" + ErrorsText(errorCount);
        }
        else
        {
            Progress.IsIndeterminate = true;
            InfoText.Text = $"Transferred: {FormatByteCount(bytesTransferred)}" + ErrorsText(errorCount);
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // The window's close button means "stop": without it the transfer would go on invisibly and could not be cancelled.
        // The program closes the window itself once the transfer has ended.
        if (!_closed)
        {
            _cts.Cancel();
        }
        _closed = true;
        base.OnClosing(e);
    }

    /// <summary>Stops the transfer (as the Cancel button does), e.g. when its connection is replaced.</summary>
    public void Cancel()
    {
        _cts.Cancel();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        CancelButton.IsEnabled = false;
        StatusText.Text = "Cancelling...";
    }

    /// <summary>Closes the window after the transfer has ended, without treating it as a cancellation.</summary>
    void ITransferProgressWindow.Close()
    {
        Dispose();
    }

    public void Dispose()
    {
        if (!_closed)
        {
            _closed = true;
            Close();
        }
        _cts.Dispose();
    }

    private string ErrorsText(int errorCount)
    {
        return _showErrorCount ? " | Errors: " + errorCount : string.Empty;
    }

    private static string FormatByteCount(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
        return (bytes / (1024.0 * 1024.0)).ToString("0.0") + " MB";
    }
}
