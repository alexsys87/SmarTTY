using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace SmarTTY.Transport;

/// <summary>
/// A file or folder of the server in the tree of Tools > Backup to tar.gz, with a check box of three states: checked (archived
/// with everything in it), unchecked, or partly checked (null: only the checked entries inside it are archived). The children of a
/// folder are read when it is first expanded; until then the folder has one placeholder child, so that the tree shows the arrow.
/// Pure logic without WPF or SSH, tested by tests/RemoteBackupTests.
/// </summary>
public sealed class BackupNode : INotifyPropertyChanged
{
    private bool? _IsChecked;

    private string _Note;

    private bool _IsLoading;

    private bool _IsExpanded;

    public string Name { get; }

    /// <summary>The absolute path on the server ("/" for the root).</summary>
    public string FullPath { get; }

    public RemoteEntryKind Kind { get; }

    public BackupNode Parent { get; }

    /// <summary>A virtual file system of the kernel (proc, sysfs): the check box is disabled and the entry is never archived.</summary>
    public bool IsLocked { get; }

    public bool IsEnabled => !IsLocked;

    /// <summary>Shows a check box: every entry but the placeholder.</summary>
    public bool IsCheckable => !IsPlaceholder;

    /// <summary>The "Loading..." entry of a folder whose children have not been read yet.</summary>
    public bool IsPlaceholder { get; }

    public bool IsRoot => Parent == null && !IsPlaceholder;

    public bool IsDirectory => Kind == RemoteEntryKind.Directory;

    /// <summary>The children have been read (or the entry has none to read: a file or a link).</summary>
    public bool ChildrenLoaded { get; private set; }

    public ObservableCollection<BackupNode> Children { get; } = new ObservableCollection<BackupNode>();

    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>
    /// Set on the root by the window: called when a folder of its tree is expanded for the first time, to read the children
    /// from the server and hand them to <see cref="ApplyChildren"/>.
    /// </summary>
    public Action<BackupNode> LoadHandler { get; set; }

    private BackupNode Root => Parent == null ? this : Parent.Root;

    /// <summary>Raised on the root whenever a check box of the tree is changed (by the user or by a quick selection).</summary>
    public event Action SelectionChanged;

    /// <summary>How the backup treats the entry by default (from its mount point or its name); see <see cref="RemoteBackup.Classify"/>.</summary>
    public BackupMountRole DefaultRole { get; private set; }

    private BackupNode(string name, string fullPath, RemoteEntryKind kind, BackupNode parent, bool isLocked, bool isPlaceholder, bool isChecked)
    {
        Name = name;
        FullPath = fullPath;
        Kind = kind;
        Parent = parent;
        IsLocked = isLocked;
        IsPlaceholder = isPlaceholder;
        _IsChecked = !isLocked && isChecked;
        if (kind == RemoteEntryKind.Directory && !isPlaceholder && !isLocked)
        {
            Children.Add(new BackupNode("Loading...", fullPath, RemoteEntryKind.File, this, false, true, false));
        }
        else
        {
            // A file, a link or a locked folder: there is nothing inside to choose from.
            ChildrenLoaded = true;
        }
    }

    /// <summary>The root of the tree: "/" itself, whose children are the top-level folders.</summary>
    public static BackupNode CreateRoot()
    {
        return new BackupNode("/", "/", RemoteEntryKind.Directory, null, false, false, true);
    }

    /// <summary>Text after the name: the file system mounted there, or why the entry is not selected by default.</summary>
    public string Note
    {
        get => _Note;
        set
        {
            _Note = value;
            Raise(nameof(Note));
            Raise(nameof(HasNote));
        }
    }

    public bool HasNote => !string.IsNullOrEmpty(_Note);

    /// <summary>The children are being read from the server.</summary>
    public bool IsLoading
    {
        get => _IsLoading;
        set
        {
            _IsLoading = value;
            Raise(nameof(IsLoading));
        }
    }

