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
        // The directory to list, with "~" expanded.
        public string Directory;

        // The directory part of the word as the user means it (quotes removed, "~" kept), ending with '/'.
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

    private bool _autoApply;

    private readonly CommandCompletion _commandCompletion;

    private ListingTarget _listing;

    private object _lock = new object();

    private bool _isLoading;

    private bool _showingOptions;

    private List<CompletionItem> _items;

    private volatile bool _disposed;

    // For variables and hosts: the text kept in front of the completed name ("$", "${", "user@") and after it ("}").
    private readonly string _insertPrefix;

    private readonly string _insertSuffix;

    private bool CompletesName => _target == CompletionTarget.Variable || _target == CompletionTarget.Host;

    // The quote the word was opened with: a completed path keeps it; '\0' for an unquoted word.
    private char QuoteChar
    {
        get
        {
            char ch = _token.Text?.FirstOrDefault() ?? '\0';
            return (ch == '"' || ch == '\'') ? ch : '\0';
        }
    }

    private bool IsQuoted => QuoteChar != '\0';

    public AppliedEventArgs LastApplied { get; private set; }

    public bool IsOpen => _popup.IsOpen;

    public event EventHandler<AppliedEventArgs> ItemApplied;

    public CompletionSession(SshTerminalControl sshTerminalControl, string commandText, CompletionTarget completionTarget, Token token, ICompletionPopup completionPopup, bool autoApply, CommandCompletion commandCompletion, string insertPrefix = null, string insertSuffix = null)
    {
        _terminal = sshTerminalControl;
        _target = completionTarget;
        _insertPrefix = insertPrefix ?? "";
        _insertSuffix = insertSuffix ?? "";
        _token = token;
        _commandText = commandText;
        _popup = completionPopup;
        _popup.ItemSelected += OnItemSelected;
        _autoApply = autoApply;
        _commandCompletion = commandCompletion;
        _listing = ResolveListing(_token.Text);
        UpdatePopup(_token.Text);
        this._isLoading = true;
        new Thread(LoadItems)
        {
            IsBackground = true
        }.Start();
    }

    private string GetFilterText(string text)
    {
        if (CompletesName)
        {
            // "$HO" / "${HO}" / "root@ro": the popup is filtered by the name after the prefix.
            string name = text ?? "";
            if (_target == CompletionTarget.Host)
            {
                // "user@" may be typed after the list has opened, so it is taken from the word each time.
                name = ShellCommandLine.Unquote(name);
                return name.Substring(name.LastIndexOf('@') + 1);
            }
            if (name.StartsWith(_insertPrefix, StringComparison.Ordinal))
            {
                name = name.Substring(_insertPrefix.Length);
            }
            if (_insertSuffix.Length > 0 && name.EndsWith(_insertSuffix, StringComparison.Ordinal))
            {
                name = name.Substring(0, name.Length - _insertSuffix.Length);
            }
            return name;
        }
        return RemotePathUtils.GetFileName(ShellCommandLine.Unquote(text ?? "")) ?? "";
    }

    // Runs an action on the UI thread unless the session (or the terminal) is gone by then.
    private void PostToUi(Action action)
    {
        try
        {
            if (!_disposed && !_terminal.IsDisposed && _terminal.IsHandleCreated)
            {
                _terminal.BeginInvoke((Action)(() =>
                {
                    if (!_disposed)
                    {
                        action();
                    }
                }));
            }
        }
        catch (InvalidOperationException)
        {
        }
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
            isOption = text.StartsWith("-") && !CompletesName;
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
        // "my\ dir/" and "'my dir'/" both mean the directory "my dir": the word is unquoted before it is listed (the
        // backslash used to be passed to ls, so escaped paths never completed).
        string pathText = ShellCommandLine.Unquote(rawText ?? "");
        int slashIndex = pathText.LastIndexOf('/');
        if (slashIndex == -1)
        {
            return new ListingTarget
            {
                Directory = _terminal.CurrentDirectory
            };
        }
        string prefix = pathText.Substring(0, slashIndex + 1);
        string directory = prefix;
        if (directory == "~/" || directory.StartsWith("~/", StringComparison.Ordinal))
        {
            directory = _terminal.HomeDirectory + directory.Substring(1);
        }
        directory = directory.TrimEnd('/');
        return new ListingTarget
        {
            Directory = (directory == "") ? "/" : RemotePathUtils.Combine(_terminal.CurrentDirectory, directory),
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
                ListingTarget listingTarget;
                lock (_lock)
                {
                    listingTarget = _listing;
                }
                if (_disposed)
                {
                    return;
                }
                if (_target == CompletionTarget.Variable)
                {
                    itemsList = LoadVariables();
                }
                else if (_target == CompletionTarget.Host)
                {
                    itemsList = LoadHosts(withColon: false);
                }
                else if (_target == CompletionTarget.Command && !listingTarget.HasPrefix)
                {
                    IEnumerable<string> source = _terminal.ShellAnalyzer.GetExecutables(_terminal);
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
                    AddShellDefinitions(itemsList);
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
                    if (_target == CompletionTarget.HostOrPath && !listingTarget.HasPrefix)
                    {
                        // scp/rsync: the word may also be the "host:" of a remote path.
                        itemsList.AddRange(LoadHosts(withColon: true));
                    }
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
            PostToUi(() =>
            {
                OnItemsLoaded(itemsList);
            });
        }
        catch (Exception exception)
        {
            lock (_lock)
            {
                _isLoading = false;
            }
            PostToUi(() =>
            {
                _popup.DisplayException(exception);
            });
        }
    }

    // Aliases and functions of the interactive shell (.bashrc) are commands as well; they replace a command of the
    // same name in the list, since the shell runs them instead.
    private void AddShellDefinitions(List<CompletionItem> items)
    {
        CompletionSources.ShellDefinitions definitions = _terminal.ShellAnalyzer.GetShellDefinitions(_terminal);
        if (definitions == null)
        {
            return;
        }
        HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
        List<CompletionItem> added = new List<CompletionItem>();
        foreach (KeyValuePair<string, string> alias in definitions.Aliases)
        {
            if (names.Add(alias.Key))
            {
                added.Add(new CompletionItem
                {
                    Icon = VirtualTerminalPopupItemIcon.Command,
                    Text = alias.Key,
                    InsertText = alias.Key,
                    Description = "alias: " + alias.Value
                });
            }
        }
        foreach (string function in definitions.Functions)
        {
            if (names.Add(function))
            {
                added.Add(new CompletionItem
                {
                    Icon = VirtualTerminalPopupItemIcon.Command,
                    Text = function,
                    InsertText = function,
                    Description = "function"
                });
            }
        }
        items.RemoveAll((CompletionItem item) => names.Contains(item.Text));
        items.AddRange(added);
        items.Sort((CompletionItem first, CompletionItem second) => StringComparer.Ordinal.Compare(first.Text, second.Text));
    }

    private List<CompletionItem> LoadVariables()
    {
        SortedSet<string> names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string name in _terminal.ShellAnalyzer.GetShellDefinitions(_terminal)?.Variables ?? new List<string>())
        {
            names.Add(name);
        }
        // Variables set with "export" in this tab.
        foreach (string name in _terminal.EnvironmentNames)
        {
            names.Add(name);
        }
        return names.Select((string name) => new CompletionItem
        {
            Icon = VirtualTerminalPopupItemIcon.Variable,
            Text = name,
            InsertText = name
        }).ToList();
    }

    private List<CompletionItem> LoadHosts(bool withColon)
    {
        return _terminal.ShellAnalyzer.GetHosts().Select((CompletionSources.HostEntry host) => new CompletionItem
        {
            Icon = VirtualTerminalPopupItemIcon.Host,
            Text = withColon ? host.Name + ":" : host.Name,
            InsertText = withColon ? host.Name + ":" : host.Name,
            Description = host.Source
        }).ToList();
    }

    /// <summary>
    /// Tab while the list is open but nothing in it is selected (or it is still loading): complete the unique match or
    /// the common prefix, as Tab does in bash, now or as soon as the list has been loaded.
    /// </summary>
    public void ApplyOnTab()
    {
        if (_disposed)
        {
            return;
        }
        _autoApply = true;
        List<CompletionItem> items = _items;
        if (items != null && !_isLoading)
        {
            AutoApply(items);
        }
    }

    private void OnItemsLoaded(List<CompletionItem> list)
    {
        _items = list;
        UpdatePopup(null);
        if (_autoApply)
        {
            AutoApply(list);
        }
    }

    private void AutoApply(List<CompletionItem> list)
    {
        string filterText = GetFilterText(_token.Text);
        CompletionItem[] matches = list.Where((CompletionItem completionItem) => completionItem.Text.StartsWith(filterText)).ToArray();
        if (matches.Length == 1)
        {
            // The only candidate is inserted and the list is not needed any more, as in bash.
            ApplyItem(matches[0], commit: false);
            _popup.Close();
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
                // The directory part of the word stays: "src/ma" + Tab used to become "main." instead of "src/main.".
                string prefix = (_listing.HasPrefix && !_showingOptions && !CompletesName) ? _listing.Prefix : "";
                ApplyItem(new CompletionItem
                {
                    Text = common,
                    InsertText = prefix + common
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

    // The literal path (not yet quoted for the shell) that replaces the word.
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
        if (completionItem.InsertText == null || _disposed)
        {
            return;
        }
        try
        {
            string literal = completionItem.InsertText;
            if (CompletesName && !_showingOptions)
            {
                ApplyName(completionItem, literal, commit, suffix);
                return;
            }
            bool isDir = literal.EndsWith("/");
            if (isDir)
            {
                if (suffix == "/")
                {
                    suffix = "";
                }
                else if (commit && literal.Length > 1)
                {
                    literal = literal.TrimEnd('/');
                }
            }
            char quote = QuoteChar;
            string insertText;
            int caretIndex;
            if (quote != '\0')
            {
                // A quoted word stays quoted; the caret stays before the closing quote so that a directory can be
                // completed further. "~/" must stay outside the quotes to be expanded.
                string home = "";
                if (literal.StartsWith("~/", StringComparison.Ordinal))
                {
                    home = "~/";
                    literal = literal.Substring(2);
                }
                insertText = home + quote + ShellCommandLine.EscapeInQuotes(literal + suffix, quote) + quote;
                caretIndex = _token.Start + insertText.Length - 1;
            }
            else
            {
                // Every character the shell would interpret is escaped, not just spaces: file names with quotes, $,
                // &, parentheses and the like used to produce a broken command.
                insertText = ShellCommandLine.EscapeWord(literal) + suffix;
                caretIndex = _token.Start + insertText.Length;
            }
            // The typed character (suffix) goes right after the completed word, not to the end of the line.
            string newText = _commandText.Substring(0, _token.Start) + insertText + _commandText.Substring(_token.End);
            AppliedEventArgs appliedEventArgs = new AppliedEventArgs
            {
                NewText = newText,
                CaretIndex = caretIndex,
                Commit = commit,
                Item = completionItem,
                IsDirectory = isDir
            };
            LastApplied = appliedEventArgs;
            this.ItemApplied?.Invoke(this, appliedEventArgs);
            if (!_disposed && !commit && _popup.IsOpen)
            {
                // The terminal does not report its own replacement back (it used to end the session, so the list
                // vanished after Tab completed a common prefix of a word that was not the first one).
                _commandText = newText;
                _token = new Token(newText, _token.Start, _token.Start + insertText.Length);
                Retarget(_token.Text);
            }
        }
        catch
        {
        }
    }

    // "$" + name (no quoting: it is a reference, not a literal) or "user@" + host.
    private void ApplyName(CompletionItem completionItem, string name, bool commit, string suffix)
    {
        string word = ShellCommandLine.Unquote(_token.Text ?? "");
        string insertText = _target == CompletionTarget.Variable
            ? _insertPrefix + name + _insertSuffix
            : ShellCommandLine.EscapeWord(word.Substring(0, word.LastIndexOf('@') + 1) + name);
        insertText += (suffix == "/" && _target == CompletionTarget.Variable) ? "/" : suffix;
        string newText = _commandText.Substring(0, _token.Start) + insertText + _commandText.Substring(_token.End);
        AppliedEventArgs appliedEventArgs = new AppliedEventArgs
        {
            NewText = newText,
            CaretIndex = _token.Start + insertText.Length,
            Commit = commit,
            Item = completionItem,
            IsDirectory = false
        };
        LastApplied = appliedEventArgs;
        ItemApplied?.Invoke(this, appliedEventArgs);
        if (!_disposed && !commit && _popup.IsOpen)
        {
            _commandText = newText;
            _token = new Token(newText, _token.Start, _token.Start + insertText.Length);
            UpdatePopup(_token.Text);
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

    public bool OnTextChanged(string newFullText, int offset, int oldLength, int newLength)
    {
        int endOffset = offset + oldLength;
        if (offset >= _token.Start && offset <= _token.End && endOffset <= _token.End)
        {
            int lengthDiff = newLength - oldLength;
            _token.Length += lengthDiff;
            _commandText = newFullText;
            string extracted = _token.Extract(newFullText) ?? "";
            // The word as typed so far: the filter of a later Tab and the quote of the word are taken from it.
            _token.Text = extracted;
            if (_target == CompletionTarget.Variable)
            {
                // Still "$NAME" or "${NAME": keep filtering; anything else ends the completion.
                string name = GetFilterText(extracted);
                bool stillVariable = extracted.StartsWith(_insertPrefix, StringComparison.Ordinal) && name.All((char ch) => ch < 128 && (char.IsLetterOrDigit(ch) || ch == '_'));
                if (!stillVariable)
                {
                    return true;
                }
                UpdatePopup(extracted);
                return false;
            }
            if (extracted.IndexOfAny(new char[2] { '`', '$' }) != -1)
            {
                return true;
            }
            // The word ended (an unquoted space) or an operator was typed: the completion is over.
            List<ShellCommandLine.Token> words = ShellCommandLine.Tokenize(extracted);
            if (words.Count > 1 || (words.Count == 1 && (words[0].IsOperator || words[0].Start != 0 || words[0].End != extracted.Length)) || (words.Count == 0 && extracted.Length != 0))
            {
                return true;
            }
            // Typing after "-" used to close the popup right away, so options could never be filtered.
            if (extracted.StartsWith("-") && _commandCompletion?.Options == null)
            {
                return true;
            }
            Retarget(extracted);
            return false;
        }
        return true;
    }

    // Lists another directory if the word now points into one and filters the popup by the word.
    private void Retarget(string word)
    {
        if (CompletesName)
        {
            UpdatePopup(word);
            return;
        }
        ListingTarget listingTarget = ResolveListing(word);
        lock (_lock)
        {
            if (!listingTarget.Equals(_listing))
            {
                _listing = listingTarget;
                if (!_isLoading)
                {
                    _isLoading = true;
                    new Thread(LoadItems)
                    {
                        IsBackground = true
                    }.Start();
                }
            }
        }
        UpdatePopup(word);
    }

    public void Dispose()
    {
        _disposed = true;
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
        _popup.Close();
        ApplyItem(selectedItem, commit: false, new string(ch, 1));
        return true;
    }
}
