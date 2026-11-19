using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Linq;
using VisualTTY;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.Terminal;

/// <summary>
/// Lists <c>directory</c> (the directory part of the word, as typed; empty for the shell's own directory) on the device and calls
/// <c>done</c> on the UI thread with the names, or with null when the device did not answer. False when no listing could be started.
/// </summary>
public delegate bool DeviceDirectoryListerHandler(string directory, Action<IReadOnlyList<DeviceDirectoryEntry>> done);

/// <summary>
/// What <see cref="LocalCompletionSession"/> needs of the terminal: the commands it suggests from the history and the lists, running a piece of work on the
/// thread of the window, and the listing of a directory of the device with the question whether one can be started now. Every member is something
/// the session asked the control for.
/// </summary>
internal interface ILocalCompletionHost
{
    /// <summary>The user's own commands and the saved history, as items of the list.</summary>
    IEnumerable<CompletionItem> GetSuggestedCommandItems();

    /// <summary>Runs <paramref name="action"/> later on the thread of the window (BeginInvoke).</summary>
    void Post(Action action);

    /// <summary>Set by the device terminal; null when there is none.</summary>
    DeviceDirectoryListerHandler DeviceDirectoryLister { get; }

    /// <summary>The shell of the device can be asked for a listing now.</summary>
    bool CanListDeviceDirectory { get; }
}

internal sealed class LocalCompletionSession : IDisposable
{
    private readonly ILocalCompletionHost _host;
    private readonly CompletionTarget _target;
    private string _commandText;
    private int _tokenStart;
    private int _tokenEnd;
    private readonly ICompletionPopup _popup;
    private readonly bool _autoApply;
    private List<CompletionItem> _items;
    // Directory whose children _items was last listed from ("" for command targets).
    private string _itemsDirectory = "";

    public event EventHandler<CompletionSession.AppliedEventArgs> ItemApplied;

    public LocalCompletionSession(ILocalCompletionHost host, string commandText, CompletionTarget target, int tokenStart, int tokenEnd, ICompletionPopup popup, bool autoApply)
    {
        _host = host;
        _target = target;
        _commandText = commandText;
        _tokenStart = tokenStart;
        _tokenEnd = tokenEnd;
        _popup = popup;
        _autoApply = autoApply;
        _popup.ItemSelected += OnItemSelected;
        LoadItems();
        UpdatePopup();
    }

    private string GetTokenText()
    {
        if (_tokenStart >= 0 && _tokenEnd <= _commandText.Length && _tokenStart <= _tokenEnd)
        {
            return _commandText.Substring(_tokenStart, _tokenEnd - _tokenStart);
        }
        return "";
    }

    private void LoadItems()
    {
        try
        {
            List<CompletionItem> itemsList = new List<CompletionItem>();
            string tokenText = GetTokenText();
            string filterText = tokenText.Trim('\'', '"');

            if (_target == CompletionTarget.Command)
            {
                // The user's own commands and the saved history, matched by what is already typed.
                itemsList.AddRange(_host.GetSuggestedCommandItems());
            }

            if (_target == CompletionTarget.Command && string.IsNullOrEmpty(filterText))
            {
                // Built-in local commands
                _itemsDirectory = "";
                itemsList.AddRange(LocalCompletionItems.GetBuiltInCommands());
                // Local executables from PATH
                itemsList.AddRange(LocalCompletionItems.GetLocalExecutables(filterText));
            }
            else if (_target == CompletionTarget.Path || _target == CompletionTarget.Directory)
            {
                // The files are on the device: its shell lists them (the folder of this program used to be offered).
                _itemsDirectory = LocalCompletionItems.GetDeviceDirectoryPrefix(tokenText);
                _items = itemsList;
                RequestDeviceListing(_itemsDirectory);
                return;
            }

            _items = itemsList;
            _host.Post(() => OnItemsLoaded());
        }
        catch (Exception ex)
        {
            _popup.DisplayException(ex);
        }
    }

