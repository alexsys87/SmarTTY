using SmarTTY.Terminal;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Linq;
using VisualTTY.DeviceTerminal;

// Checks of the local completion session of the serial terminal (Terminal/LocalCompletionSession.cs): what it offers for a command, a path and a directory, how it follows
// the edits of the word, what it applies, and how it deals with the listings of the device (answers that come late, no shell to ask). The terminal and the popup are fakes.
internal static class Program
{
    private sealed class FakePopup : ICompletionPopup
    {
        public bool IsOpen { get; private set; } = true;
        public List<string> Items = new List<string>();
        public string Filter;
        public int Exceptions;
        public int ItemListCalls;

        public event Action<CompletionItem, VirtualKey> ItemSelected;

        public void Close() => IsOpen = false;

        public void SetFilter(string text) => Filter = text;

        public bool TryHandleKeyEvent(KeyStroke e) => false;

        public void DisplayException(Exception exception) => Exceptions++;

        public void SetItemList(CompletionItem[] items)
        {
            Items = items.Select((CompletionItem item) => item.Text).ToList();
            ItemListCalls++;
        }

        public CompletionItem GetSelectedItem(bool onlyIfPrefixMatch) => default;

        public void Select(CompletionItem item, VirtualKey key) => ItemSelected?.Invoke(item, key);
    }

    private sealed class FakeHost : ILocalCompletionHost
    {
        public List<CompletionItem> Suggested = new List<CompletionItem>();
        public bool SuggestedThrows;
        public readonly Queue<Action> Posted = new Queue<Action>();
        public DeviceDirectoryListerHandler Lister;
        public bool CanList = true;
        public readonly List<string> ListedDirectories = new List<string>();
        public readonly List<Action<IReadOnlyList<DeviceDirectoryEntry>>> Answers = new List<Action<IReadOnlyList<DeviceDirectoryEntry>>>();

        public FakeHost()
        {
            Lister = (directory, done) =>
            {
                ListedDirectories.Add(directory);
                Answers.Add(done);
                return true;
            };
        }

        IEnumerable<CompletionItem> ILocalCompletionHost.GetSuggestedCommandItems()
        {
            if (SuggestedThrows)
            {
                throw new InvalidOperationException("no list");
            }
            return Suggested;
        }

        void ILocalCompletionHost.Post(Action action) => Posted.Enqueue(action);

        DeviceDirectoryListerHandler ILocalCompletionHost.DeviceDirectoryLister => Lister;

        bool ILocalCompletionHost.CanListDeviceDirectory => CanList;

        public void RunPosted()
        {
            while (Posted.Count > 0)
            {
                Posted.Dequeue()();
            }
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        Console.WriteLine("PASS: " + name);
    }

    private static void Equal<T>(T actual, T expected, string name)
    {
        bool same = EqualityComparer<T>.Default.Equals(actual, expected);
        Check(same, name + (same ? "" : " (expected '" + expected + "', got '" + actual + "')"));
    }

    private static CompletionItem Item(string text) => new CompletionItem { Text = text, InsertText = text };

    private static DeviceDirectoryEntry[] Entries(params string[] names)
    {
        return names.Select((string name) => new DeviceDirectoryEntry(name.TrimEnd('/'), name.EndsWith("/", StringComparison.Ordinal))).ToArray();
    }

    private static List<CompletionSession.AppliedEventArgs> Applied(LocalCompletionSession session)
    {
        List<CompletionSession.AppliedEventArgs> list = new List<CompletionSession.AppliedEventArgs>();
        session.ItemApplied += (sender, args) => list.Add(args);
        return list;
    }

    private static string Join(IEnumerable<string> items) => string.Join("|", items);

