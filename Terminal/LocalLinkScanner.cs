using SmarTTY.Terminal.Emulation;
using System.IO;
using System.Linq;
using System.Text;

namespace SmarTTY.Terminal;

/// <summary>What the scanner of the links in the text of a device terminal needs of the terminal: does a path exist, and open it.</summary>
internal interface ILocalLinkHost
{
    /// <summary>
    /// Whether the path exists on this computer. It answers at once with the last known answer and checks in the background (painting and
    /// hovering ask for every piece of a line), so a slow disk does not freeze the window; the terminal is painted again when the answer changes.
    /// </summary>
    bool PathExists(string path);

    /// <summary>The link was clicked: open the address or the path.</summary>
    void RequestOpenFile(string path);
}

// The scanner of the text a COM/TCP/telnet device prints: the addresses (http, https, ftp) and the paths with a drive letter that name a file of
// this computer. It was nested in SerialTerminalControl, as it was.
internal enum LinkCandidateState
{
    Word,
    Rejected,
    Position
}

internal class LocalLinkCandidate : ILinkTarget
{
    // ':' and '\\' are valid here too: drive-letter paths (C:\...) and URLs with ports stay in one candidate.
    internal static readonly string PathCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_.-/:\\";

    public LinkCandidateState State;

    private readonly ILocalLinkHost _host;

    private readonly StringBuilder _path = new StringBuilder();

    // The word has begun with "http://", "https://" or "ftp://": from there on it is read as an address, up to a blank, a quote or
    // an angle bracket ("?id=5&x=1#top" is part of it), not only as the characters of a path.
    private bool _isUrl;

    public LocalLinkCandidate(ILocalLinkHost host, char firstChar)
    {
        _host = host;
        _path.Append(firstChar);
        State = LinkCandidateState.Word;
    }

    public bool IsActive
    {
        get
        {
            if (State != LinkCandidateState.Position)
            {
                return false;
            }
            string path = TargetPath;
            if (path == null)
            {
                return false;
            }
            if (IsUrl(path))
            {
                return SmarTTY.Utilities.UrlLinks.IsOpenable(path);
            }
            // This is asked for every piece of the line each time it is painted, and when the mouse moves over it: it must
            // not wait for the disk. The answer is the last one that is known, the check runs in the background and the
            // terminal is painted again when it finds a different one.
            return _host.PathExists(path);
        }
    }

    public void Activate()
    {
        string path = TargetPath;
        if (path != null)
        {
            _host.RequestOpenFile(path);
        }
    }

    private static bool IsUrl(string path)
    {
        return SmarTTY.Utilities.UrlLinks.StartsWithOpenableScheme(path);
    }

    private string _targetPath;

    private bool _hasTargetPath;

    // The path does not change once the candidate is complete, so it is worked out once, not at every paint.
    private string TargetPath
    {
        get
        {
            if (_hasTargetPath)
            {
                return _targetPath;
            }
            string path = ComputeTargetPath();
            if (State == LinkCandidateState.Position)
            {
                _targetPath = path;
                _hasTargetPath = true;
            }
            return path;
        }
    }

    private string ComputeTargetPath()
    {
        try
        {
            string path = _path.ToString();
            if (IsUrl(path))
            {
                // Without the punctuation of the sentence around it: "see https://example.com/a." opens https://example.com/a.
                return SmarTTY.Utilities.UrlLinks.Trim(path);
            }
            // The text comes from the device: only a path with a drive letter names this machine (see LocalLinkRules.IsLocalPath).
            // "/tmp" of a Linux host would be C:\tmp here, and a network path would make Windows connect to its host and offer
            // the credentials of the user as soon as it is asked whether the path exists: neither is looked at.
            if (!SmarTTY.Utilities.LocalLinkRules.IsLocalPath(path))
            {
                return null;
            }
            string fullPath = Path.GetFullPath(path);
            // "C:\a\..\..\x" stays on C:, but check the result as well.
            return SmarTTY.Utilities.LocalLinkRules.IsLocalPath(fullPath) ? fullPath : null;
        }
        catch
        {
            return null;
        }
    }

    public bool Accept(char ch)
    {
        if (State == LinkCandidateState.Rejected || State == LinkCandidateState.Position)
        {
            return false;
        }
        if (_isUrl)
        {
            if (!SmarTTY.Utilities.UrlLinks.EndsUrl(ch))
            {
                // An address far longer than any real one is not followed further.
                if (_path.Length < 8192)
                {
                    _path.Append(ch);
                }
                return true;
            }
            Complete();
            return false;
        }
        if (PathCharacters.IndexOf(ch) >= 0)
        {
            _path.Append(ch);
            // "https://" is the longest scheme that is looked for: after it the word is an address or never becomes one.
            if (_path.Length <= 8)
            {
                _isUrl = IsUrl(_path.ToString());
            }
            return true;
        }
        Complete();
        return false;
    }

    public void Complete()
    {
        if (State == LinkCandidateState.Rejected || State == LinkCandidateState.Position)
        {
            return;
        }
        // An address, or a path of this machine with a drive letter. A Unix path ("/tmp", "/var/log/syslog") and a bare
        // name ("build.log") are those of the device on the other end of the line, not files of this computer.
        string path = _path.ToString();
        State = (IsUrl(path) || SmarTTY.Utilities.LocalLinkRules.IsLocalPath(path)) ? LinkCandidateState.Position : LinkCandidateState.Rejected;
    }
}

internal class LocalLinkScanner : IColumnAwareLinkScanner
{
    private readonly ILocalLinkHost _host;

    private LocalLinkCandidate _current;

    private char _previousChar;

    public LocalLinkScanner(ILocalLinkHost host)
    {
        _host = host;
    }

    private LocalLinkCandidate ReadChar(char ch)
    {
        char prevChar = _previousChar;
        _previousChar = ch;
        if (_current == null)
        {
            if (LocalLinkCandidate.PathCharacters.IndexOf(ch) >= 0 && prevChar != '\\')
            {
                return _current = new LocalLinkCandidate(_host, ch);
            }
            return null;
        }
        if (!_current.Accept(ch))
        {
            _current = null;
        }
        return _current;
    }

    /// <param name="column">0 when the text starts a new line: what the line before ended with is complete then (an address does not
    /// go on with the first word of the next line); -1 for a row that only continues a full row above it (auto-wrap).</param>
    public LinkCell[] Scan(string lineText, int column)
    {
        if (column == 0 && _current != null)
        {
            _current.Complete();
            _current = null;
            _previousChar = '\0';
        }
        return Scan(lineText);
    }

    public LinkCell[] Scan(string lineText)
    {
        char[] charArray = lineText.ToArray();
        LocalLinkCandidate[] candidateArray = charArray.Select(ReadChar).ToArray();
        for (int i = 0; i < candidateArray.Length; i++)
        {
            if (candidateArray[i] != null && candidateArray[i].State == LinkCandidateState.Rejected)
            {
                candidateArray[i] = null;
            }
        }
        return candidateArray.Select((LocalLinkCandidate linkCandidate) => new LinkCell
        {
            Target = linkCandidate
        }).ToArray();
    }

    public void Finish()
    {
        _current?.Complete();
    }
}
