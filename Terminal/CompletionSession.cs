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

        public Token(string text, int value2, int value3)
        {
            Start = value2;
            this.Length = value3 - value2;
            this.IsQuoted = value2 > 0 && value2 < text.Length && text[value2] == '"';
            this.Text = text.Substring(Start, this.Length);
        }

        public override string ToString()
        {
            return Text;
        }

        public bool Contains(int value2)
        {
            if (value2 >= Start && value2 <= Start + Length)
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

    public CompletionSession(SshTerminalControl sshTerminalControl, string text, CompletionTarget completionTarget, Token token, ICompletionPopup completionPopup, bool flag, CommandCompletion commandCompletion)
    {
        _terminal = sshTerminalControl;
        _target = completionTarget;
        _token = token;
        _commandText = text;
        _popup = completionPopup;
        _popup.ItemSelected += OnItemSelected;
        _autoApply = flag;
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
        bool flag;
        if (text == null)
        {
            flag = _showingOptions;
        }
        else
        {
            flag = text.StartsWith("-");
            _popup.SetFilter(GetFilterText(text));
        }
        if (flag == _showingOptions && text != null)
        {
            return;
        }
        if (flag && _commandCompletion?.Options != null)
        {
            List<CompletionItem> list = _commandCompletion.Options.SelectMany((CommandCompletion.Option option) => from option2 in new string[1] { option.Value }.Concat((option.Aliases ?? "").Split('|'))
                                                                                                                   select new CompletionItem
                                                                                                                   {
                                                                                                                       Icon = VirtualTerminalPopupItemIcon.Flag,
                                                                                                                       Text = option2,
                                                                                                                       InsertText = option2,
                                                                                                                       Description = option.ShortDescription,
                                                                                                                       Summary = new VirtualTerminalPopupSummary.OptionInfo
                                                                                                                       {
                                                                                                                           Option = option2,
                                                                                                                           Description = option.LongDescription
                                                                                                                       }
                                                                                                                   }).ToList();
            list.Sort((CompletionItem completionItem, CompletionItem completionItem2) => StringComparer.Ordinal.Compare(completionItem.Text, completionItem2.Text));
            _popup.SetItemList(list.ToArray());
        }
        else
        {
            _popup.SetItemList(_items?.ToArray());
        }
        _showingOptions = flag;
    }

    private ListingTarget ResolveListing(string text3)
    {
        string text = (text3 ?? "").Trim('\'', '"');
        if (text.StartsWith("~"))
        {
            text = _terminal.HomeDirectory + text.Substring(1);
        }
        int num = text.LastIndexOf('/');
        switch (num)
        {
            case -1:
                return new ListingTarget
                {
                    Directory = _terminal.CurrentDirectory
                };
            case 0:
                if (num < text.Length && text[num] == '/')
                {
                    return new ListingTarget
                    {
                        Directory = "/",
                        Prefix = "/"
                    };
                }
                break;
        }
        string text2 = text.Substring(0, num);
        return new ListingTarget
        {
            Directory = RemotePathUtils.Combine(_terminal.CurrentDirectory, text2),
            Prefix = text2
        };
    }

    private void LoadItems()
    {
        try
        {
            List<CompletionItem> list;
            while (true)
            {
                ListingTarget listingTarget = _listing;
                if (_target == CompletionTarget.Command && !listingTarget.HasPrefix)
                {
                    IEnumerable<string> source = _terminal._shellCompletionAnalyzer.GetExecutables(_terminal);
                    list = (from ba2 in source.Select((string fullPath) => new CompletionItem
                    {
                        Icon = VirtualTerminalPopupItemIcon.Command,
                        Text = RemotePathUtils.GetFileName(fullPath),
                        Summary = new VirtualTerminalPopupSummary.PathInfo
                        {
                            FullPath = fullPath
                        },
                        InsertText = RemotePathUtils.GetFileName(fullPath)
                    }).Concat(from text2 in "alias,bg,bind,break,builtin,case,cd,command,compgen,complete,continue,declare,dirs,disown,echo,enable,eval,exec,exit,export,fc,fg,getopts,hash,help,history,if,jobs,kill,let,local,logout,popd,printf,pushd,pwd,read,readonly,return,set,shift,shopt,source,suspend,test,times,trap,type,typeset,ulimit,umask,unalias,unset,until,wait,while".Split(',')
                              select new CompletionItem
                              {
                                  Icon = VirtualTerminalPopupItemIcon.Command,
                                  Text = text2,
                                  InsertText = text2
                              })
                            orderby ba2.Text
                            select ba2).ToList();
                }
                else
                {
                    string text = LsOutputParser.LsParser.BuildListCommand(listingTarget.Directory, flag: false, flag2: false);
                    IEnumerable<LsOutputParser.LsEntry> enumerable = _terminal.Session.RunCommandChecked(_terminal.BuildRemoteCommand(text, false), requireSuccess: true, "xterm")?.Select((string text2) => LsOutputParser.LsParser.ParseLine(text2))?.Where((LsOutputParser.LsEntry lsEntry) => lsEntry.Name != null && lsEntry.Name != ".");
                    if (enumerable == null)
                    {
                        throw new Exception("Failed to list " + listingTarget.Directory);
                    }
                    if (_target == CompletionTarget.Command)
                    {
                        enumerable = enumerable.Where((LsOutputParser.LsEntry lsEntry) => lsEntry.IsExecutable || lsEntry.IsDirectory);
                    }
                    else if (_target == CompletionTarget.Directory)
                    {
                        enumerable = enumerable.Where((LsOutputParser.LsEntry lsEntry) => lsEntry.IsDirectory);
                    }
                    if (listingTarget.HasPrefix && (RemotePathUtils.NormalizeUnix(listingTarget.Directory) == "/" || !listingTarget.Directory.EndsWith("/..")))
                    {
                        enumerable = enumerable.Where((LsOutputParser.LsEntry lsEntry) => lsEntry.Name != "..");
                    }
                    list = enumerable.Select((LsOutputParser.LsEntry lsEntry) => new CompletionItem
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
                    list.Sort(CompareItems);
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
                OnItemsLoaded(list);
            }));
        }
        catch (Exception ex)
        {
            _popup.DisplayException(ex);
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
        string text2 = GetFilterText(_token.Text);
        CompletionItem[] array = list.Where((CompletionItem completionItem) => completionItem.Text.StartsWith(text2)).ToArray();
        if (array.Length == 1)
        {
            ApplyItem(array[0], flag3: false);
        }
        else if (array.Length != 0)
        {
            string text = array[0].Text;
            CompletionItem[] array2 = array;
            for (int num = 0; num < array2.Length; num++)
            {
                CompletionItem completionItem2 = array2[num];
                text = CommonPrefix(text, completionItem2.Text);
            }
            if (text.Length > 0 && text != text2)
            {
                ApplyItem(new CompletionItem
                {
                    Text = text,
                    InsertText = text
                }, flag3: false);
            }
        }
    }

    private static string CommonPrefix(string text, string text2)
    {
        int num = Math.Min(text.Length, text2.Length);
        for (int num2 = 0; num2 < num; num2++)
        {
            if (text[num2] != text2[num2])
            {
                return text.Substring(0, num2);
            }
        }
        return text.Substring(0, num);
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

    private static int CompareItems(CompletionItem completionItem, CompletionItem completionItem2)
    {
        if (completionItem.Icon != completionItem2.Icon)
        {
            if (completionItem.Icon == VirtualTerminalPopupItemIcon.Directory)
            {
                return -1;
            }
            return 1;
        }
        if ((completionItem.Text == ".." || completionItem2.Text == "..") && completionItem.Text != completionItem2.Text)
        {
            if (!(completionItem.Text == ".."))
            {
                return 1;
            }
            return -1;
        }
        bool flag = completionItem.Text.StartsWith(".");
        bool flag2 = completionItem2.Text.StartsWith(".");
        if (flag != flag2)
        {
            if (!flag)
            {
                return -1;
            }
            return 1;
        }
        return StringComparer.InvariantCulture.Compare(completionItem.Text, completionItem2.Text);
    }

    private void OnItemSelected(CompletionItem completionItem, Keys keys)
    {
        ApplyItem(completionItem, keys == Keys.Return);
    }

    private void ApplyItem(CompletionItem completionItem, bool flag3, string text3 = "")
    {
        if (completionItem.InsertText == null)
        {
            return;
        }
        try
        {
            bool flag = IsQuoted;
            string text = completionItem.InsertText;
            if (!flag)
            {
                text = text.Replace(" ", "\\ ");
            }
            bool flag2 = text.EndsWith("/");
            if (flag2)
            {
                if (text3 == "/")
                {
                    text3 = "";
                }
                else if (flag3)
                {
                    text = text.TrimEnd('/');
                }
            }
            int num = 0;
            if (flag)
            {
                text = _token.Text[0] + text + _token.Text[0];
                num = -1;
            }
            string text2 = _commandText.Substring(0, _token.Start) + text + _commandText.Substring(_token.End) + text3;
            AppliedEventArgs appliedEventArgs = new AppliedEventArgs
            {
                NewText = text2,
                CaretIndex = _token.Start + text.Length + (text3 ?? "").Length + num,
                Commit = flag3,
                Item = completionItem,
                IsDirectory = flag2
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
        int num = 0;
        bool flag = false;
        if (string.IsNullOrEmpty(text))
        {
            return list.ToArray();
        }
        bool flag2 = false;
        for (int num2 = 0; num2 < text.Length; num2++)
        {
            if (text[num2] == '"' && !flag2)
            {
                flag = !flag;
            }
            else if (text[num2] == ' ' && !flag && !flag2)
            {
                if (num2 != num)
                {
                    list.Add(new Token(text, num, num2));
                }
                num = num2 + 1;
            }
            flag2 = text[num2] == '\\' && !flag2;
        }
        if (num != text.Length)
        {
            list.Add(new Token(text, num, text.Length));
        }
        return list.ToArray();
    }

    private static bool ContainsUnescapedSpace(string text)
    {
        bool flag = false;
        for (int num = 0; num < text.Length; num++)
        {
            if (!flag && text[num] == ' ')
            {
                return true;
            }
            flag = text[num] == '\\' && !flag;
        }
        return false;
    }

    public bool OnTextChanged(string text2, int value2, int value3, int value4)
    {
        int num = value2 + value3;
        if (value2 >= _token.Start && value2 <= _token.End && num <= _token.End)
        {
            int num2 = value4 - value3;
            _token.Length += num2;
            _commandText = text2;
            string text = _token.Extract(text2) ?? "";
            if (text.IndexOfAny(new char[7] { '`', '$', '<', '>', '|', '&', ';' }) != -1)
            {
                return true;
            }
            if (ContainsUnescapedSpace(text) && text.IndexOfAny(new char[3] { '`', '\'', '"' }) != 0)
            {
                return true;
            }
            if (text.StartsWith("-"))
            {
                return true;
            }
            ListingTarget listingTarget = ResolveListing(text);
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
            UpdatePopup(text);
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
        CompletionItem selectedItem = _popup.GetSelectedItem(flag: true);
        if (selectedItem.InsertText == null)
        {
            return false;
        }
        ApplyItem(selectedItem, flag3: false, new string(ch, 1));
        _popup.Close();
        return true;
    }
}