    private static void CommandChecks()
    {
        FakeHost host = new FakeHost { Suggested = new List<CompletionItem> { Item("help me"), Item("other") } };
        FakePopup popup = new FakePopup();
        LocalCompletionSession session = new LocalCompletionSession(host, "", CompletionTarget.Command, 0, 0, popup, false);
        Check(popup.Items.Count > 2 && popup.Items[0] == "help me" && popup.Items[1] == "other", "for an empty word the saved commands come first");
        Check(popup.Items.Contains("help") && popup.Items.Contains("settings"), "then the built-in commands (and the programs of this computer)");
        Equal(popup.Filter, "", "the filter is the empty word");
        Equal(host.Posted.Count, 1, "the list is shown again from the thread of the window");
        Equal(host.ListedDirectories.Count, 0, "the device is not asked for a command");

        host = new FakeHost { Suggested = new List<CompletionItem> { Item("help me"), Item("other"), Item("Hello") } };
        popup = new FakePopup();
        session = new LocalCompletionSession(host, "he", CompletionTarget.Command, 0, 2, popup, false);
        Check(popup.Items.Count >= 3 && popup.Items[0] == "help me" && popup.Items[1] == "Hello", "for a word that has letters the saved commands that start with it come first, whatever the case");
        Check(popup.Items.Contains("help") && !popup.Items.Contains("other") && !popup.Items.Contains("settings"), "then the built-in commands that start with it (and the programs of this computer that do), and nothing else");
        Check(popup.Items.All((string item) => item.StartsWith("he", StringComparison.OrdinalIgnoreCase)), "everything offered starts with the typed letters");
        Equal(popup.Filter, "he", "with the word as the filter");

        // A quoted word is matched without its quotes.
        host = new FakeHost { Suggested = new List<CompletionItem> { Item("help me") } };
        popup = new FakePopup();
        session = new LocalCompletionSession(host, "'he", CompletionTarget.Command, 0, 3, popup, false);
        Check(popup.Items.Count >= 2 && popup.Items[0] == "help me" && popup.Items.Contains("help"), "a quote in front of the word is not part of what is matched (a saved command and a built-in one start with it)");
        Equal(popup.Filter, "he", "nor of the filter");

        // Auto-apply: a single match is applied when the list is shown from the window thread.
        host = new FakeHost { Suggested = new List<CompletionItem> { new CompletionItem { Text = "zqboot now", InsertText = "zqboot now" } } };
        popup = new FakePopup();
        session = new LocalCompletionSession(host, "zqb", CompletionTarget.Command, 0, 3, popup, true);
        List<CompletionSession.AppliedEventArgs> applied = Applied(session);
        host.RunPosted();
        Check(applied.Count == 1 && applied[0].NewText == "zqboot now" && applied[0].CaretIndex == 10 && !applied[0].Commit && !applied[0].IsDirectory, "a single match is applied without committing, the caret after it");

        // The start of a built-in command is completed (Tab used to offer nothing for a word that has letters in it).
        host = new FakeHost();
        popup = new FakePopup();
        session = new LocalCompletionSession(host, "sendh", CompletionTarget.Command, 0, 5, popup, true);
        applied = Applied(session);
        Equal(Join(popup.Items), "sendhex", "the start of a built-in command offers it");
        host.RunPosted();
        Check(applied.Count == 1 && applied[0].NewText == "sendhex" && applied[0].CaretIndex == 7, "and the only match is applied");

        // The list cannot be made.
        host = new FakeHost { SuggestedThrows = true };
        popup = new FakePopup();
        session = new LocalCompletionSession(host, "", CompletionTarget.Command, 0, 0, popup, false);
        Equal(popup.Exceptions, 1, "a list that cannot be made is shown to the user as an exception of the popup");
    }

