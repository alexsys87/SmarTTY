using System;
using System.Threading;

namespace SmarTTY.Ssh;

/// <summary>
/// Dropbear, the SSH server of OpenWRT, silently drops the pty-req/exec/shell request of a channel with a PTY when
/// the process of a channel without one exits while it handles that request: no reply comes, the command never
/// starts and the caller waits until the session timeout. A Smart Terminal command opened together with the history
/// write (a short command without a PTY) hung the tab that way. OpenSSH is not affected.
///
/// The gate keeps the short helper commands without a PTY (<see cref="SshSession.RunCommand"/>) from overlapping
/// with setting up a PTY channel: the setup waits until the running helpers have finished, and new helpers wait until
/// the setup is done. A helper that runs for longer (or hangs) delays the setup by at most the timeout.
/// </summary>
internal sealed class PtySetupGate
{
    private readonly object _sync = new object();

    private readonly TimeSpan _timeout;

    private int _runningPlainHelpers;

    private bool _ptySetupInProgress;

    public PtySetupGate(TimeSpan timeout)
    {
        _timeout = timeout;
    }

    public void BeginPlainHelper()
    {
        lock (_sync)
        {
            DateTime deadline = DateTime.UtcNow + _timeout;
            while (_ptySetupInProgress && Wait(deadline))
            {
            }
            _runningPlainHelpers++;
        }
    }

    public void EndPlainHelper()
    {
        lock (_sync)
        {
            _runningPlainHelpers--;
            Monitor.PulseAll(_sync);
        }
    }

    public void BeginPtySetup()
    {
        lock (_sync)
        {
            DateTime deadline = DateTime.UtcNow + _timeout;
            while ((_ptySetupInProgress || _runningPlainHelpers > 0) && Wait(deadline))
            {
            }
            _ptySetupInProgress = true;
        }
    }

    public void EndPtySetup()
    {
        lock (_sync)
        {
            _ptySetupInProgress = false;
            Monitor.PulseAll(_sync);
        }
    }

    // Must be called under _sync; false once the deadline has passed.
    private bool Wait(DateTime deadline)
    {
        TimeSpan remaining = deadline - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return false;
        }
        Monitor.Wait(_sync, remaining);
        return true;
    }
}
