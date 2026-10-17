using SmarTTY.Controls;
using SmarTTY.IO;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using VisualTTY;

namespace SmarTTY.Terminal;

internal class CompletionSession : IDisposable
{
    private struct ListingTarget
    {
        public string Directory;

        public string Prefix;

        public bool HasPrefix => Prefix != null;
    }

    public class AppliedEventArgs : EventArgs
    {
        public string NewText;

        public int CaretIndex;

        public bool Commit;

        public CompletionItem Item;

        public bool IsDirectory;
    }

    public struct Token
    {
        public int Start;

        public int Length;

        public bool IsQuoted;

        public string Text;

        public int End => Start + Length;

        public Token(string text, int start, int end)
        {
            Start = start;
            this.Length = end - start;
            this.IsQuoted = start > 0 && start < text.Length && text[start] == '"';
            this.Text = text.Substring(Start, this.Length);
        }

        public override string ToString()
        {
            return Text;
        }

        public bool Contains(int position)
        {
            if (position >= Start && position <= Start + Length)
            {
                return true;
            }
            return false;
        }

        public string Extract(string text)
        {
            if (text == null || Length < 0 || End > text.Length)
            {
                return "";
            }
            return text.Substring(Start, Length);
        }
    }

    private SshTerminalControl _terminal;

    private readonly CompletionTarget _target;

    private Token _token;

    private ICompletionPopup _popup;

    private string _commandText;

    private readonly bool _autoApply;

    private readonly CommandCompletion _commandCompletion;

    private ListingTarget _listing;

    private object _lock = new object();

    private bool _isLoading;

    private bool _showingOptions;

    private List<CompletionItem> _items;

    private bool IsQuoted
    {
        get
        {
            char ch = _token.Text?.FirstOrDefault() ?? '\0';
            if (ch == '"' || ch == '\'' || ch == '`')
            {
                return true;
            }
            return false;
        }
    }

    public AppliedEventArgs LastApplied { get; private set; }

    public bool IsOpen => _popup.IsOpen;

    public event EventHandler<AppliedEventArgs> ItemApplied;

    public CompletionSession(SshTerminalControl sshTerminalControl, string commandText, CompletionTarget completionTarget, Token token, ICompletionPopup completionPopup, bool autoApply, CommandCompletion commandCompletion)
    {
        _terminal = sshTerminalControl;
        _target = completionTarget;
        _token = token;
        _commandText = commandText;
        _popup = completionPopup;
        _popup.ItemSelected += OnItemSelected;
        _autoApply = autoApply;
        _commandCompletion = commandCompletion;
        _listing = ResolveListing(_token.Text);
        UpdatePopup(_token.Text);
        this._isLoading = true;
        new Thread(LoadItems).Start();
    }

    private static string GetFilterText(string text)
    {
        text = text ?? "";
        return RemotePathUtils.GetFileName(text.Trim('"', '\'')) ?? "";
    }

    private void UpdatePopup(string text)
    {
        bool isOption;
        if (text == null)
        {
            isOption = _showingOptions;
        }
        else
        {
            isOption = text.StartsWith("-");
            _popup.SetFilter(GetFilterText(text));
        }
        if (isOption == _showingOptions && text != null)
        {
            return;
        }
        if (isOption && _commandCompletion?.Options != null)
        {
            List<CompletionItem> list = _commandCompletion.Options.SelectMany((CommandCompletion.Option option) => from val in new string[1] { option.Value }.Concat((option.Aliases ?? "").Split('|'))
                                                                                                                   select new CompletionItem
                                                                                                                   {
                                                                                                                       Icon = VirtualTerminalPopupItemIcon.Flag,
                                                                                                                       Text = val,
                                                                                                                       InsertText = val,
                                                                                                                       Description = option.ShortDescription,
                                                                                                                       Summary = new VirtualTerminalPopupSummary.OptionInfo
                                                                                                                       {
                                                                                                                           Option = val,
                                                                                                                           Description = option.LongDescription
                                                                                                                       }
                                                                                                                   }).ToList();
            list.Sort((CompletionItem first, CompletionItem second) => StringComparer.Ordinal.Compare(first.Text, second.Text));
            _popup.SetItemList(list.ToArray());
        }
        else
        {
            _popup.SetItemList(_items?.ToArray());
        }
        _showingOptions = isOption;
    }