    /// <summary>Bound to the tree item: the first expansion of a folder asks for its children (<see cref="LoadHandler"/>).</summary>
    public bool IsExpanded
    {
        get => _IsExpanded;
        set
        {
            if (_IsExpanded == value)
            {
                return;
            }
            _IsExpanded = value;
            Raise(nameof(IsExpanded));
            if (value && !ChildrenLoaded && !IsLoading)
            {
                Root.LoadHandler?.Invoke(this);
            }
        }
    }

    /// <summary>
    /// The check box: setting it checks or unchecks everything inside the folder (the locked entries stay unchecked) and updates
    /// the folders above it. Setting null (a click on a partly checked box in a two-state check box never does) checks it.
    /// </summary>
    public bool? IsChecked
    {
        get => _IsChecked;
        set => SetChecked(value ?? true);
    }

    public void SetChecked(bool value)
    {
        if (IsLocked || IsPlaceholder)
        {
            return;
        }
        SetDown(value);
        Parent?.UpdateFromChildren();
        Root.SelectionChanged?.Invoke();
    }

    private void SetDown(bool value)
    {
        if (IsLocked || IsPlaceholder)
        {
            return;
        }
        SetState(value);
        foreach (BackupNode child in Children)
        {
            child.SetDown(value);
        }
    }

    private void SetState(bool? value)
    {
        if (_IsChecked == value)
        {
            return;
        }
        _IsChecked = value;
        Raise(nameof(IsChecked));
    }

    /// <summary>Checked when every child that can be is, unchecked when none is, partly checked otherwise.</summary>
    private void UpdateFromChildren()
    {
        List<BackupNode> choosable = Children.Where(child => !child.IsPlaceholder && !child.IsLocked).ToList();
        if (choosable.Count > 0)
        {
            bool? state;
            if (choosable.All(child => child._IsChecked == true))
            {
                state = true;
            }
            else if (choosable.All(child => child._IsChecked == false))
            {
                state = false;
            }
            else
            {
                state = null;
            }
            SetState(state);
        }
        Parent?.UpdateFromChildren();
    }

    /// <summary>A child entry of this folder, as the server listed it. <see cref="ApplyChildren"/> makes them.</summary>
    public sealed class ChildSpec
    {
        public RemoteEntry Entry;

        public BackupMountRole Role;

        public string Note;
    }

    /// <summary>
    /// Puts the children read from the server in place of the placeholder. A child takes the state of this folder: checked inside a
    /// checked folder, unchecked inside an unchecked one. For a partly checked folder (the root, whose children are read first)
    /// a child is checked unless it is one that is skipped by default. A locked child is always unchecked.
    /// </summary>
    public void ApplyChildren(IEnumerable<ChildSpec> children)
    {
        Children.Clear();
        foreach (ChildSpec spec in children ?? Enumerable.Empty<ChildSpec>())
        {
            bool locked = spec.Role == BackupMountRole.NeverArchived;
            bool isChecked = _IsChecked == false ? false : _IsChecked == true && !IsRoot ? true : spec.Role == BackupMountRole.Ordinary;
            BackupNode child = new BackupNode(spec.Entry.Name, RemoteBackup.Combine(FullPath, spec.Entry.Name), spec.Entry.Kind, this, locked, false, isChecked)
            {
                _Note = spec.Note,
                DefaultRole = spec.Role
            };
            Children.Add(child);
        }
        ChildrenLoaded = true;
        IsLoading = false;
        UpdateFromChildren();
        Raise(nameof(Children));
    }

    /// <summary>The folder could not be read: no children to choose from, the folder itself can still be archived as a whole.</summary>
    public void SetLoadFailed(string reason)
    {
        Children.Clear();
        ChildrenLoaded = true;
        IsLoading = false;
        Note = reason;
    }

    /// <summary>The node of <paramref name="path"/> among the entries read so far, or null.</summary>
    public BackupNode Find(string path)
    {
        path = RemoteBackup.NormalizeDirectory(path);
        if (path == FullPath)
        {
            return this;
        }
        foreach (BackupNode child in Children)
        {
            if (!child.IsPlaceholder && (path == child.FullPath || path.StartsWith(child.FullPath + "/", StringComparison.Ordinal)))
            {
                return child.Find(path);
            }
        }
        return null;
    }

