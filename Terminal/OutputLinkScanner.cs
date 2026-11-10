using SmarTTY.CodeEditing;
using SmarTTY.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SmarTTY.Terminal;

/// <summary>What the scanner of the output of a command needs from the terminal.</summary>
internal interface IOutputLinkHost
{
    /// <summary>The text after the last dot of a file name, dot included (".c"), is a kind of file that gets a link.</summary>
    bool IsLinkExtension(string extension);

    /// <summary>
    /// The link was clicked: open <paramref name="path"/> at the line and column (counted from 0, in <paramref name="unit"/>), when
    /// they are known.
    /// </summary>
    void RequestOpenFile(string path, int? line, int? column, ColumnUnit unit);

    /// <summary>An address was clicked (http, https or ftp, already trimmed of the punctuation after it): open it in the browser.</summary>
    void RequestOpenUrl(string url);
}

/// <summary>
/// Finds the file names in the output of a command that is run in a Smart Terminal tab, "main.c", "src/util.h:12:5:",
/// "/etc/hosts", and makes them links that open the file (at the line and the column that follow the name) in the editor.
/// The text comes in pieces, as the terminal receives it, so the scanner keeps its state from one piece to the next.
///
/// Names are relative to the directory the build tool works in, which is what <see cref="BuildDirectories"/> works out from
/// the command line and from the "Entering directory" lines of make and ninja.
///
/// It also marks the words that stand for an error, a warning or a note (<see cref="DiagnosticHighlighter"/>) in the
/// cells it returns, for the terminal to colour where the compiler left the text plain.
///
/// The class is free of any UI dependency, so the test harness can check it without a display.
/// </summary>
internal sealed class OutputLinkScanner : TerminalEmulatorBase.IColumnAwareLinkScanner
{
    public const string PathCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_.-/";

