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
/// the setups that are waiting or running are done. A helper that runs for longer (or hangs) delays the setup by at most the
/// timeout. A long operation (a backup, a restore, an upload of a script) is a helper only while its channel is being opened
/// (<see cref="SshSession.OpenPlainChannel"/>): holding the gate for the whole run made every new tab wait for the timeout and
/// then set up its PTY in the very moment that the gate exists to avoid.
/// </summary>
internal sealed class PtySetupGate
{
    private readonly object _sync = new object();

    private readonly TimeSpan _timeout;

    private int _runningPlainHelpers;

    // Setups of a PTY that run now (a number: two that ran out of time together are both in progress, and the first to end must not
    // open the gate for the helpers while the second still sets up) and that wait for the helpers to end. Both keep new helpers back,
    // or a steady stream of short helpers would hold a setup for the whole timeout.
    private int _ptySetupsInProgress;

    private int _ptySetupsWaiting;

    public PtySetupGate(TimeSpan timeout)
    {
        _timeout = timeout;
    }

    public void BeginPlainHelper()
    {
        lock (_sync)
        {
            DateTime deadline = DateTime.UtcNow + _timeout;
            while ((_ptySetupsInProgress > 0 || _ptySetupsWaiting > 0) && Wait(deadline))
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
            _ptySetupsWaiting++;
            try
            {
                while ((_ptySetupsInProgress > 0 || _runningPlainHelpers > 0) && Wait(deadline))
                {
                }
            }
            finally
            {
                _ptySetupsWaiting--;
            }
            _ptySetupsInProgress++;
        }
    }

    public void EndPtySetup()
    {
        lock (_sync)
        {
            if (_ptySetupsInProgress > 0)
            {
                _ptySetupsInProgress--;
            }
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