    private ListingTarget ResolveListing(string rawText)
    {
        string pathText = (rawText ?? "").Trim('\'', '"');
        if (pathText.StartsWith("~"))
        {
            pathText = _terminal.HomeDirectory + pathText.Substring(1);
        }
        int slashIndex = pathText.LastIndexOf('/');
        switch (slashIndex)
        {
            case -1:
                return new ListingTarget
                {
                    Directory = _terminal.CurrentDirectory
                };
            case 0:
                if (slashIndex < pathText.Length && pathText[slashIndex] == '/')
                {
                    return new ListingTarget
                    {
                        Directory = "/",
                        Prefix = "/"
                    };
                }
                break;
        }
        string prefix = pathText.Substring(0, slashIndex);
        return new ListingTarget
        {
            Directory = RemotePathUtils.Combine(_terminal.CurrentDirectory, prefix),
            Prefix = prefix
        };
    }

    private void LoadItems()
    {
        try
        {
            List<CompletionItem> itemsList;
            while (true)
            {
                ListingTarget listingTarget = _listing;
                if (_target == CompletionTarget.Command && !listingTarget.HasPrefix)
                {
                    IEnumerable<string> source = _terminal._shellCompletionAnalyzer.GetExecutables(_terminal);
                    itemsList = (from item in source.Select((string fullPath) => new CompletionItem
                    {
                        Icon = VirtualTerminalPopupItemIcon.Command,
                        Text = RemotePathUtils.GetFileName(fullPath),
                        Summary = new VirtualTerminalPopupSummary.PathInfo
                        {
                            FullPath = fullPath
                        },
                        InsertText = RemotePathUtils.GetFileName(fullPath)
                    }).Concat(from builtinCmd in "alias,bg,bind,break,builtin,case,cd,command,compgen,complete,continue,declare,dirs,disown,echo,enable,eval,exec,exit,export,fc,fg,getopts,hash,help,history,if,jobs,kill,let,local,logout,popd,printf,pushd,pwd,read,readonly,return,set,shift,shopt,source,suspend,test,times,trap,type,typeset,ulimit,umask,unalias,unset,until,wait,while".Split(',')
                              select new CompletionItem
                              {
                                  Icon = VirtualTerminalPopupItemIcon.Command,
                                  Text = builtinCmd,
                                  InsertText = builtinCmd
                              })
                            orderby item.Text
                            select item).ToList();
                }
                else
                {
                    string lsCommand = LsOutputParser.LsParser.BuildListCommand(listingTarget.Directory, followSymlinks: false, recursive: false);
                    IEnumerable<LsOutputParser.LsEntry> entries = _terminal.Session.RunCommandChecked(_terminal.BuildRemoteCommand(lsCommand, false), requireSuccess: true, "xterm")?.Select((string line) => LsOutputParser.LsParser.ParseLine(line))?.Where((LsOutputParser.LsEntry lsEntry) => lsEntry.Name != null && lsEntry.Name != ".");
                    if (entries == null)
                    {
                        throw new Exception("Failed to list " + listingTarget.Directory);
                    }
                    if (_target == CompletionTarget.Command)
                    {
                        entries = entries.Where((LsOutputParser.LsEntry lsEntry) => lsEntry.IsExecutable || lsEntry.IsDirectory);
                    }
                    else if (_target == CompletionTarget.Directory)
                    {
                        entries = entries.Where((LsOutputParser.LsEntry lsEntry) => lsEntry.IsDirectory);
                    }
                    if (listingTarget.HasPrefix && (RemotePathUtils.NormalizeUnix(listingTarget.Directory) == "/" || !listingTarget.Directory.EndsWith("/..")))
                    {
                        entries = entries.Where((LsOutputParser.LsEntry lsEntry) => lsEntry.Name != "..");
                    }
                    itemsList = entries.Select((LsOutputParser.LsEntry lsEntry) => new CompletionItem
                    {
                        Icon = (lsEntry.IsDirectory ? VirtualTerminalPopupItemIcon.Directory : VirtualTerminalPopupItemIcon.File),
                        Summary = new VirtualTerminalPopupSummary.FileOrDirectoryInfo
                        {
                            FullPath = RemotePathUtils.NormalizeUnix(listingTarget.Directory.TrimEnd('/') + "/" + lsEntry.Name),
                            IsDirectory = lsEntry.IsDirectory
                        },
                        Text = lsEntry.Name,
                        InsertText = BuildInsertText(listingTarget, lsEntry)
                    }).ToList();
                    itemsList.Sort(CompareItems);
                }
                lock (_lock)
                {
                    if (!listingTarget.Equals(_listing))
                    {
                        continue;
                    }
                    _isLoading = false;
                    break;
                }
            }
            _terminal.BeginInvoke((Action)(() =>
            {
                OnItemsLoaded(itemsList);
            }));
        }
        catch (Exception exception)
        {
            _popup.DisplayException(exception);
            _isLoading = false;
        }
    }