    // Live-sync: called by the terminal after every text edit that lands
    // inside this session's token. Keeps the token text, the item listing
    // and the popup filter consistent with what the user is typing.
    // Returns true when the edit invalidated the completion token (the
    // session must be closed, e.g. the token turned into a region of shell
    // metacharacters or an unescaped space that listing cannot represent).
    public bool OnTextChanged(string newFullText, int offset, int oldLength, int newLength)
    {
        if (_popup == null)
        {
            return true;
        }

        int endOffset = offset + oldLength;
        if (offset < _tokenStart || offset > _tokenEnd || endOffset > _tokenEnd)
        {
            // Edit happened outside the completed token - the session is stale.
            return true;
        }

        _tokenEnd += newLength - oldLength;
        _commandText = newFullText;

        string extracted = GetTokenText();
        if (string.IsNullOrEmpty(extracted))
        {
            UpdatePopup();
            return false;
        }

        if (extracted.IndexOfAny(LocalCompletionItems.CompletionMetaChars) != -1)
        {
            return true;
        }
        if (LocalCompletionItems.ContainsUnescapedSpace(extracted) && extracted[0] != '\'' && extracted[0] != '"')
        {
            return true;
        }

        // For path/directory targets the listing depends on the directory the
        // token points into; when it changed we must re-enumerate.
        if (_target == CompletionTarget.Path || _target == CompletionTarget.Directory)
        {
            string directory = LocalCompletionItems.GetDeviceDirectoryPrefix(extracted);
            if (!string.Equals(directory, _itemsDirectory, StringComparison.Ordinal))
            {
                LoadItems();
            }
        }

        UpdatePopup();
        return false;
    }

    // The prefix of the names that matches the word: what follows its last slash.
    private string GetItemFilter()
    {
        string tokenText = GetTokenText();
        if (_target == CompletionTarget.Path || _target == CompletionTarget.Directory)
        {
            string path = ShellCommandLine.Unquote(tokenText);
            return path.Substring(path.LastIndexOf('/') + 1);
        }
        return tokenText.Trim('\'', '"');
    }

    // Each listing request gets a number: the answer to an older one (the word moved to another directory
    // meanwhile) is dropped.
    private int _listingGeneration;
    private bool _disposed;

    private void RequestDeviceListing(string directory)
    {
        int generation = ++_listingGeneration;
        bool directoriesOnly = _target == CompletionTarget.Directory;
        DeviceDirectoryListerHandler lister = _host.DeviceDirectoryLister;
        bool started = lister != null && _host.CanListDeviceDirectory && lister(directory, entries =>
        {
            if (_disposed || generation != _listingGeneration)
            {
                return;
            }
            _items = LocalCompletionItems.BuildPathItems(directory, entries, directoriesOnly);
            OnItemsLoaded();
        });
        if (!started)
        {
            // No shell to ask (a bootloader, a password prompt, characters echoed to the device as they are typed):
            // nothing is offered rather than files of this computer.
            _items = new List<CompletionItem>();
            _host.Post(() =>
            {
                if (!_disposed && generation == _listingGeneration)
                {
                    UpdatePopup();
                }
            });
        }
    }

    private void OnItemsLoaded()
    {
        UpdatePopup();
        if (_autoApply)
        {
            ApplyBestMatch();
        }
    }

    private void UpdatePopup()
    {
        if (_items == null || _disposed) return;

        string filter = GetItemFilter();

        var filtered = _items.Where(item => item.Text.StartsWith(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
        _popup.SetItemList(filtered);
        _popup.SetFilter(filter);
    }

    private void ApplyBestMatch()
    {
        if (_items == null) return;
        string filter = GetItemFilter();

        var matches = _items.Where(item => item.Text.StartsWith(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 1)
        {
            ApplyItem(matches[0], commit: false);
        }
        else if (matches.Length > 1)
        {
            string common = matches[0].Text;
            foreach (var item in matches.Skip(1))
            {
                common = LocalCompletionItems.CommonPrefix(common, item.Text);
            }
            if (common.Length > 0 && common != filter)
            {
                // A path keeps the directory part of the word in front of the common start of the names.
                string insertText = (_target == CompletionTarget.Path || _target == CompletionTarget.Directory)
                    ? ShellCommandLine.EscapeWord(_itemsDirectory + common)
                    : common;
                ApplyItem(new CompletionItem { Text = common, InsertText = insertText }, commit: false);
            }
        }
    }

    private void OnItemSelected(CompletionItem item, VirtualKey keys)
    {
        ApplyItem(item, keys == VirtualKey.Return);
    }

    private void ApplyItem(CompletionItem item, bool commit, string suffix = "")
    {
        if (item.InsertText == null) return;

        string insertText = item.InsertText;
        // Paths are the device's: its separator is "/" (the one of Windows used to be put in).
        bool isDir = insertText.EndsWith("/", StringComparison.Ordinal);

        if (isDir)
        {
            if (suffix == "/") suffix = "";
            else if (commit) insertText = insertText.TrimEnd('/');
        }

        string newText = _commandText.Substring(0, _tokenStart) + insertText + _commandText.Substring(_tokenEnd) + suffix;
        int caretOffset = 0;

        var args = new CompletionSession.AppliedEventArgs
        {
            NewText = newText,
            CaretIndex = _tokenStart + insertText.Length + (suffix ?? "").Length + caretOffset,
            Commit = commit,
            Item = item,
            IsDirectory = isDir
        };
        ItemApplied?.Invoke(this, args);
    }

    public void Dispose()
    {
        _disposed = true;
        _popup?.Close();
    }
}
