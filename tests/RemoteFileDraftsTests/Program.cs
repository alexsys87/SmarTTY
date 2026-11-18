using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using SmarTTY.CodeEditing;
using SmarTTY.IO;

// Checks of the draft of the unsaved changes of a remote file editor (CodeEditing/RemoteFileDrafts.cs, CodeEditing/DraftKeyClaims.cs): the keys
// that two editors of one file get, the draft that an earlier editor left, when a text is taken, and the writes and removals of which the latest
// request wins. They run on a real EditorDraftStore (without the Windows encryption) in a temporary folder.
// The same code is run in the editor against real servers by tests/RemoteFileEditorIntegrationTests (the drafts scenario).
// Run with: dotnet run --project tests/RemoteFileDraftsTests
internal static class Program
{
    private static int _passed;

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static void Equal<T>(T actual, T expected, string name)
    {
        bool same = EqualityComparer<T>.Default.Equals(actual, expected);
        Check(same, name + (same ? "" : " (expected '" + expected + "', got '" + actual + "')"));
    }

    private static bool Wait(Func<bool> condition)
    {
        for (int i = 0; i < 100; i++)
        {
            if (condition())
            {
                return true;
            }
            Thread.Sleep(50);
        }
        return condition();
    }

    private static EditorDraft Draft(string text, string baseHash = "h", bool isNewFile = false)
    {
        return new EditorDraft { RemotePath = "/etc/x", SavedUtc = DateTime.UtcNow, BaseHash = baseHash, IsNewFile = isNewFile, Text = text };
    }

    private static string NewFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "smartty-drafts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void Keys()
    {
        string folder = NewFolder();
        DraftKeyClaims claims = new DraftKeyClaims(folder);
        Equal(claims.Claim("host-abc"), "host-abc", "the first editor of a file has the plain key");
        Check(File.Exists(Path.Combine(folder, "host-abc.lock")), "the key is held by a lock file in the folder of the drafts");
        Equal(claims.Claim("host-abc"), "host-abc-2", "the second editor of the same file has the key with a number");
        Equal(claims.Claim("host-abc"), "host-abc-3", "the third one the next number");
        Equal(claims.Claim("other"), "other", "another file has its own plain key");
        claims.Release("host-abc");
        Check(!File.Exists(Path.Combine(folder, "host-abc.lock")), "the lock file goes away with the release");
        Equal(claims.Claim("host-abc"), "host-abc", "a released number is free again");
        claims.Release("never-claimed");
        Check(true, "releasing a key that is not claimed does nothing");
        claims.Release("host-abc");
        claims.Release("host-abc-2");
        claims.Release("host-abc-3");
        claims.Release("other");
        Check(Directory.GetFiles(folder, "*.lock").Length == 0, "no lock file is left");

        // A folder that cannot be used (a file is in its place) is no reason not to edit; the editors of this program are still told apart.
        string blocked = Path.Combine(folder, "blocked");
        File.WriteAllText(blocked, "x");
        DraftKeyClaims unusable = new DraftKeyClaims(blocked);
        Equal(unusable.Claim("k"), "k", "an unusable folder: the first key");
        Equal(unusable.Claim("k"), "k-2", "an unusable folder: the editors of this program are told apart");
        unusable.Release("k");
        unusable.Release("k-2");
        Directory.Delete(folder, true);
    }

    private static void Restoring()
    {
        string folder = NewFolder();
        EditorDraftStore store = new EditorDraftStore(folder);
        RemoteFileDrafts drafts = new RemoteFileDrafts(store, "k");
        drafts.BaseHash = "h";
        Equal(drafts.FindUnsaved(null, "text"), null, "no draft: nothing to restore");

        drafts.Queue(Draft("changed"));
        Check(Wait(() => store.TryLoad("k") != null), "a draft is written");
        EditorDraft found = drafts.FindUnsaved(null, "text");
        Check(found != null && found.Text == "changed", "a draft that differs from the file is offered");
        Check(store.TryLoad("k") != null, "offering it does not remove it");

        drafts.Queue(Draft("text"));
        Check(Wait(() => store.TryLoad("k")?.Text == "text"), "the draft is replaced");
        Equal(drafts.FindUnsaved(null, "text"), null, "a draft that is what the file holds is not offered");
        Check(Wait(() => store.TryLoad("k") == null), "and it is removed");

        drafts.Queue(new EditorDraft { RemotePath = "/etc/x", SavedUtc = DateTime.UtcNow, BaseHash = "h", Bytes = new byte[] { 1, 2, 3 } });
        Check(Wait(() => store.TryLoad("k") != null), "a draft of the hex view is written");
        Check(drafts.FindUnsaved(new byte[] { 1, 2, 4 }, "x") != null, "a hex draft that differs from the bytes of the file is offered");
        Equal(drafts.FindUnsaved(new byte[] { 1, 2, 3 }, "x"), null, "a hex draft of the bytes of the file is not");
        Check(Wait(() => store.TryLoad("k") == null), "and it is removed too");

        drafts.Queue(new EditorDraft { RemotePath = "/etc/x", SavedUtc = DateTime.UtcNow, BaseHash = "h", Bytes = Array.Empty<byte>() });
        Check(Wait(() => store.TryLoad("k") != null), "a hex draft of no bytes is written");
        Equal(drafts.FindUnsaved(null, "x"), null, "a hex draft of no bytes equals a file that is not there");
        Check(Wait(() => store.TryLoad("k") == null), "and it is removed");

        EditorDraft old = Draft("t", baseHash: "h");
        Check(!drafts.ServerChangedSince(old), "the file has the hash the draft was made of: not changed");
        drafts.BaseHash = "other";
        Check(drafts.ServerChangedSince(old), "another hash: the file has changed since");
        Check(!drafts.ServerChangedSince(Draft("t", baseHash: "h", isNewFile: true)), "a draft of a new file has no hash to compare");
        Directory.Delete(folder, true);
    }

