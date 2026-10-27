using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace SmarTTY.Controls;

/// <summary>
/// The file choosers of the controls that the Mono test harness compiles (<c>Controls/</c> and <c>VisualTTY/</c>).
/// Those cannot reference <c>GUI/</c>, so they cannot show the themed WPF dialog (<c>GUI/LocalFileDialogWindow</c>)
/// directly, and the WinForms <c>OpenFileDialog</c>/<c>SaveFileDialog</c> draw a plain window that ignores the
/// application theme. This facade keeps the call shape of the native dialogs and routes it to the themed dialog that
/// <c>App</c> injects through <see cref="ShowImpl"/>; without one it falls back to the WinForms dialogs, so the
/// controls still work on their own.
/// </summary>
internal static class ThemedFileDialog
{
    /// <summary>
    /// Shows the themed file dialog and returns what the user chose, or null when the window was cancelled. Injected
    /// by <c>App</c>.
    /// </summary>
    public static Func<ThemedFileDialogRequest, ThemedFileDialogResult> ShowImpl;

    /// <summary>
    /// Asks for a single existing file to open. Returns the chosen path, or null when the user cancelled.
    /// </summary>
    public static string OpenFile(IWin32Window owner, string title, string filter, int filterIndex = 1, string initialDirectory = null)
    {
        ThemedFileDialogResult result = Show(owner, new ThemedFileDialogRequest
        {
            Mode = ThemedFileDialogMode.OpenFile,
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
        ThemedFileDialogResult result = Show(owner, new ThemedFileDialogRequest
        {
            Mode = ThemedFileDialogMode.OpenFile,
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
    public static ThemedFileDialogResult SaveFileEx(IWin32Window owner, string title, string filter, int filterIndex = 1, string fileName = null, bool overwritePrompt = true, bool addExtension = false, string initialDirectory = null)
    {
        return Show(owner, new ThemedFileDialogRequest
        {
            Mode = ThemedFileDialogMode.SaveFile,
            Title = title,
            Filter = filter,
            FilterIndex = filterIndex,
            FileName = fileName,
            OverwritePrompt = overwritePrompt,
            AddExtension = addExtension,
            InitialDirectory = initialDirectory
        });
    }

    private static string FirstName(ThemedFileDialogResult result)
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

    private static ThemedFileDialogResult Show(IWin32Window owner, ThemedFileDialogRequest request)
    {
        request.OwnerHandle = owner?.Handle ?? IntPtr.Zero;
        Func<ThemedFileDialogRequest, ThemedFileDialogResult> show = ShowImpl;
        if (show != null)
        {
            return show(request);
        }
        return ShowNative(request);
    }

    /// <summary>
    /// The fallback used when no themed dialog is installed (the Mono harness): the WinForms dialog with the same
    /// settings.
    /// </summary>
    private static ThemedFileDialogResult ShowNative(ThemedFileDialogRequest request)
    {
        if (request.Mode == ThemedFileDialogMode.SaveFile)
        {
            using SaveFileDialog dialog = new SaveFileDialog
            {
                Title = request.Title,
                Filter = request.Filter,
                FilterIndex = request.FilterIndex,
                FileName = request.FileName ?? "",
                OverwritePrompt = request.OverwritePrompt,
                AddExtension = request.AddExtension,
                InitialDirectory = request.InitialDirectory ?? ""
            };
            return dialog.ShowDialog() == DialogResult.OK
                ? new ThemedFileDialogResult { FileName = dialog.FileName, FilterIndex = dialog.FilterIndex }
                : null;
        }
        using OpenFileDialog openDialog = new OpenFileDialog
        {
            Title = request.Title,
            Filter = request.Filter,
            FilterIndex = request.FilterIndex,
            Multiselect = request.Multiselect,
            InitialDirectory = request.InitialDirectory ?? ""
        };
        if (openDialog.ShowDialog() != DialogResult.OK)
        {
            return null;
        }
        ThemedFileDialogResult result = new ThemedFileDialogResult { FilterIndex = openDialog.FilterIndex };
        if (request.Multiselect)
        {
            result.FileNames.AddRange(openDialog.FileNames);
        }
        else
        {
            result.FileName = openDialog.FileName;
        }
        return result;
    }
}

/// <summary>Whether the themed file dialog opens files or chooses a name to save to.</summary>
internal enum ThemedFileDialogMode
{
    OpenFile,
    SaveFile
}

/// <summary>
/// What a caller asks the themed file dialog for. The fields mirror the settings of the WinForms
/// <c>OpenFileDialog</c>/<c>SaveFileDialog</c> that the callers used before.
/// </summary>
internal sealed class ThemedFileDialogRequest
{
    public ThemedFileDialogMode Mode { get; set; }

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

    /// <summary>The handle of the window that asked, for centering the dialog; set by the facade.</summary>
    public IntPtr OwnerHandle { get; set; }
}

/// <summary>What the user chose: a single path, or several in multiselect mode.</summary>
internal sealed class ThemedFileDialogResult
{
    public string FileName { get; set; }

    public List<string> FileNames { get; } = new List<string>();

    /// <summary>The 1-based index of the filter entry the user finally chose.</summary>
    public int FilterIndex { get; set; } = 1;
}