    private void OnItemsLoaded(List<CompletionItem> list)
    {
        _items = list;
        UpdatePopup(null);
        if (!_autoApply)
        {
            return;
        }
        string filterText = GetFilterText(_token.Text);
        CompletionItem[] matches = list.Where((CompletionItem completionItem) => completionItem.Text.StartsWith(filterText)).ToArray();
        if (matches.Length == 1)
        {
            ApplyItem(matches[0], commit: false);
        }
        else if (matches.Length != 0)
        {
            string common = matches[0].Text;
            foreach (CompletionItem item in matches)
            {
                common = CommonPrefix(common, item.Text);
            }
            if (common.Length > 0 && common != filterText)
            {
                ApplyItem(new CompletionItem
                {
                    Text = common,
                    InsertText = common
                }, commit: false);
            }
        }
    }

    private static string CommonPrefix(string first, string second)
    {
        int limit = Math.Min(first.Length, second.Length);
        for (int i = 0; i < limit; i++)
        {
            if (first[i] != second[i])
            {
                return first.Substring(0, i);
            }
        }
        return first.Substring(0, limit);
    }

    private string BuildInsertText(ListingTarget listingTarget, LsOutputParser.LsEntry lsEntry)
    {
        string text = "";
        if (listingTarget.HasPrefix)
        {
            text = listingTarget.Prefix;
            if (!text.EndsWith("/"))
            {
                text += "/";
            }
        }
        text += lsEntry.Name;
        if (lsEntry.IsDirectory && !text.EndsWith("/"))
        {
            text += "/";
        }
        return text;
    }

    private static int CompareItems(CompletionItem first, CompletionItem second)
    {
        if (first.Icon != second.Icon)
        {
            if (first.Icon == VirtualTerminalPopupItemIcon.Directory)
            {
                return -1;
            }
            return 1;
        }
        if ((first.Text == ".." || second.Text == "..") && first.Text != second.Text)
        {
            if (!(first.Text == ".."))
            {
                return 1;
            }
            return -1;
        }
        bool isFirstHidden = first.Text.StartsWith(".");
        bool isSecondHidden = second.Text.StartsWith(".");
        if (isFirstHidden != isSecondHidden)
        {
            if (!isFirstHidden)
            {
                return -1;
            }
            return 1;
        }
        return StringComparer.InvariantCulture.Compare(first.Text, second.Text);
    }

    private void OnItemSelected(CompletionItem completionItem, Keys keys)
    {
        ApplyItem(completionItem, keys == Keys.Return);
    }

