using SmarTTY.Transport;
using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace SmarTTY.Controls;

// "Send file": the file goes to the device as a stream of small writes from a worker thread, with a progress window
// that can cancel it. Only a few writes are queued at a time, so the progress shows what really went out (a 9600 baud
// line needs minutes for a large file) and Cancel stops within moments instead of after the whole file.
public partial class DeviceTerminalControl
{
    private const int FileSendChunkBytes = 1024;

    // Writes queued ahead of the port: enough to keep it busy, few enough that Cancel takes effect quickly.
    private const int FileSendWritesInFlight = 4;

    private volatile bool _isFileSendActive;

    /// <summary>Must be called on the UI thread; the line stays claimed until the file has been sent or the send stopped.</summary>
    private void StartFileSend(string fileName)
    {
        AsyncTransportConnection connection = _asyncTransportConnection;
        if (IsLineClaimed || connection == null)
        {
            return;
        }
        string title = "Send file - " + Path.GetFileName(fileName);
        // Not a "using": the window is modeless and outlives this method; it is closed (and disposed) when the send ends.
        ITransferProgressWindow progressForm = CreateTransferProgressWindow(title, showErrorCount: false);
        progressForm.Show();
        _isFileSendActive = true;
        UpdateSerialLineUi();
        bool echo = HexMode && _deviceTerminalSettings.EchoTypedCharacters;

        ThreadPool.QueueUserWorkItem(state =>
        {
            string error = null;
            // Filled in as the writes go through, so a failure can still say how much went out.
            long[] written = new long[1];
            try
            {
                SendFileThrough(connection, fileName, progressForm, echo, written);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            finally
            {
                _isFileSendActive = false;
            }
            try
            {
                BeginInvoke((Action)(() =>
                {
                    progressForm.Dispose();
                    UpdateSerialLineUi();
                    if (error != null)
                    {
                        MessageBox.Show("The file was not sent completely (" + FormatByteCount(Interlocked.Read(ref written[0])) + " went out).\r\n\r\n" + error,
                            "Send file", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }));
            }
            catch (InvalidOperationException)
            {
                // The terminal was closed while the file was being sent: nobody is left to tell.
            }
        });
    }

    /// <summary>
    /// Runs on a worker thread. <paramref name="written"/>[0] counts the bytes that reached the device; Cancel ends the
    /// send without an error, a failed write or a closed connection throws.
    /// </summary>
    private void SendFileThrough(AsyncTransportConnection connection, string fileName, ITransferProgressWindow progressForm, bool echo, long[] written)
    {
        bool cancelled = false;
        long queuedBytes = 0;
        int failed = 0;
        int lastProgressTick = Environment.TickCount;
        // One slot per write that may be queued; a slot comes back when its write has finished, well or not.
        SemaphoreSlim slots = new SemaphoreSlim(FileSendWritesInFlight, FileSendWritesInFlight);
        CancellationToken cancellation = progressForm.CancellationToken;
        using FileStream stream = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read, FileSendChunkBytes * 4);
        long total = stream.Length;
        progressForm.UpdateProgress(0, total, 0, "Sending " + FormatByteCount(total) + "...");
        while (true)
        {
            if (!WaitForSlot(slots, connection, cancellation))
            {
                cancelled = cancellation.IsCancellationRequested;
                break;
            }
            if (Volatile.Read(ref failed) != 0)
            {
                slots.Release();
                break;
            }
            byte[] chunk = new byte[FileSendChunkBytes];
            int count = stream.Read(chunk, 0, chunk.Length);
            if (count <= 0)
            {
                slots.Release();
                break;
            }
            int chunkLength = count;
            bool queued = connection.Send(chunk, chunkLength, ok =>
            {
                if (ok)
                {
                    long done = Interlocked.Add(ref written[0], chunkLength);
                    // A few updates a second are enough; a fast port finishes a chunk every millisecond.
                    int now = Environment.TickCount;
                    if (done >= total || now - Volatile.Read(ref lastProgressTick) >= 100)
                    {
                        Volatile.Write(ref lastProgressTick, now);
                        progressForm.UpdateProgress(done, total, 0, null);
                    }
                }
                else
                {
                    Interlocked.Exchange(ref failed, 1);
                }
                slots.Release();
            });
            if (!queued)
            {
                throw new IOException("The connection is closed.");
            }
            queuedBytes += chunkLength;
            Interlocked.Add(ref _bytesSent, chunkLength);
            if (echo)
            {
                PostEcho(chunk, chunkLength);
            }
        }
        // Let the writes already queued finish, so the result and the byte count are final. After Cancel they are not
        // waited for: the few KB already handed to the port go out on their own.
        for (int i = 0; i < FileSendWritesInFlight; i++)
        {
            if (!WaitForSlot(slots, connection, cancellation))
            {
                break;
            }
        }
        long done = Interlocked.Read(ref written[0]);
        if (cancelled || cancellation.IsCancellationRequested)
        {
            return;
        }
        if (Volatile.Read(ref failed) != 0 || done < queuedBytes)
        {
            if (connection.HasEnded)
            {
                throw new IOException("The connection was closed.");
            }
            throw new IOException(connection.WriteError ?? "The device did not accept the data.");
        }
    }

    /// <summary>Takes a write slot; false when the send was cancelled or the connection ended while waiting.</summary>
    private static bool WaitForSlot(SemaphoreSlim slots, AsyncTransportConnection connection, CancellationToken cancellation)
    {
        while (!slots.Wait(200))
        {
            if (cancellation.IsCancellationRequested || connection.HasEnded)
            {
                return false;
            }
        }
        if (cancellation.IsCancellationRequested)
        {
            slots.Release();
            return false;
        }
        return true;
    }

    private void PostEcho(byte[] chunk, int count)
    {
        try
        {
            BeginInvoke((Action)(() =>
            {
                if (!IsDisposed && HexMode)
                {
                    hexTerminalControl1.AppendData(chunk, count, isEcho: true);
                }
            }));
        }
        catch (InvalidOperationException)
        {
            // The terminal is closing.
        }
    }
}
