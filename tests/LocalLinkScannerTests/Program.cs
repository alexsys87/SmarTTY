using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SmarTTY.Terminal;
using SmarTTY.Terminal.Emulation;

// Checks of the scanner of the links in the text of a device terminal (Terminal/LocalLinkScanner.cs): which words become links, what a link
// covers and opens, when a link is live, how the lines of the screen and the pieces of the text are put together, and what the scanner asks of
// the terminal.
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

    private sealed class Host : ILocalLinkHost
    {
        public readonly List<string> Asked = new List<string>();

        public readonly List<string> Opened = new List<string>();

        public bool Exists = true;

        public bool PathExists(string path)
        {
            Asked.Add(path);
            return Exists;
        }

        public void RequestOpenFile(string path)
        {
            Opened.Add(path);
        }
    }

    // The same text, the same way as the control gives it: pieces, each "column|text" or just the text.
    private static string Scan(Host host, bool finish, params string[] pieces)
    {
        LocalLinkScanner scanner = new LocalLinkScanner(host);
        StringBuilder text = new StringBuilder();
        List<LinkCell> cells = new List<LinkCell>();
        foreach (string piece in pieces)
        {
            int bar = piece.IndexOf('|');
            if (bar > 0 && int.TryParse(piece.Substring(0, bar), out int column))
            {
                string chunk = piece.Substring(bar + 1);
                text.Append(chunk);
                cells.AddRange(scanner.Scan(chunk, column));
            }
            else
            {
                text.Append(piece);
                cells.AddRange(scanner.Scan(piece));
            }
        }
        if (finish)
        {
            scanner.Finish();
        }
        Equal(cells.Count, text.Length, "a cell for every character");
        List<ILinkTarget> targets = new List<ILinkTarget>();
        foreach (LinkCell cell in cells)
        {
            if (cell.Target != null && !targets.Contains(cell.Target))
            {
                targets.Add(cell.Target);
            }
        }
        List<string> result = new List<string>();
        foreach (ILinkTarget target in targets)
        {
            StringBuilder covered = new StringBuilder();
            for (int i = 0; i < cells.Count; i++)
            {
                if (ReferenceEquals(cells[i].Target, target))
                {
                    covered.Append(text[i]);
                }
            }
            result.Add((target.IsActive ? "+" : "-") + covered);
            target.Activate();
        }
        return string.Join(" ", result) + " => " + string.Join(" ", host.Opened);
    }

    private static void ScanChecks()
    {
        Equal(Scan(new Host(), true, "see https://example.com/a. now"), "+https://example.com/a. -now => https://example.com/a", "an address in a sentence covers its full stop but opens without it");
        Equal(Scan(new Host(), true, "go https://exa", "mple.com/x?y=1&z=2 ok"), "+https://example.com/x?y=1&z=2 -ok => https://example.com/x?y=1&z=2", "an address that comes in two pieces is one link, with its query");
        Equal(Scan(new Host(), false, "at https://example.com/z"), "-https://example.com/z => https://example.com/z", "an address at the end of the text is not live until the text is finished (but opens)");
        Equal(Scan(new Host(), true, "at https://example.com/z"), "+https://example.com/z => https://example.com/z", "and is live when it is finished");
        Equal(Scan(new Host(), true, "/tmp/x build.log ./run.sh"), "-./run.sh => ", "a Unix path and a bare name are no links (the last word is left undecided)");
        Equal(Scan(new Host(), true, "0|https://example.com/long", "0|tail"), "+https://example.com/long -tail => https://example.com/long", "an address does not go on in the next line");
        Equal(Scan(new Host(), true, "0|https://example.com/long", "-1|tail"), "+https://example.com/longtail => https://example.com/longtail", "but it goes on in the row of a wrapped line");
        Equal(Scan(new Host(), true, "0|https://example.com/long", "7|tail"), "+https://example.com/longtail => https://example.com/longtail", "and where the text starts in a column other than the first");
        Equal(Scan(new Host(), true, "https://a.org/1 and ftp://b.org/2"), "+https://a.org/1 +ftp://b.org/2 => https://a.org/1 ftp://b.org/2", "two addresses are two links");
        Equal(Scan(new Host(), true, "<a href=\"https://a.org/p\">x"), "+https://a.org/p -x => https://a.org/p", "an address ends at a quote");
        Equal(Scan(new Host(), true, "http://h.org:8080/p?q=1#top end"), "+http://h.org:8080/p?q=1#top -end => http://h.org:8080/p?q=1#top", "a port, a query and an anchor stay in the address");
        Equal(Scan(new Host(), true, "see (https://a.org/p) now"), "+https://a.org/p) -now => https://a.org/p", "the bracket after an address is cut off when it opens");
        string filler = CellWidth.Filler.ToString();
        Equal(Scan(new Host(), true, "see https://a.org/\u65e5" + filler + "\u672c" + filler + " now").Replace(filler, "_"), "+https://a.org/\u65e5_\u672c_ -now => https://a.org/\u65e5\u672c",
            "an address with wide characters covers both cells of each and opens without the fillers");
        Equal(Scan(new Host(), true, ""), " => ", "empty text");
        Equal(Scan(new Host(), true, "a\\ b.txt"), "-b.txt => ", "a word after a backslash and a blank is a word of its own");
        Equal(Scan(new Host(), true, "see C:\\dir\\a.txt ok"), "-C:\\dir\\a.txt -ok => ", "a path with a drive letter is a candidate (it opens only on a system where the full path is of this computer)");
    }

    private static void HostChecks()
    {
        Host host = new Host();
        Scan(host, true, "an address https://example.com/a and more");
        Equal(host.Asked.Count, 0, "the terminal is never asked whether an address exists");
        host = new Host { Exists = false };
        Scan(host, true, "see https://example.com/a");
        Equal(host.Opened.Count, 1, "an address opens whatever the terminal knows of paths");
    }

    private static void CandidateChecks()
    {
        Host host = new Host();
        LocalLinkCandidate word = new LocalLinkCandidate(host, 'b');
        Equal(word.State, LinkCandidateState.Word, "a candidate starts as a word");
        Check(!word.IsActive, "which is not live");
        Check(word.Accept('u'), "it takes a character of a path");
        Check(word.Accept('i'), "and the next");
        Check(!word.Accept(' '), "a blank ends it");
        Equal(word.State, LinkCandidateState.Rejected, "a bare name is rejected");
        Check(!word.Accept('x'), "a rejected one takes nothing");
        word.Complete();
        Equal(word.State, LinkCandidateState.Rejected, "and stays rejected");

        LocalLinkCandidate drive = new LocalLinkCandidate(host, 'C');
        foreach (char ch in ":\\dir\\a.txt")
        {
            Check(drive.Accept(ch), "a drive path takes " + ch);
        }
        Check(!drive.Accept(','), "a comma ends it");
        Equal(drive.State, LinkCandidateState.Position, "a path with a drive letter becomes a link candidate");
        Check(!drive.Accept('x'), "which takes nothing more");

        LocalLinkCandidate url = new LocalLinkCandidate(host, 'h');
        foreach (char ch in "ttps://a.org/p?x=1&y=2#t")
        {
            Check(url.Accept(ch), "an address takes " + ch);
        }
        Check(url.Accept('/'), "and the next one");
        Check(!url.Accept('"'), "a quote ends it");
        Equal(url.State, LinkCandidateState.Position, "the address is a link candidate");
        Check(url.IsActive, "which is live");
        url.Activate();
        Equal(host.Opened[0], "https://a.org/p?x=1&y=2#t/", "it opens the address");
        Equal(host.Asked.Count, 0, "without asking the terminal about paths");

        host = new Host();
        LocalLinkCandidate longUrl = new LocalLinkCandidate(host, 'h');
        foreach (char ch in "ttps://a.org/")
        {
            longUrl.Accept(ch);
        }
        for (int i = 0; i < 9000; i++)
        {
            longUrl.Accept('x');
        }
        longUrl.Complete();
        longUrl.Activate();
        Equal(host.Opened[0].Length, 8192, "an address is followed up to 8192 characters");

        LocalLinkCandidate scheme = new LocalLinkCandidate(host, 'f');
        foreach (char ch in "tp://")
        {
            scheme.Accept(ch);
        }
        foreach (char ch in " ")
        {
            Check(!scheme.Accept(ch), "a blank ends an address");
        }
        Equal(scheme.State, LinkCandidateState.Position, "ftp:// is an address");
        LocalLinkCandidate file = new LocalLinkCandidate(host, 'f');
        foreach (char ch in "ile://")
        {
            file.Accept(ch);
        }
        file.Complete();
        Equal(file.State, LinkCandidateState.Rejected, "file:// is not one that opens");
        if (OperatingSystem.IsWindows())
        {
            // Only where a path with a drive letter is a path of this computer: the terminal is asked whether it exists, and its answer is the answer.
            host = new Host { Exists = true };
            LocalLinkCandidate real = new LocalLinkCandidate(host, 'C');
            foreach (char ch in ":\\Windows\\a.txt ")
            {
                real.Accept(ch);
            }
            Check(real.IsActive, "a path of this computer that exists is live");
            host.Exists = false;
            Check(!real.IsActive, "and one that does not exist is not");
            Check(host.Asked.Count == 2, "the terminal is asked each time");
            real.Activate();
            Equal(host.Opened.Count, 1, "it opens the full path");
        }
        Equal(LocalLinkCandidate.PathCharacters.Length, 26 + 26 + 10 + 6, "the characters of a path (letters, digits and _ . - / : \\)");
    }

    private static void Main()
    {
        ScanChecks();
        HostChecks();
        CandidateChecks();
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " local link scanner checks passed.");
    }
}