    /// <summary>A locked entry is somewhere among the children read so far.</summary>
    private bool ContainsLocked()
    {
        return Children.Any(child => !child.IsPlaceholder && (child.IsLocked || child.ContainsLocked()));
    }

    /// <summary>
    /// The paths to hand to tar: a checked entry as a whole, the checked entries inside a partly checked folder, nothing of an
    /// unchecked one. A checked folder that holds a locked entry (a virtual file system mounted deeper inside) is replaced by its
    /// children, so that tar never descends into it. The root is never a member itself: tar would take /proc along.
    /// </summary>
    public List<string> CollectMembers()
    {
        List<string> members = new List<string>();
        Collect(members);
        return members;
    }

    private void Collect(List<string> members)
    {
        if (IsPlaceholder || IsLocked || _IsChecked == false)
        {
            return;
        }
        if (_IsChecked == true && !IsRoot && !ContainsLocked())
        {
            members.Add(FullPath);
            return;
        }
        foreach (BackupNode child in Children)
        {
            child.Collect(members);
        }
    }

    /// <summary>
    /// The folders whose children must be read before the archive is made, so that the virtual file systems mounted at
    /// <paramref name="lockedPaths"/> can be left out: for each such path, the first folder on the way to it that is checked as a
    /// whole and not read yet. An unchecked folder on the way ends the walk, since nothing under it is archived anyway.
    /// The window reads them, applies the children and asks again until the list is empty.
    /// </summary>
    public List<BackupNode> FoldersToReadBeforeArchiving(IEnumerable<string> lockedPaths)
    {
        List<BackupNode> folders = new List<BackupNode>();
        foreach (string rawPath in lockedPaths ?? Enumerable.Empty<string>())
        {
            string path = RemoteBackup.NormalizeDirectory(rawPath);
            if (path == "/")
            {
                continue;
            }
            BackupNode node = this;
            while (node != null && node.FullPath != path)
            {
                if (node.IsLocked || node._IsChecked == false || node.IsPlaceholder)
                {
                    node = null;
                    break;
                }
                if (!node.ChildrenLoaded)
                {
                    if (node.IsDirectory && !folders.Contains(node))
                    {
                        folders.Add(node);
                    }
                    node = null;
                    break;
                }
                BackupNode next = null;
                foreach (BackupNode child in node.Children)
                {
                    if (!child.IsPlaceholder && (path == child.FullPath || path.StartsWith(child.FullPath + "/", StringComparison.Ordinal)))
                    {
                        next = child;
                        break;
                    }
                }
                node = next;
            }
        }
        return folders;
    }

    /// <summary>
    /// The selection of a backup of the whole system: every top-level entry that is part of the installed system as a whole, none of
    /// the ones that are skipped by default (/tmp, /dev, /mnt...).
    /// </summary>
    public void SelectWholeSystem()
    {
        foreach (BackupNode child in Children)
        {
            if (!child.IsPlaceholder && !child.IsLocked)
            {
                child.SetDown(child.DefaultRole == BackupMountRole.Ordinary);
            }
        }
        UpdateFromChildren();
        SelectionChanged?.Invoke();
    }

    /// <summary>
    /// Whether every top-level entry that <see cref="SelectWholeSystem"/> selects is checked as a whole (the name of the archive
    /// then says "rootfs"). Entries skipped by default may be checked as well.
    /// </summary>
    public bool IsWholeSystemSelected()
    {
        List<BackupNode> system = Children.Where(child => !child.IsPlaceholder && !child.IsLocked && child.DefaultRole == BackupMountRole.Ordinary).ToList();
        return system.Count > 0 && system.All(child => child._IsChecked == true);
    }

    private void Raise(string property)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    public override string ToString()
    {
        return FullPath + " [" + (_IsChecked == true ? "x" : _IsChecked == false ? " " : "-") + "]";
    }
}
