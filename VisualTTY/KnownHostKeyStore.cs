using SmarTTY.Ssh;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Xml.Serialization;

namespace VisualTTY;

internal class KnownHostKeyStore
{
    private const int FileAttempts = 5;

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    // Threads of this process wait for each other here, other processes (a second window of the program) at the lock file.
    private static readonly object ProcessLock = new object();

    private List<KnownHostKey> _keys;

    // What this instance added or replaced since it read the file (or saved it). Save applies it to what is in the file at that
    // moment, so that the keys another window or process accepted in the meantime are not thrown away by a list that went stale
    // while a confirmation dialog was open.
    private readonly List<KnownHostKey> _changes = new List<KnownHostKey>();

    // True when the file was damaged and a copy of it was kept: a new file may then be written over it.
    private bool _damagedCopyKept;

    /// <summary>
    /// Why the list could not be used as it is (the file is damaged, or it cannot be read), worded for the user, or null.
    /// Without it nobody would know that the hosts accepted so far are suddenly unknown again.
    /// </summary>
    public string LoadProblem { get; private set; }

    private static string FilePath => Path.Combine(GlobalSettings.DataDirectory, "KnownHosts.xml");

    private enum ReadOutcome
    {
        Read,
        Missing,
        Damaged,
        Unavailable
    }

    public KnownHostKeyStore()
    {
        ReadOutcome outcome = TryRead(out List<KnownHostKey> keys, out Exception problem);
        if (outcome == ReadOutcome.Read)
        {
            _keys = keys;
            return;
        }
        // Whatever is wrong, the host keys accepted so far are not known now: every host asks for confirmation again.
        _keys = new List<KnownHostKey>();
        if (outcome == ReadOutcome.Damaged)
        {
            // A file that exists but cannot be understood is not the same as no file: the next Save would overwrite it and
            // every host key the user has accepted so far would be gone. Keep a copy.
            string copy = KeepCopyOfDamagedFile();
            _damagedCopyKept = copy != null;
            LoadProblem = "The list of known SSH hosts (" + FilePath + ") is damaged (" + Describe(problem) + ")." +
                (copy != null ? " A copy of it was kept as " + copy + "." : " It could not be copied, so it is left as it is.") +
                " The host keys accepted before are not known now, so every host asks for confirmation again.";
        }
        else if (outcome == ReadOutcome.Unavailable)
        {
            LoadProblem = "The list of known SSH hosts (" + FilePath + ") cannot be read (" + Describe(problem) + "). " +
                "Every host asks for confirmation again, and new host keys are not saved until the file can be read, " +
                "so that the file is not overwritten.";
        }
    }

    // Host names are not case sensitive: "Example.com" and "example.com" are the same host.
    private static bool SameHost(string first, string second)
    {
        return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    }

    public KnownHostKey Find(string text)
    {
        foreach (KnownHostKey item in this._keys)
        {
            if (SameHost(item.HostName, text))
            {
                return item;
            }
        }
        return null;
    }

    /// <summary>
    /// Writes what this instance changed into the file. Returns null when that worked, otherwise why it did not (to be shown to the
    /// user: the host is then asked about again at the next connection). It does not throw.
    /// The file is read again under a lock and the changes are applied to it, so keys that another window or process saved after
    /// this instance read the file are kept, and a file that cannot be read is never overwritten.
    /// </summary>
    public string Save()
    {
        if (_changes.Count == 0)
        {
            return null;
        }
        try
        {
            lock (ProcessLock)
            {
                using (AcquireFileLock())
                {
                    ReadOutcome outcome = TryRead(out List<KnownHostKey> current, out Exception problem);
                    if (outcome == ReadOutcome.Damaged && _damagedCopyKept)
                    {
                        // The damaged file was copied when it was read: the new file may replace it.
                        current = new List<KnownHostKey>();
                    }
                    else if (outcome == ReadOutcome.Damaged || outcome == ReadOutcome.Unavailable)
                    {
                        return "The host key could not be saved: the list of known SSH hosts (" + FilePath + ") cannot be read (" +
                            Describe(problem) + ") and is not overwritten. The host asks for confirmation again next time.";
                    }
                    foreach (KnownHostKey change in _changes)
                    {
                        current.RemoveAll(item => SameHost(item.HostName, change.HostName));
                        current.Add(change);
                    }
                    WriteFile(current);
                    _keys = current;
                    _changes.Clear();
                    _damagedCopyKept = false;
                    return null;
                }
            }
        }
        catch (Exception exception)
        {
            return "The host key could not be saved (" + Describe(exception) + "). The host asks for confirmation again next time.";
        }
    }