    private static void TextChangeChecks()
    {
        FakeHost host = new FakeHost { Suggested = new List<CompletionItem> { Item("zqboot"), Item("zqname"), Item("ls") } };
        FakePopup popup = new FakePopup();
        LocalCompletionSession session = new LocalCompletionSession(host, "zq", CompletionTarget.Command, 0, 2, popup, false);
        Equal(Join(popup.Items), "zqboot|zqname", "the words that start with the typed letters");
        Equal(session.OnTextChanged("zqb", 2, 0, 1), false, "a letter typed at the end of the word goes on");
        Equal(Join(popup.Items), "zqboot", "the list follows it");
        Equal(popup.Filter, "zqb", "and so does the filter");
        Equal(session.OnTextChanged("zq", 2, 1, 0), false, "a letter taken out goes on");
        Equal(Join(popup.Items), "zqboot|zqname", "the list is wider again");
        Equal(session.OnTextChanged("zq x", 3, 0, 1), true, "an edit after the end of the word ends the session");
        Equal(session.OnTextChanged("z", 1, 3, 0), true, "an edit that starts inside the word and reaches past its end ends the session");
        Equal(session.OnTextChanged("zq", 0, 0, 0), false, "an edit at the start of the word is inside it");
        Equal(session.OnTextChanged("xzq", 0, 0, 1) , false, "a letter put in front of the word is inside it");
        Equal(popup.Filter, "xzq", "the word is longer");

        host = new FakeHost();
        popup = new FakePopup();
        session = new LocalCompletionSession(host, "a b", CompletionTarget.Command, 2, 3, popup, false);
        Equal(session.OnTextChanged("a ", 0, 1, 0), true, "an edit that starts before the word ends the session");
        session = new LocalCompletionSession(host, "a b", CompletionTarget.Command, 2, 3, popup, false);
        Equal(session.OnTextChanged("a ", 2, 1, 0), false, "taking out the whole word leaves an empty word, which the session keeps");
        Equal(popup.Filter, "", "and the filter is empty");

        // Characters that end a word, and a blank in it.
        host = new FakeHost();
        popup = new FakePopup();
        session = new LocalCompletionSession(host, "a", CompletionTarget.Command, 0, 1, popup, false);
        Equal(session.OnTextChanged("a;", 1, 0, 1), true, "a character of the shell syntax ends the session");
        session = new LocalCompletionSession(host, "a", CompletionTarget.Command, 0, 1, popup, false);
        Equal(session.OnTextChanged("a b", 1, 0, 2), true, "a blank in the word that is not escaped ends it");
        session = new LocalCompletionSession(host, "a", CompletionTarget.Command, 0, 1, popup, false);
        Equal(session.OnTextChanged("a\\ b", 1, 0, 3), false, "an escaped blank does not");
        session = new LocalCompletionSession(host, "'a", CompletionTarget.Command, 0, 2, popup, false);
        Equal(session.OnTextChanged("'a b", 2, 0, 2), false, "and a blank in a quoted word does not");
    }

