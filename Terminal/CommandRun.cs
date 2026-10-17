using SmarTTY.Controls;
using SmarTTY.IO;
using System.Linq;
using System.Text;

namespace SmarTTY.Terminal;

internal class CommandRun
{
    private enum CandidateState
    {
        Word,
        Rejected,
        AbsolutePath,
        Position
    }

    private class LinkCandidate : TerminalEmulatorBase.ILinkTarget
    {
        public CandidateState State;

        private StringBuilder _path = new StringBuilder();

        private SshTerminalControl _terminal;

        private string _workingDirectory;

        private StringBuilder _extension;

        private int? _line;

        private int? _column;

        private string _numberBuffer;

        public bool IsActive => State == CommandRun.CandidateState.Position;

        public LinkCandidate(SshTerminalControl sshTerminalControl, string workingDirectory, char firstChar)
        {
            _terminal = sshTerminalControl;
            this._workingDirectory = workingDirectory;
            _path.Append(firstChar);
            State = ((firstChar == '/') ? CommandRun.CandidateState.AbsolutePath : CommandRun.CandidateState.Word);
        }

        public void Activate()
        {
            _terminal.RequestOpenFile(RemotePathUtils.Combine(_workingDirectory, _path.ToString()), _line, _column);
        }

        public override string ToString()
        {
            return State switch
            {
                CommandRun.CandidateState.Rejected => "-",
                CommandRun.CandidateState.Word => ">" + _path.ToString(),
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
            if (State == CommandRun.CandidateState.Rejected)
            {
                return false;
            }
            if (State == CommandRun.CandidateState.Position)
            {
                return this.ParsePositionChar(ch);
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
            Complete();
            if (ch == ':')
            {
                return true;
            }
            return false;
        }

        public void Complete()
        {
            if (State == CommandRun.CandidateState.Position && !string.IsNullOrEmpty(_numberBuffer))
            {
                ParsePositionChar('\0');
                _numberBuffer = null;
            }
            if (State == CommandRun.CandidateState.AbsolutePath)
            {
                State = CommandRun.CandidateState.Position;
            }
            else if (State == CommandRun.CandidateState.Word && _extension != null && _terminal.IsLinkExtension(_extension.ToString()))
            {
                State = CommandRun.CandidateState.Position;
            }
            else
            {
                State = CommandRun.CandidateState.Rejected;
            }
        }
    }

    private class LinkScanner : TerminalEmulatorBase.ILinkScanner
    {

        private CommandRun _run;

        private LinkCandidate _current;

        private char _previousChar;

        public LinkScanner(CommandRun commandRun)
        {
            _run = commandRun;
        }

        private LinkCandidate ReadChar(char ch)
        {
            char prevChar = _previousChar;
            _previousChar = ch;
            if (_current == null)
            {
                if (IsPathChar(ch) && prevChar != '/')
                {
                    return _current = new LinkCandidate(_run._terminal, _run.WorkingDirectory, ch);
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
            char[] charArray = lineText.ToArray();
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
            }
            return candidateArray.Select((LinkCandidate linkCandidate) => new TerminalEmulatorBase.LinkCell
            {
                Target = linkCandidate
            }).ToArray();
        }

        public void Finish()
        {
            _current?.Complete();
        }
    }

    private readonly SshTerminalControl _terminal;

    public readonly string WorkingDirectory;

    public readonly string CommandText;

    public readonly int StartLine;

    public const string PathCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_.-/";

    public int UnusedValue { get; private set; }

    public CommandRun(SshTerminalControl sshTerminalControl, string commandText, string workingDirectory, int startLine)
    {
        _terminal = sshTerminalControl;
        WorkingDirectory = workingDirectory;
        CommandText = commandText;
        this.StartLine = startLine;
    }

    private static bool IsPathChar(char ch)
    {
        return "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_.-/".Contains(ch);
    }

    internal TerminalEmulatorBase.ILinkScanner CreateScanner()
    {
        return new LinkScanner(this);
    }
}