    private void ApplyItem(CompletionItem completionItem, bool commit, string suffix = "")
    {
        if (completionItem.InsertText == null)
        {
            return;
        }
        try
        {
            bool isQuoted = IsQuoted;
            string insertText = completionItem.InsertText;
            if (!isQuoted)
            {
                insertText = insertText.Replace(" ", "\\ ");
            }
            bool isDir = insertText.EndsWith("/");
            if (isDir)
            {
                if (suffix == "/")
                {
                    suffix = "";
                }
                else if (commit)
                {
                    insertText = insertText.TrimEnd('/');
                }
            }
            int caretOffset = 0;
            if (isQuoted)
            {
                insertText = _token.Text[0] + insertText + _token.Text[0];
                caretOffset = -1;
            }
            string newText = _commandText.Substring(0, _token.Start) + insertText + _commandText.Substring(_token.End) + suffix;
            AppliedEventArgs appliedEventArgs = new AppliedEventArgs
            {
                NewText = newText,
                CaretIndex = _token.Start + insertText.Length + (suffix ?? "").Length + caretOffset,
                Commit = commit,
                Item = completionItem,
                IsDirectory = isDir
            };
            this.ItemApplied?.Invoke(this, appliedEventArgs);
            LastApplied = appliedEventArgs;
        }
        catch
        {
        }
    }

    public static Token[] Tokenize(string text)
    {
        List<Token> list = new List<Token>();
        int tokenStart = 0;
        bool inQuotes = false;
        if (string.IsNullOrEmpty(text))
        {
            return list.ToArray();
        }
        bool isEscaped = false;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '"' && !isEscaped)
            {
                inQuotes = !inQuotes;
            }
            else if (text[i] == ' ' && !inQuotes && !isEscaped)
            {
                if (i != tokenStart)
                {
                    list.Add(new Token(text, tokenStart, i));
                }
                tokenStart = i + 1;
            }
            isEscaped = text[i] == '\\' && !isEscaped;
        }
        if (tokenStart != text.Length)
        {
            list.Add(new Token(text, tokenStart, text.Length));
        }
        return list.ToArray();
    }

    private static bool ContainsUnescapedSpace(string text)
    {
        bool isEscaped = false;
        for (int i = 0; i < text.Length; i++)
        {
            if (!isEscaped && text[i] == ' ')
            {
                return true;
            }
            isEscaped = text[i] == '\\' && !isEscaped;
        }
        return false;
    }

    public bool OnTextChanged(string newFullText, int offset, int oldLength, int newLength)
    {
        int endOffset = offset + oldLength;
        if (offset >= _token.Start && offset <= _token.End && endOffset <= _token.End)
        {
            int lengthDiff = newLength - oldLength;
            _token.Length += lengthDiff;
            _commandText = newFullText;
            string extracted = _token.Extract(newFullText) ?? "";
            if (extracted.IndexOfAny(new char[7] { '`', '$', '<', '>', '|', '&', ';' }) != -1)
            {
                return true;
            }
            if (ContainsUnescapedSpace(extracted) && extracted.IndexOfAny(new char[3] { '`', '\'', '"' }) != 0)
            {
                return true;
            }
            if (extracted.StartsWith("-"))
            {
                return true;
            }
            ListingTarget listingTarget = ResolveListing(extracted);
            if (!listingTarget.Equals(_listing))
            {
                _listing = listingTarget;
                lock (_lock)
                {
                    if (!_isLoading)
                    {
                        _isLoading = true;
                        new Thread(LoadItems).Start();
                    }
                }
            }
            UpdatePopup(extracted);
            return false;
        }
        return true;
    }

    public void Dispose()
    {
        _popup.Close();
    }

    public bool HandleKey(KeyEventArgs e)
    {
        return _popup.TryHandleKeyEvent(e);
    }

    public bool HandleChar(char ch)
    {
        switch (ch)
        {
            case ' ':
                if (IsQuoted)
                {
                    return false;
                }
                break;
            default:
                return false;
            case '/':
                break;
        }
        CompletionItem selectedItem = _popup.GetSelectedItem(onlyIfPrefixMatch: true);
        if (selectedItem.InsertText == null)
        {
            return false;
        }
        ApplyItem(selectedItem, commit: false, new string(ch, 1));
        _popup.Close();
        return true;
    }
}