    private static void PathChecks()
    {
        FakeHost host = new FakeHost();
        FakePopup popup = new FakePopup();
        LocalCompletionSession session = new LocalCompletionSession(host, "cat /etc/h", CompletionTarget.Path, 4, 10, popup, true);
        List<CompletionSession.AppliedEventArgs> applied = Applied(session);
        Equal(Join(host.ListedDirectories), "/etc/", "the device is asked for the directory in the word, as typed");
        Equal(host.Posted.Count, 0, "the answer is waited for");
        host.Answers[0](Entries("init.d/", "hosts", "passwd"));
        Equal(Join(popup.Items), "hosts", "the files that start with the typed letters, from the answer");
        Equal(popup.Filter, "h", "with what follows the last slash as the filter");
        Check(applied.Count == 1 && applied[0].NewText == "cat /etc/hosts" && applied[0].CaretIndex == 14 && !applied[0].IsDirectory && !applied[0].Commit, "the only match is applied");

        // Directories first, escaped; a directory target offers directories only.
        host = new FakeHost();
        popup = new FakePopup();
        session = new LocalCompletionSession(host, "cd /d/", CompletionTarget.Directory, 3, 6, popup, false);
        host.Answers[0](Entries("my dir/", "b.txt", "a/"));
        Equal(Join(popup.Items), "a|my dir", "a directory target offers the directories, sorted");
        host = new FakeHost();
        popup = new FakePopup();
        session = new LocalCompletionSession(host, "cat /d/", CompletionTarget.Path, 4, 7, popup, false);
        host.Answers[0](Entries("my dir/", "b.txt", "a/", "a b"));
        Equal(Join(popup.Items), "a|my dir|a b|b.txt", "a path target offers directories first, then files, sorted by name");

        // Auto-apply of several matches: the common start, with the directory in front of it and escaped.
        host = new FakeHost();
        popup = new FakePopup();
        session = new LocalCompletionSession(host, "cat /cfg/c", CompletionTarget.Path, 4, 10, popup, true);
        applied = Applied(session);
        host.Answers[0](Entries("config.a", "config.b", "other"));
        Check(applied.Count == 1 && applied[0].NewText == "cat /cfg/config." && applied[0].CaretIndex == 16, "the common start of the names that match is applied");
        Equal(Join(popup.Items), "config.a|config.b", "and the names that match are offered");

        host = new FakeHost();
        popup = new FakePopup();
        session = new LocalCompletionSession(host, "cat /cfg/lib", CompletionTarget.Path, 4, 12, popup, true);
        applied = Applied(session);
        host.Answers[0](Entries("libfoo", "libbar"));
        Equal(applied.Count, 0, "nothing is applied when the common start is what is typed already");

        host = new FakeHost();
        popup = new FakePopup();
        session = new LocalCompletionSession(host, "cat /cfg/m", CompletionTarget.Path, 4, 10, popup, true);
        applied = Applied(session);
        host.Answers[0](Entries("my one", "my two"));
        Check(applied.Count == 1 && applied[0].NewText == "cat /cfg/my\\ ", "a common start with a blank is escaped, with the directory in front");

        host = new FakeHost();
        popup = new FakePopup();
        session = new LocalCompletionSession(host, "cat /cfg/x", CompletionTarget.Path, 4, 10, popup, true);
        applied = Applied(session);
        host.Answers[0](Entries("config.a"));
        Check(applied.Count == 0 && popup.Items.Count == 0, "no match: nothing is offered and nothing applied");

        host = new FakeHost();
        popup = new FakePopup();
        session = new LocalCompletionSession(host, "cat /cfg/c", CompletionTarget.Path, 4, 10, popup, false);
        applied = Applied(session);
        host.Answers[0](Entries("config.a"));
        Check(applied.Count == 0 && Join(popup.Items) == "config.a", "without auto-apply the list is only shown");

        // A listing that finds nothing, or that is answered with null.
        host = new FakeHost();
        popup = new FakePopup();
        session = new LocalCompletionSession(host, "cat /cfg/", CompletionTarget.Path, 4, 9, popup, true);
        host.Answers[0](null);
        Equal(popup.Items.Count, 0, "an answer of null (the device did not answer) offers nothing");
    }

    private static void ListingChecks()
    {
        // No lister, a lister that cannot start, a shell that cannot be asked: nothing is offered, not the files of this computer.
        foreach (int variant in new[] { 0, 1, 2 })
        {
            FakeHost host = new FakeHost();
            if (variant == 0) host.Lister = null;
            if (variant == 1) host.Lister = (directory, done) => false;
            if (variant == 2) host.CanList = false;
            FakePopup popup = new FakePopup();
            LocalCompletionSession session = new LocalCompletionSession(host, "cat /etc/", CompletionTarget.Path, 4, 9, popup, true);
            List<CompletionSession.AppliedEventArgs> applied = Applied(session);
            Check(popup.Items.Count == 0 && host.Posted.Count == 1, "no listing started (" + variant + "): an empty list now, and the popup is updated from the window thread");
            host.RunPosted();
            Check(applied.Count == 0 && host.ListedDirectories.Count == 0, "and nothing is applied (" + variant + ")");
        }

        // A word that moved to another directory: the answer for the old one is dropped.
        FakeHost host2 = new FakeHost();
        FakePopup popup2 = new FakePopup();
        LocalCompletionSession session2 = new LocalCompletionSession(host2, "cat /a/", CompletionTarget.Path, 4, 7, popup2, false);
        Equal(session2.OnTextChanged("cat /a/b/", 7, 0, 2), false, "the word moves into another directory");
        Equal(Join(host2.ListedDirectories), "/a/|/a/b/", "the device is asked for the new one");
        host2.Answers[0](Entries("old.txt"));
        Equal(popup2.Items.Count, 0, "the answer for the old directory is dropped");
        host2.Answers[1](Entries("new.txt"));
        Equal(Join(popup2.Items), "new.txt", "the answer for the new one is used");

        // Typing in the same directory does not ask again.
        host2 = new FakeHost();
        popup2 = new FakePopup();
        session2 = new LocalCompletionSession(host2, "cat /a/", CompletionTarget.Path, 4, 7, popup2, false);
        host2.Answers[0](Entries("new.txt", "next.txt"));
        Equal(session2.OnTextChanged("cat /a/n", 7, 0, 1), false, "a letter in the same directory goes on");
        Equal(host2.ListedDirectories.Count, 1, "without a new listing");
        Equal(Join(popup2.Items), "new.txt|next.txt", "the list is filtered");
        session2.OnTextChanged("cat /a/ne", 8, 0, 1);
        Equal(Join(popup2.Items), "new.txt|next.txt", "and filtered again");
        session2.OnTextChanged("cat /a/nex", 9, 0, 1);
        Equal(Join(popup2.Items), "next.txt", "down to what matches");

        // Disposed: late answers are dropped and the popup closed.
        host2 = new FakeHost();
        popup2 = new FakePopup();
        session2 = new LocalCompletionSession(host2, "cat /a/", CompletionTarget.Path, 4, 7, popup2, true);
        List<CompletionSession.AppliedEventArgs> applied2 = Applied(session2);
        session2.Dispose();
        Check(!popup2.IsOpen, "disposing closes the popup");
        host2.Answers[0](Entries("x.txt"));
        Check(popup2.Items.Count == 0 && applied2.Count == 0, "an answer that comes after is dropped");

        // A word that moved while no shell could be asked: the empty list is not shown over a newer one.
        host2 = new FakeHost { CanList = false };
        popup2 = new FakePopup();
        session2 = new LocalCompletionSession(host2, "cat /a/", CompletionTarget.Path, 4, 7, popup2, false);
        host2.CanList = true;
        session2.OnTextChanged("cat /a/b/", 7, 0, 2);
        int before = popup2.ItemListCalls;
        host2.RunPosted();
        Equal(popup2.ItemListCalls, before, "the late update of an older request does nothing");
    }

