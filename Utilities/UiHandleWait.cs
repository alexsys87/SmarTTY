using System;
using System.Threading;

namespace SmarTTY.Utilities;

/// <summary>
/// Waits, on a worker thread, for the window handle of a control that the worker's owner has just created. A thread that is started in the
/// constructor of a control can be faster than the creation of its handle, and Invoke or BeginInvoke called before that throw
/// "Invoke or BeginInvoke cannot be called on a control until the window handle has been created".
/// </summary>
internal static class UiHandleWait
{
    /// <summary>The longest wait: 600 tries, 50 ms apart, 30 seconds.</summary>
    public const int Attempts = 600;

    public const int StepMilliseconds = 50;

    /// <summary>
    /// True when the handle exists; false when the control was closed first or the handle did not appear in time. The closed
    /// check comes first: a control that is being disposed has no handle to wait for.
    /// </summary>
    public static bool Until(Func<bool> closed, Func<bool> handleCreated, Action<int> sleep = null, int attempts = Attempts)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (closed())
            {
                return false;
            }
            if (handleCreated())
            {
                return true;
            }
            if (sleep != null)
            {
                sleep(StepMilliseconds);
            }
            else
            {
                Thread.Sleep(StepMilliseconds);
            }
        }
        return false;
    }
}
