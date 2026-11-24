using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace SmarTTY.Controls;

/// <summary>
/// The message boxes, file choosers and folder chooser of the code that the Mono test harness compiles (<c>Controls/</c>,
/// <c>Ssh/</c>, <c>VisualTTY/</c>) and of the rest of the application. That code cannot reference <c>GUI/</c>, so it asks
/// through these hooks, which <c>App</c> points at the WPF windows (<c>GUI/MessageDialog</c>, <c>GUI/LocalFileDialogWindow</c>,
/// <c>GUI/LocalFolderBrowserWindow</c>) and the harness at its own answers. The vocabulary is the application's
/// (<see cref="DialogButtons"/>, <see cref="DialogIcon"/>, <see cref="DialogAnswer"/>), not that of the WinForms boxes. Since stage 4
/// there is no WinForms box behind them: a question asked with no hook installed is an error.
/// </summary>
internal static class UserDialogs
{
    /// <summary>Shows a message box and returns the button chosen. Set by <c>App</c> (<c>MessageDialog.Ask</c>).</summary>
    public static Func<MessageRequest, DialogAnswer> MessageImpl;

    /// <summary>Shows a file chooser and returns what was chosen, or null when it was cancelled. Set by <c>App</c>.</summary>
    public static Func<FileDialogRequest, FileDialogResult> FileImpl;

    /// <summary>Shows the folder chooser: (description, initial path) -> the folder, or null when it was cancelled. Set by <c>App</c>.</summary>
    public static Func<string, string, string> FolderImpl;

    /// <summary>Asks the user (owned by the window the user is working in) and returns the button chosen.</summary>
    public static DialogAnswer Show(string text, string caption, DialogButtons buttons, DialogIcon icon)
    {
        return Show(null, text, caption, buttons, icon);
    }

    /// <summary>Asks the user in a box owned by <paramref name="owner"/> (null: the window the user is working in).</summary>
    public static DialogAnswer Show(IWin32Window owner, string text, string caption, DialogButtons buttons, DialogIcon icon)
    {
        return Installed(MessageImpl)(new MessageRequest
        {
            Text = text,
            Caption = caption,
            Buttons = buttons,
            Icon = icon,
            OwnerHandle = owner?.Handle ?? IntPtr.Zero
        });
    }

    /// <summary>
    /// Asks for a single existing file to open. Returns the chosen path, or null when the user cancelled.
    /// </summary>
    public static string OpenFile(IWin32Window owner, string title, string filter, int filterIndex = 1, string initialDirectory = null)
    {
        FileDialogResult result = ShowFileDialog(owner, new FileDialogRequest
        {
            Mode = FileDialogMode.OpenFile,
            Title = title,
            Filter = filter,
            FilterIndex = filterIndex,
            InitialDirectory = initialDirectory
        });
        return FirstName(result);
    }

    /// <summary>
    /// Asks for one or more existing files to open. Returns the chosen paths (never empty), or null when cancelled.
    /// </summary>
    public static string[] OpenFiles(IWin32Window owner, string title, string filter, int filterIndex = 1, string initialDirectory = null)
    {
        FileDialogResult result = ShowFileDialog(owner, new FileDialogRequest
        {
            Mode = FileDialogMode.OpenFile,
            Multiselect = true,
            Title = title,
            Filter = filter,
            FilterIndex = filterIndex,
            InitialDirectory = initialDirectory
        });
        if (result == null || result.FileNames == null || result.FileNames.Count == 0)
        {
            return null;
        }
        return result.FileNames.ToArray();
    }

    /// <summary>
    /// Asks for the destination file to save to. Returns the chosen path, or null when the user cancelled.
    /// </summary>
    public static string SaveFile(IWin32Window owner, string title, string filter, int filterIndex = 1, string fileName = null, bool overwritePrompt = true, bool addExtension = false, string initialDirectory = null)
    {
        return SaveFileEx(owner, title, filter, filterIndex, fileName, overwritePrompt, addExtension, initialDirectory)?.FileName;
    }

