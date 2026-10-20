using System;
using System.Threading;
using System.Windows.Forms;

namespace SmarTTY.Forms;

public partial class XModemTransferForm : DpiAwareForm
{
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();

    public CancellationToken CancellationToken => _cts.Token;

    public bool IsCancelled => _cts.IsCancellationRequested;

    private volatile string _lastStatus = string.Empty;

    /// <summary>The most recent status text reported by the transfer; explains a failure once it has ended.</summary>
    public string LastStatus => _lastStatus;

    // Off for a plain file send, which has no retransmissions to count.
    private readonly bool _showErrorCount;

    public XModemTransferForm(string title, bool showErrorCount = true)
    {
        InitializeComponent();
        Text = title;
        _showErrorCount = showErrorCount;
    }

    public void UpdateProgress(long bytesTransferred, long totalBytes, int errorCount, string status)
    {
        if (!string.IsNullOrEmpty(status))
        {
            _lastStatus = status;
        }
        if (IsDisposed || !IsHandleCreated) return;

        try
        {
            BeginInvoke((Action)(() =>
            {
                if (IsDisposed) return;

                if (!string.IsNullOrEmpty(status))
                {
                    lblStatus.Text = status;
                }

                if (totalBytes > 0)
                {
                    progressBar1.Style = ProgressBarStyle.Blocks;
                    int percent = (int)Math.Min(100, (bytesTransferred * 100) / totalBytes);
                    progressBar1.Value = percent;
                    lblInfo.Text = $"Transferred: {FormatByteCount(bytesTransferred)} / {FormatByteCount(totalBytes)}" + ErrorsText(errorCount);
                }
                else
                {
                    progressBar1.Style = ProgressBarStyle.Marquee;
                    lblInfo.Text = $"Transferred: {FormatByteCount(bytesTransferred)}" + ErrorsText(errorCount);
                }
            }));
        }
        catch (InvalidOperationException)
        {
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // The window's close button means "stop": without it the transfer would go on invisibly and could not be cancelled.
        // The program closes the window itself (CloseReason.None) once the transfer has ended.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            _cts.Cancel();
        }
        base.OnFormClosing(e);
    }

    private void btnCancel_Click(object sender, EventArgs e)
    {
        _cts.Cancel();
        btnCancel.Enabled = false;
        lblStatus.Text = "Cancelling...";
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
