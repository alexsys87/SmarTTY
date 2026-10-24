using SmarTTY.IO;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Threading;

namespace SmarTTY.Ssh;

/// <summary>
/// The free disk space of the SSH host that the bar at the bottom of the terminal shows. Every
/// <see cref="Interval"/> it runs <c>df -h</c> on the host (in a channel of its own, so the terminal is not disturbed),
/// takes the row of the root file system and publishes it for binding. One monitor belongs to one SSH session; after a
/// reconnect <see cref="Attach"/> points it at the new connection. The bar is filled by the share of the space that is used (like the "Use%" column), the text names what is left.
/// </summary>
public sealed class DiskSpaceMonitor : INotifyPropertyChanged, IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    /// <summary>The bar turns red when less than this share of the disk is free.</summary>
    public const int LowFreePercent = 10;

    private readonly object _Sync = new object();

    private SshSession _Session;

    private Timer _Timer;

    private int _Generation;

    private bool _Disposed;

    private bool _IsAvailable;

    private bool _IsStale;

    private int _UsedPercent;

    private string _Text = "";

    private string _ToolTipText = "";

    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>True once a reading has arrived; the bar stays hidden until then (and on a host without a working df).</summary>
    public bool IsAvailable => _IsAvailable;

    /// <summary>True when the last attempt to read the disk failed: the figures shown are the last good ones.</summary>
    public bool IsStale => _IsStale;

    /// <summary>The used share of the disk, 0 to 100 (the "Use%" column of df): what the progress bar shows.</summary>
    public double UsedPercent => _UsedPercent;

    public bool IsLow => _IsAvailable && 100 - _UsedPercent < LowFreePercent;

    public string Text => _Text;

    /// <summary>The full output of <c>df -h</c>.</summary>
    public string ToolTipText => _ToolTipText;

    /// <summary>Starts reading the disk of <paramref name="session"/> (the first connection or a new one after a reconnect).</summary>
    public void Attach(SshSession session)
    {
        lock (_Sync)
        {
            if (_Disposed)
            {
                return;
            }
            _Session = session;
            _Generation++;
            _Timer?.Dispose();
            _Timer = session == null ? null : new Timer(OnTimer, _Generation, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (_Sync)
        {
            _Disposed = true;
            _Session = null;
            _Generation++;
            _Timer?.Dispose();
            _Timer = null;
        }
    }

    private void OnTimer(object state)
    {
        int generation = (int)state;
        SshSession session;
        lock (_Sync)
        {
            if (_Disposed || generation != _Generation)
            {
                return;
            }
            session = _Session;
        }
        try
        {
            Poll(session, generation);
        }
        catch
        {
            // A failed reading must never end the polling or reach the thread pool as an unhandled exception.
            Publish(generation, null, null);
        }
        finally
        {
            lock (_Sync)
            {
                // The next reading starts a full interval after this one has finished, so slow answers never pile up.
                if (!_Disposed && generation == _Generation)
                {
                    try
                    {
                        _Timer?.Change(Interval, Timeout.InfiniteTimeSpan);
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
            }
        }
    }

    private void Poll(SshSession session, int generation)
    {
        if (session == null || session.IsConnectionClosed)
        {
            Publish(generation, null, null);
            return;
        }
        List<string> output = session.RunCommand("df -h", out _);
        List<DfOutputParser.DiskUsage> rows = DfOutputParser.Parse(output);
        Publish(generation, DfOutputParser.SelectMain(rows), output);
    }

    private void Publish(int generation, DfOutputParser.DiskUsage usage, List<string> output)
    {
        lock (_Sync)
        {
            if (_Disposed || generation != _Generation)
            {
                // A reconnect or the end of the session has overtaken this reading.
                return;
            }
        }
        if (usage == null)
        {
            if (_IsStale)
            {
                return;
            }
            // Keep the last good figures on the screen and only mark them as old.
            _IsStale = true;
            Raise(nameof(IsStale));
            return;
        }
        string disk = string.IsNullOrEmpty(usage.MountPoint) ? usage.Filesystem : usage.MountPoint;
        _UsedPercent = usage.UsePercent;
        _Text = string.Format(CultureInfo.InvariantCulture, "Disk {0}: {1} free of {2} ({3}% used)", disk, usage.Available, usage.Size, usage.UsePercent);
        _ToolTipText = output == null ? "" : string.Join(Environment.NewLine, output);
        _IsStale = false;
        _IsAvailable = true;
        Raise(nameof(UsedPercent));
        Raise(nameof(IsLow));
        Raise(nameof(Text));
        Raise(nameof(ToolTipText));
        Raise(nameof(IsStale));
        Raise(nameof(IsAvailable));
    }

    private void Raise(string property)
    {
        // WPF marshals the change of a plain property binding to the UI thread by itself.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}