    /// <summary>
    /// Like <see cref="SaveFile"/>, but also reports the filter entry the user finally chose (the caller may pick the
    /// file format from it). Returns null when the user cancelled.
    /// </summary>
    public static FileDialogResult SaveFileEx(IWin32Window owner, string title, string filter, int filterIndex = 1, string fileName = null, bool overwritePrompt = true, bool addExtension = false, string initialDirectory = null)
    {
        return ShowFileDialog(owner, new FileDialogRequest
        {
            Mode = FileDialogMode.SaveFile,
            Title = title,
            Filter = filter,
            FilterIndex = filterIndex,
            FileName = fileName,
            OverwritePrompt = overwritePrompt,
            AddExtension = addExtension,
            InitialDirectory = initialDirectory
        });
    }

    /// <summary>
    /// Asks for a folder. <paramref name="owner"/> is not used: the folder chooser is owned by the window the user is working in.
    /// Returns the chosen path, or null when the user cancelled.
    /// </summary>
    public static string Browse(IWin32Window owner, string description, string initialPath = null)
    {
        return Installed(FolderImpl)(description, initialPath);
    }

    private static string FirstName(FileDialogResult result)
    {
        if (result == null)
        {
            return null;
        }
        if (!string.IsNullOrEmpty(result.FileName))
        {
            return result.FileName;
        }
        return (result.FileNames != null && result.FileNames.Count > 0) ? result.FileNames[0] : null;
    }

    private static FileDialogResult ShowFileDialog(IWin32Window owner, FileDialogRequest request)
    {
        request.OwnerHandle = owner?.Handle ?? IntPtr.Zero;
        return Installed(FileImpl)(request);
    }

    private static T Installed<T>(T hook) where T : class
    {
        return hook ?? throw new InvalidOperationException("No dialog is installed (App sets UserDialogs at startup).");
    }
}

/// <summary>Which buttons a message box offers.</summary>
internal enum DialogButtons
{
    Ok,
    OkCancel,
    YesNo,
    YesNoCancel
}

/// <summary>The picture shown next to the text of a message box.</summary>
internal enum DialogIcon
{
    Error,
    Warning,
    Question,
    Information
}

/// <summary>The button the user chose; a box closed without one is <see cref="Cancel"/>.</summary>
internal enum DialogAnswer
{
    Ok,
    Cancel,
    Yes,
    No
}

/// <summary>A message box to show.</summary>
internal sealed class MessageRequest
{
    public string Text { get; set; }

    /// <summary>The window caption; empty means "SmarTTY".</summary>
    public string Caption { get; set; }

    public DialogButtons Buttons { get; set; }

    public DialogIcon Icon { get; set; }

    /// <summary>The handle of the window that asked, so the box is owned by it; zero for the window the user is working in.</summary>
    public IntPtr OwnerHandle { get; set; }
}

/// <summary>Whether the file dialog opens files or chooses a name to save to.</summary>
internal enum FileDialogMode
{
    OpenFile,
    SaveFile
}

/// <summary>
/// What a caller asks the file dialog for. The fields mirror the settings of the WinForms
/// <c>OpenFileDialog</c>/<c>SaveFileDialog</c> that the callers used before.
/// </summary>
internal sealed class FileDialogRequest
{
    public FileDialogMode Mode { get; set; }

    /// <summary>The window caption.</summary>
    public string Title { get; set; }

    /// <summary>The filter string ("All files (*.*)|*.*|Text (*.txt)|*.txt").</summary>
    public string Filter { get; set; }

    /// <summary>The 1-based index of the filter entry to preselect.</summary>
    public int FilterIndex { get; set; } = 1;

    /// <summary>Open mode: allow more than one file to be chosen.</summary>
    public bool Multiselect { get; set; }

    /// <summary>Save mode: the suggested file name.</summary>
    public string FileName { get; set; }

    /// <summary>Save mode: ask before overwriting an existing file.</summary>
    public bool OverwritePrompt { get; set; } = true;

    /// <summary>Save mode: append the extension of the selected filter entry when the name has none.</summary>
    public bool AddExtension { get; set; }

    /// <summary>The folder to open in; null uses the folder of <see cref="FileName"/>, or Documents.</summary>
    public string InitialDirectory { get; set; }

    /// <summary>The handle of the window that asked, for centering the dialog; set by <see cref="UserDialogs"/>.</summary>
    public IntPtr OwnerHandle { get; set; }
}

/// <summary>What the user chose: a single path, or several in multiselect mode.</summary>
internal sealed class FileDialogResult
{
    public string FileName { get; set; }

    public List<string> FileNames { get; } = new List<string>();

    /// <summary>The 1-based index of the filter entry the user finally chose.</summary>
    public int FilterIndex { get; set; } = 1;
}