    private static void ApplyChecks()
    {
        FakeHost host = new FakeHost();
        FakePopup popup = new FakePopup();
        LocalCompletionSession session = new LocalCompletionSession(host, "cd /a/b", CompletionTarget.Directory, 3, 7, popup, false);
        List<CompletionSession.AppliedEventArgs> applied = Applied(session);
        CompletionItem dir = new CompletionItem { Text = "b", InsertText = "/a/b/" };
        popup.Select(dir, VirtualKey.Tab);
        Check(applied.Count == 1 && applied[0].NewText == "cd /a/b/" && applied[0].CaretIndex == 8 && applied[0].IsDirectory && !applied[0].Commit, "a directory chosen with another key than Enter keeps its slash and is not committed");
        popup.Select(dir, VirtualKey.Return);
        Check(applied[1].NewText == "cd /a/b" && applied[1].CaretIndex == 7 && applied[1].IsDirectory && applied[1].Commit, "with Enter it is committed, without the slash");
        CompletionItem file = new CompletionItem { Text = "c.txt", InsertText = "/a/c.txt" };
        popup.Select(file, VirtualKey.Return);
        Check(applied[2].NewText == "cd /a/c.txt" && applied[2].Commit && !applied[2].IsDirectory, "a file is put in as it is");
        popup.Select(new CompletionItem { Text = "x" }, VirtualKey.Return);
        Equal(applied.Count, 3, "an item with nothing to insert is ignored");

        // The text after the word is kept.
        host = new FakeHost();
        popup = new FakePopup();
        session = new LocalCompletionSession(host, "cat ab cd", CompletionTarget.Path, 4, 6, popup, false);
        applied = Applied(session);
        popup.Select(new CompletionItem { Text = "abc", InsertText = "abc" }, VirtualKey.Tab);
        Check(applied[0].NewText == "cat abc cd" && applied[0].CaretIndex == 7, "what follows the word stays, the caret is after the inserted text");
    }

    private static int Main()
    {
        try
        {
            CommandChecks();
            TextChangeChecks();
            PathChecks();
            ListingChecks();
            ApplyChecks();
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            return 1;
        }
        return 0;
    }
}

// The namespaces of the code the logic folders use: an empty one would make "using" fail before the real missing file is named.
namespace SmarTTY.IO
{
    internal static class NamespaceSeed
    {
    }
}

namespace SmarTTY.Ssh
{
    internal static class NamespaceSeed
    {
    }
}
