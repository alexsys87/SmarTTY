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

        private string _numberBuffer;

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

        private bool ParsePositionChar(char ch)
        {
            if (ch >= '0' && ch <= '9')
            {
                _numberBuffer += ch;
                return true;
            }
            if (string.IsNullOrEmpty(_numberBuffer))
            {
                return false;
            }
            if (int.TryParse(_numberBuffer, out var parsedNumber))
            {
                if (!_line.HasValue)
                {
                    _line = parsedNumber - 1;
                }
                else
                {
                    _column = parsedNumber - 1;
                }
            }
            _numberBuffer = null;
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
                return ParsePositionChar(ch);
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
                if (!string.IsNullOrEmpty(_numberBuffer))
                {
                    ParsePositionChar('\0');
                    _numberBuffer = null;
                }
                return;
            }
            if (State == CandidateState.AbsolutePath)
            {
                State = (_strictAbsolute && !positionMayFollow && !IsKnownFile()) ? CandidateState.Rejected : CandidateState.Position;
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
    public OutputLinkScanner(IOutputLinkHost host, string workingDirectory, string commandText, bool links = true, bool highlight = false, bool logLevels = false, ColumnUnit columnSetting = ColumnUnit.Auto, bool strictAbsolute = false)
    {
        _host = host;
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

    private LinkCandidate ReadChar(char ch)
    {
        TrackDirectories(ch);
        char prevChar = _previousChar;
        _previousChar = ch;
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
        if (_links)
        {
            char[] charArray = text.ToArray();
            LinkCandidate[] candidateArray = charArray.Select(ReadChar).ToArray();
            for (int i = 0; i < candidateArray.Length; i++)
            {
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