    public void Update(string text, SshSession.HostKeyInfo hostKeyInfo)
    {
        KnownHostKey key = new KnownHostKey
        {
            HostName = text,
            KeyType = hostKeyInfo.Algorithm,
            KeyBase64 = Convert.ToBase64String(hostKeyInfo.KeyData)
        };
        this._keys.RemoveAll(item => SameHost(item.HostName, text));
        this._keys.Add(key);
        _changes.RemoveAll(item => SameHost(item.HostName, text));
        _changes.Add(key);
    }

    // Reads the file. "Missing" is the normal state of a program that has not accepted a host yet. A file that is held by someone
    // for a moment (a virus scanner, another window swapping it) is tried again a few times before it counts as unavailable.
    private static ReadOutcome TryRead(out List<KnownHostKey> keys, out Exception problem)
    {
        keys = new List<KnownHostKey>();
        problem = null;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    return ReadOutcome.Missing;
                }
                XmlSerializer xmlSerializer = new XmlSerializer(typeof(List<KnownHostKey>));
                // Another window may swap the file while it is read (Save does that): let it.
                using (FileStream stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    keys = (List<KnownHostKey>)xmlSerializer.Deserialize(stream) ?? new List<KnownHostKey>();
                }
                return ReadOutcome.Read;
            }
            catch (Exception exception) when (IsAccessProblem(exception))
            {
                if (attempt >= FileAttempts)
                {
                    problem = exception;
                    return ReadOutcome.Unavailable;
                }
                Thread.Sleep(50 * attempt);
            }
            catch (Exception exception)
            {
                problem = exception;
                return ReadOutcome.Damaged;
            }
        }
    }

    // The XML serializer wraps what the stream throws, so the cause is looked for inside.
    private static bool IsAccessProblem(Exception exception)
    {
        for (Exception inner = exception; inner != null; inner = inner.InnerException)
        {
            if (inner is IOException || inner is UnauthorizedAccessException)
            {
                return true;
            }
        }
        return false;
    }

    // The message of the root cause: the serializer's own text only says that there is an error in the document.
    private static string Describe(Exception exception)
    {
        while (exception.InnerException != null)
        {
            exception = exception.InnerException;
        }
        return exception.Message;
    }

    private static string KeepCopyOfDamagedFile()
    {
        try
        {
            string copy = FilePath + ".unreadable";
            File.Copy(FilePath, copy, overwrite: true);
            return copy;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // The lock file goes away with its last handle, also when a process dies while it holds it.
    private static FileStream AcquireFileLock()
    {
        Directory.CreateDirectory(GlobalSettings.DataDirectory);
        string lockPath = FilePath + ".lock";
        Stopwatch watch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                if (watch.Elapsed >= LockTimeout)
                {
                    throw;
                }
                Thread.Sleep(20);
            }
        }
    }

    private static void WriteFile(List<KnownHostKey> keys)
    {
        XmlSerializer xmlSerializer = new XmlSerializer(typeof(List<KnownHostKey>));
        // Write next to the file and swap, so that a crash in the middle does not leave a half-written store.
        string temporaryPath = FilePath + ".tmp";
        try
        {
            using (FileStream stream = File.Create(temporaryPath))
            {
                xmlSerializer.Serialize(stream, keys);
            }
            ReplaceFile(temporaryPath, FilePath);
        }
        catch (Exception)
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception)
            {
            }
            throw;
        }
    }

    // Swaps the new file in with one call, so that a failure leaves the old file as it was. A virus scanner may hold either file
    // for a moment, so a failure is tried again a few times. Metadata errors (ACLs, attributes) are ignored.
    private static void ReplaceFile(string temporary, string target)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(target))
                {
                    try
                    {
                        File.Replace(temporary, target, null, true);
                        return;
                    }
                    catch (FileNotFoundException) when (!File.Exists(target))
                    {
                        // The old file was removed between the check and the swap: there is nothing to replace.
                    }
                }
                File.Move(temporary, target);
                return;
            }
            catch (Exception exception) when (attempt < FileAttempts && (exception is IOException || exception is UnauthorizedAccessException))
            {
                Thread.Sleep(50 * attempt);
            }
        }
    }
}