    // Files that are known by their name alone: "Makefile:12: *** missing separator" has no extension to go by.
    private static readonly HashSet<string> WellKnownNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "Makefile", "makefile", "GNUmakefile", "Dockerfile", "Containerfile", "Kconfig", "Vagrantfile", "Jenkinsfile", "Rakefile", "Gemfile"
    };

    // The end of the output is kept to find a "Entering directory" message; a message is a line, far below this.
    private const int TailLimit = 600;

    // A line longer than this is not looked at for errors and warnings (a minified file, a binary dump).
    private const int MaxAnalyzedLine = 1000;

    private enum CandidateState
    {
        Word,
        Rejected,
        AbsolutePath,
        Position
    }

    private sealed class LinkCandidate : TerminalEmulatorBase.ILinkTarget
    {
        public CandidateState State;

        private readonly StringBuilder _path = new StringBuilder();

        private readonly IOutputLinkHost _host;

        private readonly string _directory;

        private readonly ColumnUnit _unit;

        // A name that starts with "/" is a link only when it is a file that is known by its extension or name, or when a position
        // follows it (see the constructor of the scanner).
        private readonly bool _strictAbsolute;

        private StringBuilder _extension;

        private int? _line;

        private int? _column;

        // The digits of the number being read, as a value (no string is built, so a very long run of digits costs nothing): -1 when
        // there is no number yet, and a number that does not fit an int is dropped as it always was.
        private long _number = -1;

        private bool _numberOverflow;

        private bool HasNumber => _number >= 0;

        // An absolute name of an unknown kind that is a link only for the position after it: "/etc/passwd:3" is a link, and the
        // directory in "bash: /tmp: Is a directory" is not.
        private bool _needsLine;

        public bool IsActive => State == CandidateState.Position;

        public string PathText => _path.ToString();

        /// <summary>Takes the link back: what looked like a file turned out to be something else (a directory in a message).</summary>
        public void Reject()
        {
            State = CandidateState.Rejected;
        }

        /// <summary>The name is no link, and the rest of it (up to a character that cannot be in a name) is not looked at either.</summary>
        public void Swallow()
        {
            State = CandidateState.Rejected;
            _swallow = true;
        }

        private bool _swallow;

        public LinkCandidate(IOutputLinkHost host, string directory, ColumnUnit unit, bool strictAbsolute, char firstChar)
        {
            _host = host;
            _directory = directory;
            _unit = unit;
            _strictAbsolute = strictAbsolute;
            _path.Append(firstChar);
            State = (firstChar == '/') ? CandidateState.AbsolutePath : CandidateState.Word;
        }

        public void Activate()
        {
            _host.RequestOpenFile(BuildDirectories.Resolve(_directory, _path.ToString()), _line, _column, _unit);
        }

        public override string ToString()
        {
            return State switch
            {
                CandidateState.Rejected => "-",
                CandidateState.Word => ">" + _path.ToString(),
                _ => _path.ToString(),
            };
        }

        private void ResetNumber()
        {
            _number = -1;
            _numberOverflow = false;
        }

        private bool ParsePositionChar(char ch)
        {
            if (ch >= '0' && ch <= '9')
            {
                if (!_numberOverflow)
                {
                    _number = Math.Max(_number, 0) * 10 + (ch - '0');
                    _numberOverflow = _number > int.MaxValue;
                }
                return true;
            }
            if (!HasNumber)
            {
                return false;
            }
            if (!_numberOverflow)
            {
                // Positions are counted from 1; a compiler that has no position prints 0 ("file.c:0:", "file.c:5:0"). That is the
                // first line or column, not -1: a negative number is what marks an invalid TextPosition, and one such part would
                // make the editor ignore the other part as well.
                int parsedNumber = (int)Math.Max(0, _number - 1);
                if (!_line.HasValue)
                {
                    _line = parsedNumber;
                }
                else
                {
                    _column = parsedNumber;
                }
            }
            ResetNumber();
            return true;
        }

        public bool Accept(char ch)
        {
            if (State == CandidateState.Rejected)
            {
                return _swallow && IsPathChar(ch);
            }
            if (State == CandidateState.Position)
            {
                if (ParsePositionChar(ch))
                {
                    return true;
                }
                if (_needsLine && !_line.HasValue)
                {
                    State = CandidateState.Rejected;
                }
                return false;
            }
            if (ch == '.')
            {
                _extension = new StringBuilder();
            }
            if (IsPathChar(ch))
            {
                _path.Append(ch);
                _extension?.Append(ch);
                return true;
            }
            Complete(ch == ':');
            if (ch == ':')
            {
                return true;
            }
            return false;
        }

        public void Complete(bool positionMayFollow = false)
        {
            if (State == CandidateState.Position)
            {
                // The name is complete and the position after it ends here (the end of the output, or the output was
                // interrupted): the link stays. It used to be dropped, so the last link of an output had no underline.
                if (HasNumber)
                {
                    ParsePositionChar('\0');
                    ResetNumber();
                }
                if (_needsLine && !_line.HasValue)
                {
                    State = CandidateState.Rejected;
                }
                return;
            }
            if (State == CandidateState.AbsolutePath)
            {
                if (IsKnownFile())
                {
                    State = CandidateState.Position;
                }
                else if (positionMayFollow)
                {
                    // "/var/log: Is a directory", "bash: /temp: No such file or directory": a message about a name is no
                    // reason to open it, a line number after the colon is.
                    State = CandidateState.Position;
                    _needsLine = true;
                }
                else if (!_strictAbsolute && LooksLikeFile())
                {
                    State = CandidateState.Position;
                }
                else
                {
                    State = CandidateState.Rejected;
                }
            }
            else if (State == CandidateState.Word && (IsKnownFile()))
            {
                State = CandidateState.Position;
            }
            else
            {
                State = CandidateState.Rejected;
            }
        }

        /// <summary>
        /// An absolute name of an unknown kind that may be a file ("/etc/hosts", "/var/log/syslog"). A name right under the root
        /// ("/tmp", "/temp") is a directory nearly always, and the "/24" of a network mask ("10.0.0.0 /24", "2001:db8::/32") is
        /// no name at all; a name that ends with "/" is a directory, and one whose last part has no letter ("/proc/1") is no
        /// file to edit either.
        /// </summary>
        private bool LooksLikeFile()
        {
            string path = _path.ToString();
            int lastSlash = path.LastIndexOf('/');
            if (lastSlash <= 0 || lastSlash == path.Length - 1)
            {
                return false;
            }
            for (int i = lastSlash + 1; i < path.Length; i++)
            {
                if (char.IsLetter(path[i]))
                {
                    return true;
                }
            }
            return false;
        }

        private bool IsKnownFile()
        {
            if (_extension != null && _host.IsLinkExtension(_extension.ToString()))
            {
                return true;
            }
            string path = _path.ToString();
            return WellKnownNames.Contains(path.Substring(path.LastIndexOf('/') + 1));
        }
    }

    /// <summary>
    /// Tells where an address in the output ("https://example.com/a.html") is, character by character. The address has "." and "/"
    /// in it, so its host and its path read as a relative file name, and the extension at its end is one that gets a link:
    /// a click opened "&lt;directory&gt;/xample.com/a.html" in the editor on the remote machine. What follows a scheme and "://" is
    /// no file name, up to a blank, a control character or a quote or bracket that does not belong to an address.
    /// </summary>
    private sealed class UrlTracker
    {
        // The characters of a scheme that have been read in a row (letters, digits, "+", "-", "."). A scheme has two or more of them:
        // a single letter in front of ":" is a drive ("C://dir"), not a scheme.
        private readonly StringBuilder _scheme = new StringBuilder();

        // The scheme in front of the ":" that was read last.
        private string _pendingScheme;

        // 0: nothing; 1: a scheme and ":" have been read; 2: and one "/" as well.
        private int _stage;

        public bool InUrl { get; private set; }

        /// <summary>True when the last character fed was the one that made an address of what came before it (the second "/").</summary>
        public bool JustStarted { get; private set; }

        /// <summary>The scheme of the current address ("https"), as it was written.</summary>
        public string Scheme { get; private set; }

        public void Reset()
        {
            _scheme.Clear();
            _pendingScheme = null;
            _stage = 0;
            InUrl = false;
            JustStarted = false;
            Scheme = null;
        }

        /// <summary>True when the character is part of an address (from the second "/" after the scheme on).</summary>
        public bool Feed(char ch)
        {
            JustStarted = false;
            if (InUrl)
            {
                if (EndsUrl(ch))
                {
                    Reset();
                    return false;
                }
                return true;
            }
            if (ch == ':' && _scheme.Length >= 2)
            {
                _stage = 1;
                _pendingScheme = _scheme.ToString();
                _scheme.Clear();
                return false;
            }
            if (ch == '/' && _stage == 1)
            {
                _stage = 2;
                return false;
            }
            if (ch == '/' && _stage == 2)
            {
                InUrl = true;
                JustStarted = true;
                Scheme = _pendingScheme;
                _stage = 0;
                return true;
            }
            _stage = 0;
            if (IsSchemeChar(ch))
            {
                // A run longer than any scheme keeps its last characters: "xxxx...https" still ends in a scheme.
                if (_scheme.Length >= 64)
                {
                    _scheme.Remove(0, 1);
                }
                _scheme.Append(ch);
            }
            else
            {
                _scheme.Clear();
            }
            return false;
        }

        // The characters that a scheme is made of (RFC 3986): letters, digits, "+", "-" and ".".
        private static bool IsSchemeChar(char ch)
        {
            return (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '+' || ch == '-' || ch == '.';
        }

        // Where an address ends: the same rule as for the addresses of the device terminals and of the editor.
        private static bool EndsUrl(char ch)
        {
            return Utilities.UrlLinks.EndsUrl(ch);
        }
    }

    /// <summary>
    /// An address in the output, as a link that opens it in the browser. Every cell of the address points at the same object, which
    /// grows as the address arrives (it may come in several pieces); the scheme decides whether it is opened at all (http, https,
    /// ftp: "s3://bucket/key" or "git+ssh://host/x" are no files and no links either).
    /// </summary>
    private sealed class UrlLink : TerminalEmulatorBase.ILinkTarget
    {
        private readonly IOutputLinkHost _host;

        private readonly StringBuilder _text = new StringBuilder();

        // IsActive is asked at every paint: the answer is worked out again only when the address has grown.
        private int _checkedLength = -1;

        private bool _active;

        public UrlLink(IOutputLinkHost host, string start)
        {
            _host = host;
            _text.Append(start);
        }

        public void Append(char ch)
        {
            // An address far longer than any real one is not followed further (a dump of base64 after "data://").
            if (_text.Length < 8192)
            {
                _text.Append(ch);
            }
        }

        public string Url => Utilities.UrlLinks.Trim(_text.ToString());

        public bool IsActive
        {
            get
            {
                if (_checkedLength != _text.Length)
                {
                    _checkedLength = _text.Length;
                    _active = Utilities.UrlLinks.IsOpenable(Url);
                }
                return _active;
            }
        }

        public void Activate()
        {
            string url = Url;
            if (Utilities.UrlLinks.IsOpenable(url))
            {
                _host.RequestOpenUrl(url);
            }
        }

        public override string ToString()
        {
            return "url:" + _text;
        }
    }

    private readonly UrlTracker _url = new UrlTracker();

    // The address being read, when addresses are links (see the constructor); null outside of one.
    private UrlLink _urlLink;

    // The address that the last character belongs to, and how many characters before it (its scheme and ":/", read before it was
    // known to be an address) belong to it as well.
    private UrlLink _lastUrl;

    private int _urlBackfill;

    private readonly bool _urls;

    private readonly IOutputLinkHost _host;

    private readonly string _startDirectory;

    // Where the build tool started: a directory that ninja announces is relative to it.
    private readonly string _shellDirectory;

    // The directories that make and ninja announced and have not left yet; the last one is where the names are relative to.
    private readonly List<string> _entered = new List<string>();

    private readonly StringBuilder _tail = new StringBuilder();

    // What the columns of the messages of this command count (see CompilerColumns).
    private readonly ColumnUnit _columnUnit;

    private readonly bool _links;

    private readonly bool _highlight;

    // Mark the log levels of a log line (ERROR, WARN, ...) from the level to the end of the line.
    private readonly bool _logLevels;

    // The text of the line so far, for the words of errors and warnings, which are told by what stands in front of them.
    private readonly StringBuilder _line = new StringBuilder();

    private bool _lineTooLong;

    // The log level found in the line so far (0: none yet): the rest of the line gets it without looking again.
    private byte _lineLevel;

    private LinkCandidate _current;

    // The last name that started with '/': the directory of an "Entering directory" message is that one.
    private LinkCandidate _lastAbsolute;

    private char _previousChar;

    // For a terminal that shows more than the output of one command (a classic tab, with its prompts and its listings): a name
    // that starts with "/" is a link only when it is a file known by its extension or name, or a position follows it. Without
    // that, the directory in a prompt ("me@host:/var/log$") and the ones that "ls -d" prints would be links.
    private readonly bool _strictAbsolute;

    /// <param name="host">The terminal.</param>
    /// <param name="workingDirectory">Where the command was typed.</param>
    /// <param name="commandText">The command line; where it says the build works in another directory, the names are relative to that.</param>
    /// <param name="links">Make links of the file names.</param>
    /// <param name="highlight">Mark the words of errors, warnings and notes.</param>
    /// <param name="logLevels">Mark the lines of a log from their level (ERROR, WARN, [error], level=warn) to the end.</param>
    /// <param name="columnSetting">What the columns count, or Auto to tell it from the command line.</param>
    /// <param name="strictAbsolute">An absolute name is a link only when it is a known kind of file or has a position (a classic terminal tab).</param>
    /// <param name="urls">Make links of the http, https and ftp addresses, which open in the browser. With or without it, no file name is looked for in an address.</param>
    public OutputLinkScanner(IOutputLinkHost host, string workingDirectory, string commandText, bool links = true, bool highlight = false, bool logLevels = false, ColumnUnit columnSetting = ColumnUnit.Auto, bool strictAbsolute = false, bool urls = false)
    {
        _host = host;
        _urls = urls;
        _strictAbsolute = strictAbsolute;
        _columnUnit = CompilerColumns.Resolve(columnSetting, commandText);
        _links = links;
        _highlight = highlight;
        _logLevels = logLevels;
        _startDirectory = BuildDirectories.Analyze(commandText, workingDirectory, out _shellDirectory) ?? workingDirectory;
    }

    /// <summary>The directory that the names printed now are relative to.</summary>
    public string CurrentDirectory => _entered.Count > 0 ? _entered[_entered.Count - 1] : _startDirectory;

    private static bool IsPathChar(char ch)
    {
        return PathCharacters.IndexOf(ch) >= 0;
    }

    private void TrackDirectories(char ch)
    {
        _tail.Append(ch);
        if (_tail.Length > TailLimit)
        {
            _tail.Remove(0, _tail.Length - TailLimit / 2);
        }
        if (ch != '\'' && ch != '’')
        {
            return;
        }
        if (!BuildDirectories.TryParseMessage(_tail.ToString(), out bool entering, out string directory))
        {
            return;
        }
        _tail.Clear();
        // The directory in the message was taken for a file name, and it is none.
        if (_lastAbsolute != null && _lastAbsolute.PathText == directory)
        {
            _lastAbsolute.Reject();
        }
        if (entering)
        {
            // A directory that is not absolute is relative to the one the tool started in, or to the one it is in already.
            _entered.Add(BuildDirectories.Resolve(_entered.Count > 0 ? _entered[_entered.Count - 1] : _shellDirectory, directory));
        }
        else if (_entered.Count > 0)
        {
            _entered.RemoveAt(_entered.Count - 1);
        }
    }

    // The scheme of an address from the run of scheme characters in front of its ":": "see.https" or "a-http" end with one that is
    // opened, after a character that is not a letter or a digit; that one is the start of the link.
    private static string SchemeOf(string run)
    {
        foreach (string scheme in new[] { "https", "http", "ftp" })
        {
            if (run.Length > scheme.Length && run.EndsWith(scheme, StringComparison.OrdinalIgnoreCase) && !char.IsLetterOrDigit(run[run.Length - scheme.Length - 1]))
            {
                return run.Substring(run.Length - scheme.Length);
            }
        }
        return run;
    }

    private LinkCandidate ReadChar(char ch)
    {
        TrackDirectories(ch);
        char prevChar = _previousChar;
        _previousChar = ch;
        if (_url.Feed(ch))
        {
            // Inside an address: no file name is looked for in it (and the one that had begun is not a name).
            _current = null;
            if (_urls)
            {
                if (_url.JustStarted)
                {
                    // The scheme, the ":" and the first "/" came before: they belong to the link as well.
                    string scheme = SchemeOf(_url.Scheme ?? "");
                    _urlLink = new UrlLink(_host, scheme + ":/");
                    _urlBackfill = scheme.Length + 2;
                }
                _urlLink?.Append(ch);
                _lastUrl = _urlLink;
            }
            return null;
        }
        _urlLink = null;
        _lastUrl = null;
        if (!_links)
        {
            return null;
        }
        if (_current == null)
        {
            if (IsPathChar(ch) && prevChar != '/')
            {
                _current = new LinkCandidate(_host, CurrentDirectory, _columnUnit, _strictAbsolute, ch);
                if (ch == '/')
                {
                    _lastAbsolute = _current;
                    // "~/notes.txt": the shell expands the "~", and the scanner cannot, so there is no name to make a link of.
                    if (prevChar == '~')
                    {
                        _current.Swallow();
                    }
                }
                return _current;
            }
            return null;
        }
        if (!_current.Accept(ch))
        {
            _current = null;
        }
        return _current;
    }

    public TerminalEmulatorBase.LinkCell[] Scan(string lineText)
    {
        return Scan(lineText, -1);
    }

    /// <param name="text">A piece of the output, within one line.</param>
    /// <param name="column">The column of the piece on the screen: 0 starts a new line; -1 when it is not known.</param>
    public TerminalEmulatorBase.LinkCell[] Scan(string text, int column)
    {
        TerminalEmulatorBase.LinkCell[] cells = new TerminalEmulatorBase.LinkCell[text.Length];
        if (column == 0)
        {
            // A new line: an address that was not ended by a blank does not go on in it. A row that only continues the one above it
            // (the text was wrapped at the edge of the screen) is passed with the column -1 by the terminal, and the address goes on.
            _url.Reset();
            _urlLink = null;
        }
        if (_links || _urls)
        {
            char[] charArray = text.ToArray();
            LinkCandidate[] candidateArray = new LinkCandidate[charArray.Length];
            UrlLink[] urlArray = new UrlLink[charArray.Length];
            for (int i = 0; i < charArray.Length; i++)
            {
                candidateArray[i] = ReadChar(charArray[i]);
                urlArray[i] = _lastUrl;
                if (_urlBackfill > 0)
                {
                    // Within this piece; a scheme that arrived in an earlier piece stays as it was drawn (the rest of the address is the link).
                    for (int j = i - 1; j >= 0 && j >= i - _urlBackfill; j--)
                    {
                        urlArray[j] = _lastUrl;
                    }
                    _urlBackfill = 0;
                }
            }
            for (int i = 0; i < candidateArray.Length; i++)
            {
                if (urlArray[i] != null)
                {
                    cells[i].Target = urlArray[i];
                    continue;
                }
                if (candidateArray[i] != null && candidateArray[i].State == CandidateState.Rejected)
                {
                    candidateArray[i] = null;
                }
                else if (charArray[i] == ':' && (i >= candidateArray.Length - 1 || candidateArray[i + 1] == null || candidateArray[i + 1].State == CandidateState.Rejected))
                {
                    candidateArray[i] = null;
                }
                cells[i].Target = candidateArray[i];
            }
        }
        if (_highlight || _logLevels)
        {
            MarkDiagnostics(text, column, cells);
        }
        return cells;
    }

    private void MarkDiagnostics(string text, int column, TerminalEmulatorBase.LinkCell[] cells)
    {
        if (column == 0)
        {
            _line.Clear();
            _lineTooLong = false;
            _lineLevel = 0;
        }
        if (_logLevels && _lineLevel != 0)
        {
            for (int i = 0; i < cells.Length; i++)
            {
                if (cells[i].Severity == 0)
                {
                    cells[i].Severity = _lineLevel;
                }
            }
            if (!_highlight)
            {
                return;
            }
        }
        if (_lineTooLong)
        {
            return;
        }
        int offset = _line.Length;
        if (offset + text.Length > MaxAnalyzedLine)
        {
            _lineTooLong = true;
            return;
        }
        _line.Append(text);
        if (_highlight)
        {
            foreach (DiagnosticHighlighter.Span span in DiagnosticHighlighter.FindSpans(_line.ToString()))
            {
                // Only the part of the span that is in this piece: the earlier part has been written already.
                int from = Math.Max(span.Start, offset) - offset;
                int to = Math.Min(span.Start + span.Length, offset + text.Length) - offset;
                for (int i = from; i < to; i++)
                {
                    cells[i].Severity = span.Severity;
                }
            }
        }
        if (_logLevels && _lineLevel == 0 && DiagnosticHighlighter.TryFindLogLevel(_line.ToString(), out int levelStart, out byte level))
        {
            _lineLevel = level;
            // From the level to the end of the line: the pieces that follow it in the same line are covered too.
            for (int i = Math.Max(0, levelStart - offset); i < text.Length; i++)
            {
                if (cells[i].Severity == 0)
                {
                    cells[i].Severity = level;
                }
            }
        }
    }

    public void Finish()
    {
        _current?.Complete();
    }
}
