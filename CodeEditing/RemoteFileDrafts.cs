using SmarTTY.IO;
using SmarTTY.TextEditing;
using System;
using System.Threading;

namespace SmarTTY.CodeEditing;

/// <summary>
/// The draft of the unsaved changes of one remote file editor: which draft the editor has (its key), when the text is taken, and the
/// writes and removals, which run on the thread pool so that the latest request wins. The code is that of <c>RemoteFileEditor</c>, moved
/// here as it was; the timer that asks for a draft, the reading of the text, and the question "restore the draft?" stay in the editor.
/// </summary>
public sealed class RemoteFileDrafts
{
    /// <summary>Drafts older than this are removed when an editor starts; a week before that the user is told which ones are about to go.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    private static readonly TimeSpan WarnBefore = TimeSpan.FromDays(7);

    private static int _cleanupDone;

    // Said once per run of the program, in the first editor that finishes loading (see StartCleanupOnce).
    private static string _pendingNotice;

    private readonly EditorDraftStore _store;

    private readonly string _key;

    // The text version the draft on disk holds (null version = the text as opened is not a draft).
    private volatile bool _written;

    private object _version;

    private readonly DraftSchedule _schedule = new DraftSchedule();

    // The size of the text as opened or as of the last draft: how large the text is is not known cheaply otherwise.
    private long _sizeEstimate;

    // Every draft write or removal takes a number; one that is no longer the latest by the time it runs is skipped, so
    // a late write cannot bring back the draft of a text that has been saved meanwhile.
    private int _sequence;

    private readonly object _lock = new object();

    /// <param name="store">The store of all drafts (one folder).</param>
    /// <param name="key">The key the editor claimed (see <see cref="DraftKeyClaims"/>).</param>
    public RemoteFileDrafts(EditorDraftStore store, string key)
    {
        _store = store;
        _key = key;
    }

    /// <summary>The hash of the text the editor opened (or last loaded): a draft that has another one was made of a file that has changed since.</summary>
    public string BaseHash { get; set; } = "";

    /// <summary>The size of the text as opened or as of the last draft, in characters.</summary>
    public long SizeEstimate
    {
        get => _sizeEstimate;
        set => _sizeEstimate = value;
    }

    /// <summary>
    /// Removes the drafts that are too old, once per run of the program (the first editor does it, on the thread pool), and remembers the
    /// notice about the drafts that are about to expire for <see cref="TakeExpiryNotice"/>.
    /// </summary>
    public static void StartCleanupOnce(EditorDraftStore store)
    {
        if (Interlocked.Exchange(ref _cleanupDone, 1) == 0)
        {
            ThreadPool.QueueUserWorkItem((object state) =>
            {
                store.DeleteOlderThan(MaxAge);
                _pendingNotice = EditorDraftStore.DescribeExpiring(store.FindExpiring(MaxAge, WarnBefore), DateTime.UtcNow);
            });
        }
    }

    /// <summary>The notice about the drafts that are about to expire, once: null when there is none or an editor has taken it.</summary>
    public static string TakeExpiryNotice()
    {
        return Interlocked.Exchange(ref _pendingNotice, null);
    }

    /// <summary>
    /// The draft left by an earlier editor of this file (the connection was lost or the program ended before the changes were saved), or
    /// null when there is none or it holds what the server has: <paramref name="serverBytes"/> (the file) or <paramref name="serverText"/>.
    /// A draft that holds the server's version is removed.
    /// </summary>
    public EditorDraft FindUnsaved(byte[] serverBytes, string serverText)
    {
        EditorDraft draft = _store.TryLoad(_key);
        if (draft == null)
        {
            return null;
        }
        bool same = (draft.Bytes != null)
            ? HexDocument.ByteArraysEqual(draft.Bytes, serverBytes ?? Array.Empty<byte>())
            : (draft.Text == serverText);
        if (same)
        {
            // Nothing to restore: the changes were saved after all.
            Queue(null);
            return null;
        }
        return draft;
    }

    /// <summary>The file on the server has changed since <paramref name="draft"/> was made of it.</summary>
    public bool ServerChangedSince(EditorDraft draft)
    {
        return !draft.IsNewFile && draft.BaseHash != BaseHash;
    }

    /// <summary>
    /// Whether to take a draft of the version <paramref name="version"/> now (a text that has changed): false when this version is already
    /// the draft, or when the text is large and the schedule says to wait. On true the version counts as written; call <see cref="Captured"/>
    /// after the text was read. <paramref name="bytesSize"/> is the size of the hex view, or null for the text (its size is the estimate).
    /// </summary>
    public bool ShouldCapture(object version, long? bytesSize)
    {
        if (_written && ReferenceEquals(version, _version))
        {
            return false;
        }
        // Reading the text takes the UI thread for as long as the text is large: not while the user is typing in it.
        long size = bytesSize ?? _sizeEstimate;
        if (!_schedule.ShouldCapture(version, size, DateTime.UtcNow))
        {
            return false;
        }
        _written = true;
        _version = version;
        return true;
    }

    /// <summary>The text of the draft was read: its size is the new estimate.</summary>
    public void Captured(long size)
    {
        _sizeEstimate = size;
        _schedule.Captured(DateTime.UtcNow);
    }

    /// <summary>Writes <paramref name="draft"/>, or removes the draft when it is null; the latest request wins.</summary>
    public void Queue(EditorDraft draft)
    {
        int sequence = Interlocked.Increment(ref _sequence);
        ThreadPool.QueueUserWorkItem((object state) =>
        {
            lock (_lock)
            {
                if (sequence != Volatile.Read(ref _sequence))
                {
                    return;
                }
                try
                {
                    if (draft == null)
                    {
                        _store.Delete(_key);
                    }
                    else
                    {
                        _store.Save(_key, draft);
                    }
                }
                catch
                {
                    // The text on disk is not the one that was meant to be written: let the next timer tick try again (the
                    // version was marked as written before this ran). A newer request has its own outcome.
                    if (draft != null && sequence == Volatile.Read(ref _sequence))
                    {
                        _written = false;
                    }
                }
            }
        });
    }

    /// <summary>
    /// The user chose not to save the changes (closing the tab or the program): the draft is removed, otherwise the next
    /// opening of the file would offer them again.
    /// </summary>
    public void Discard()
    {
        _written = false;
        _version = null;
        Queue(null);
    }
}