    private static void LatestWins()
    {
        string folder = NewFolder();
        EditorDraftStore store = new EditorDraftStore(folder);
        RemoteFileDrafts drafts = new RemoteFileDrafts(store, "k");
        bool allGone = true;
        for (int i = 0; i < 30; i++)
        {
            drafts.Queue(Draft("text " + i));
            drafts.Discard();
        }
        Thread.Sleep(400);
        allGone &= store.TryLoad("k") == null;
        Check(allGone, "a removal asked for after writes wins: no draft is left, however the pool ran them");

        for (int i = 0; i < 30; i++)
        {
            drafts.Queue(null);
            drafts.Queue(Draft("text " + i));
        }
        Check(Wait(() => store.TryLoad("k")?.Text == "text 29"), "a write asked for after removals wins and holds the last text");
        Directory.Delete(folder, true);
    }

    private static void Capturing()
    {
        string folder = NewFolder();
        EditorDraftStore store = new EditorDraftStore(folder);
        RemoteFileDrafts drafts = new RemoteFileDrafts(store, "k");
        object v1 = new object();
        object v2 = new object();
        Check(drafts.ShouldCapture(v1, null), "a changed small text is taken");
        Check(!drafts.ShouldCapture(v1, null), "the same version is not taken twice");
        Check(drafts.ShouldCapture(v2, null), "a newer version is taken");
        drafts.Discard();
        Check(drafts.ShouldCapture(v2, null), "after a discard the version counts as not written");

        drafts.Captured(9L * 1024 * 1024);
        Equal(drafts.SizeEstimate, 9L * 1024 * 1024, "what was read is the new size estimate");
        Check(!drafts.ShouldCapture(new object(), null), "a large text is not taken right after the previous draft");
        drafts.SizeEstimate = 10;
        Check(drafts.ShouldCapture(new object(), null), "a small text is taken again");
        RemoteFileDrafts hex = new RemoteFileDrafts(store, "h");
        hex.Captured(1);
        hex.SizeEstimate = 10;
        Check(!hex.ShouldCapture(new object(), 100L * 1024 * 1024), "a large hex view is not taken right after the previous draft (its own size counts, not the text estimate)");
        Directory.Delete(folder, true);
    }

    private static void Cleanup()
    {
        string folder = NewFolder();
        EditorDraftStore store = new EditorDraftStore(folder);
        store.Save("old", Draft("old"));
        store.Save("new", Draft("new"));
        File.SetLastWriteTimeUtc(Path.Combine(folder, "old.draft"), DateTime.UtcNow - TimeSpan.FromDays(40));
        Equal(RemoteFileDrafts.TakeExpiryNotice(), null, "no notice before the cleanup ran");
        RemoteFileDrafts.StartCleanupOnce(store);
        Check(Wait(() => !File.Exists(Path.Combine(folder, "old.draft"))), "the cleanup removes a draft that is older than the limit");
        Check(File.Exists(Path.Combine(folder, "new.draft")), "and keeps the others");
        Equal(RemoteFileDrafts.MaxAge, TimeSpan.FromDays(30), "the limit is 30 days");

        // Once per run: a second call does nothing.
        store.Save("old2", Draft("old2"));
        File.SetLastWriteTimeUtc(Path.Combine(folder, "old2.draft"), DateTime.UtcNow - TimeSpan.FromDays(40));
        RemoteFileDrafts.StartCleanupOnce(store);
        Thread.Sleep(300);
        Check(File.Exists(Path.Combine(folder, "old2.draft")), "the cleanup runs once per run of the program");
        Directory.Delete(folder, true);
    }

    private static int Main()
    {
        try
        {
            Keys();
            Restoring();
            LatestWins();
            Capturing();
            Cleanup();
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " remote file draft checks passed.");
        return 0;
    }
}
