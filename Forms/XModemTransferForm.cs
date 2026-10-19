using System;
using System.Threading;
using System.Windows.Forms;

namespace SmarTTY.Forms;

public partial class XModemTransferForm : DpiAwareForm
{
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();

    public CancellationToken CancellationToken => _cts.Token;

    public bool IsCancelled => _cts.IsCancellationRequested;

    public XModemTransferForm(string title)
    {
        InitializeComponent();
        Text = title;
    }

    public void UpdateProgress(long bytesTransferred, long totalBytes, int errorCount, string status)
    {
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
                    lblInfo.Text = $"Transferred: {FormatByteCount(bytesTransferred)} / {FormatByteCount(totalBytes)} | Errors: {errorCount}";
                }
                else
                {
                    progressBar1.Style = ProgressBarStyle.Marquee;
                    lblInfo.Text = $"Transferred: {FormatByteCount(bytesTransferred)} | Errors: {errorCount}";
                }
            }));
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void btnCancel_Click(object sender, EventArgs e)
    {
        _cts.Cancel();
        btnCancel.Enabled = false;
        lblStatus.Text = "Cancelling...";
    }

    private static string FormatByteCount(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
        return (bytes / (1024.0 * 1024.0)).ToString("0.0") + " MB";
    }
}
