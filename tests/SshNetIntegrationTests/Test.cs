using System;
using System.Collections.Generic;
using System.Threading;

namespace SmarTTY.Ssh.Tests;

/// <summary>
/// Minimal console test harness: the standalone harness pattern of
/// tests/CommandCompletionDatabaseTests, so no test framework is needed.
/// </summary>
internal static class Test
{
    private static readonly List<string> Failures = new List<string>();

    private static int _passed;

    public static void Section(string name)
    {
        Console.WriteLine();
        Console.WriteLine("== " + name);
    }

    public static void Check(bool condition, string name)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine("PASS: " + name);
            return;
        }
        Failures.Add(name);
        Console.WriteLine("FAIL: " + name);
    }

    public static void Equal<T>(T expected, T actual, string name)
    {
        Check(EqualityComparer<T>.Default.Equals(expected, actual), name + " (expected: " + Describe(expected) + ", actual: " + Describe(actual) + ")");
    }

    public static void Throws<TException>(Action action, string name, Action<TException> verify = null)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException ex)
        {
            verify?.Invoke(ex);
            _passed++;
            Console.WriteLine("PASS: " + name);
            return;
        }
        catch (Exception ex)
        {
            Failures.Add(name);
            Console.WriteLine("FAIL: " + name + " (threw " + ex.GetType().Name + ": " + ex.Message + " instead of " + typeof(TException).Name + ")");
            return;
        }
        Failures.Add(name);
        Console.WriteLine("FAIL: " + name + " (no " + typeof(TException).Name + " was thrown)");
    }

    /// <summary>
    /// Fails when <paramref name="condition"/> does not become true in time; keeps a hang from blocking CI.
    /// </summary>
    public static void WaitUntil(Func<bool> condition, TimeSpan timeout, string name)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                _passed++;
                Console.WriteLine("PASS: " + name);
                return;
            }
            Thread.Sleep(20);
        }
        Failures.Add(name);
        Console.WriteLine("FAIL: " + name + " (timed out after " + timeout.TotalSeconds + "s)");
    }

    /// <summary>
    /// Aborts the whole run if a test hangs; without it a stuck SSH call would block CI until the job limit.
    /// </summary>
    public static void StartWatchdog(TimeSpan timeout)
    {
        Thread watchdog = new Thread(() =>
        {
            Thread.Sleep(timeout);
            Console.Error.WriteLine("FAIL: the test run exceeded " + timeout.TotalSeconds + "s and was aborted.");
            Environment.Exit(1);
        })
        {
            IsBackground = true,
            Name = "test watchdog"
        };
        watchdog.Start();
    }

    public static int Report(string title)
    {
        Console.WriteLine();
        if (Failures.Count == 0)
        {
            Console.WriteLine(title + ": all " + _passed + " checks passed.");
            return 0;
        }
        Console.WriteLine(title + ": " + Failures.Count + " of " + (_passed + Failures.Count) + " checks failed:");
        foreach (string failure in Failures)
        {
            Console.WriteLine("  - " + failure);
        }
        return 1;
    }

    private static string Describe(object value)
    {
        if (value == null)
        {
            return "null";
        }
        string text = value.ToString();
        return (text.Length > 120) ? text.Substring(0, 120) + "..." : text;
    }
}
