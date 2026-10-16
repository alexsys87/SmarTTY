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

        public LinkCandidate(SshTerminalControl sshTerminalControl, string text, char ch)
        {
            _terminal = sshTerminalControl;
            this._workingDirectory = text;
            _path.Append(ch);
            State = ((ch == '/') ? CommandRun.CandidateState.AbsolutePath : CommandRun.CandidateState.Word);
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
            if (int.TryParse(_numberBuffer, out var result))
            {
                if (!_line.HasValue)
                {
                    _line = result - 1;
                }
                else
                {
                    _column = result - 1;
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
            char ch2 = _previousChar;
            _previousChar = ch;
            if (_current == null)
            {
                if (IsPathChar(ch) && ch2 != '/')
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

        public TerminalEmulatorBase.LinkCell[] Scan(string text)
        {
            char[] array = text.ToArray();
            LinkCandidate[] array2 = array.Select(ReadChar).ToArray();
            for (int num = 0; num < array2.Length; num++)
            {
                if (array2[num] != null && array2[num].State == CandidateState.Rejected)
                {
                    array2[num] = null;
                }
                else if (array[num] == ':' && (num >= array2.Length - 1 || array2[num + 1] == null || array2[num + 1].State == CandidateState.Rejected))
                {
                    array2[num] = null;
                }
            }
            return array2.Select((LinkCandidate linkCandidate) => new TerminalEmulatorBase.LinkCell
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

    public CommandRun(SshTerminalControl sshTerminalControl, string text, string text2, int value2)
    {
        _terminal = sshTerminalControl;
        WorkingDirectory = text2;
        CommandText = text;
        this.StartLine = value2;
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
