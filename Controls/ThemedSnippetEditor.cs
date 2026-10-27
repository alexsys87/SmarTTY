using System;
using System.Windows.Forms;

namespace SmarTTY.Controls;

/// <summary>
/// The snippet editor of the controls that the Mono test harness compiles (<c>Controls/</c>). Those cannot reference
/// <c>GUI/</c>, so they cannot show the themed WPF editor (<c>GUI/SnippetEditWindow</c>) directly, and the WinForms
/// <c>Forms/SnippetEditForm</c> draws a plain window that ignores the application theme. This facade routes the edit
/// to the themed window that <c>App</c> injects through <see cref="EditImpl"/>; without one it falls back to the
/// WinForms form, so the controls still work on their own.
/// </summary>
internal static class ThemedSnippetEditor
{
    /// <summary>
    /// Shows the themed snippet editor. Injected by <c>App</c>: (name, text, canDelete, owner handle) -> the result.
    /// </summary>
    public static Func<string, string, bool, IntPtr, ThemedSnippetEditorResult> EditImpl;

    /// <summary>
    /// Asks for a snippet's name and text. <paramref name="canDelete"/> adds the Delete button. Returns the result,
    /// or null when the user cancelled.
    /// </summary>
    public static ThemedSnippetEditorResult Edit(IWin32Window owner, string name, string text, bool canDelete)
    {
        Func<string, string, bool, IntPtr, ThemedSnippetEditorResult> edit = EditImpl;
        if (edit != null)
        {
            return edit(name, text, canDelete, owner?.Handle ?? IntPtr.Zero);
        }
        return EditNative(owner, name, text, canDelete);
    }

    /// <summary>
    /// The fallback used when no themed editor is installed (the Mono harness): the WinForms form.
    /// </summary>
    private static ThemedSnippetEditorResult EditNative(IWin32Window owner, string name, string text, bool canDelete)
    {
        using SmarTTY.Forms.SnippetEditForm form = new SmarTTY.Forms.SnippetEditForm(name, text, canDelete);
        System.Windows.Forms.DialogResult result = owner != null ? form.ShowDialog(owner) : form.ShowDialog();
        if (result == System.Windows.Forms.DialogResult.Cancel)
        {
            return null;
        }
        return new ThemedSnippetEditorResult
        {
            Name = form.SnippetName,
            Text = form.SnippetText,
            Delete = result == System.Windows.Forms.DialogResult.Abort
        };
    }
}

/// <summary>What the user did in the snippet editor: saved the name and text, or asked to delete the snippet.</summary>
internal sealed class ThemedSnippetEditorResult
{
    /// <summary>The name, trimmed; empty means "use the text as the menu label".</summary>
    public string Name { get; set; }

    /// <summary>The text to paste.</summary>
    public string Text { get; set; }

    /// <summary>True when the Delete button was pressed.</summary>
    public bool Delete { get; set; }
}
