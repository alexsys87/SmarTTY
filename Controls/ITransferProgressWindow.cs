using System;
using System.Threading;

namespace SmarTTY.Controls;

/// <summary>
/// The progress window of a file transfer protocol (XMODEM, YMODEM, ZMODEM, Kermit): it shows the transferred bytes
/// and errors, and its close button stops the transfer. The device terminal is compiled by the Mono test harness,
/// which cannot reference the themed WPF window, so the window is injected through
/// <see cref="DeviceTerminalControl.TransferProgressWindowFactory"/>.
/// </summary>
public interface ITransferProgressWindow : IDisposable
{
    /// <summary>Cancelled when the user stops the transfer (the close button or Cancel).</summary>
    CancellationToken CancellationToken { get; }

    bool IsCancelled { get; }

    /// <summary>The most recent status text reported by the transfer; explains a failure once it has ended.</summary>
    string LastStatus { get; }

    /// <summary>Shows the modeless window over the active window. Must be called on the UI thread.</summary>
    void Show();

    /// <summary>Shows the newest progress; may be called from a worker thread.</summary>
    void UpdateProgress(long bytesTransferred, long totalBytes, int errorCount, string status);

    /// <summary>Stops the transfer, as the Cancel button does.</summary>
    void Cancel();

    /// <summary>Closes the window; the transfer has ended. Safe to call more than once.</summary>
    void Close();
}

/// <summary>
/// The progress window used when none was injected: it keeps the transfer cancellable but shows nothing. Only the
/// test harness runs without a window, and it does not transfer files, so this is a safety net.
/// </summary>
internal sealed class HeadlessTransferProgressWindow : ITransferProgressWindow
{
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();

    public CancellationToken CancellationToken => _cts.Token;

    public bool IsCancelled => _cts.IsCancellationRequested;

    public string LastStatus { get; private set; } = string.Empty;

    public void Show()
    {
    }

    public void UpdateProgress(long bytesTransferred, long totalBytes, int errorCount, string status)
    {
        if (!string.IsNullOrEmpty(status))
        {
            LastStatus = status;
        }
    }

    public void Cancel()
    {
        _cts.Cancel();
    }

    public void Close()
    {
    }

    public void Dispose()
    {
        _cts.Dispose();
    }
}
