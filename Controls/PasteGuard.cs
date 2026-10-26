using SmarTTY.Forms;
using SmarTTY.Terminal;
using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;
using VisualTTY;

namespace SmarTTY.Controls;

/// <summary>
/// What every terminal does with the text of the clipboard before it is pasted: cleaning (escape sequences, typographic
/// characters, a final line break), the confirmation of a paste that runs several lines at once, is large or holds
/// dangerous commands, and the choice of the line break. The text it returns has LF line breaks; the terminal turns them
/// into what it sends (see <see cref="ResolveLineBreak"/>).
/// </summary>
internal static class PasteGuard
{
    /// <summary>The settings in use; the defaults when the control has none (a control used on its own).</summary>
    public static PasteSettings OptionsOf(GlobalSettings settings)
    {
        return settings?.Paste ?? new PasteSettings();
    }

    /// <summary>The line break to send for the pasted one: the one chosen in the settings, otherwise <paramref name="automatic"/>.</summary>
    public static string ResolveLineBreak(GlobalSettings settings, string automatic)
    {
        return OptionsOf(settings).ResolveLineBreak(automatic);
    }

    /// <summary>The text cleaned as the settings say, with LF line breaks.</summary>
    public static string Clean(string text, PasteSettings options)
    {
        text = PasteSanitizer.NormalizeLineBreaks(text);
        if (options.RemoveControlCharacters)
        {
            text = PasteSanitizer.RemoveControlCharacters(text);
        }
        if (options.ReplaceTypographicCharacters)
        {
            text = PasteSanitizer.ReplaceTypographicCharacters(text);
        }
        if (options.TrimTrailingNewline)
        {
            text = PasteSanitizer.TrimTrailingLineBreaks(text);
        }
        return text;
    }

    /// <summary>
    /// Cleans the clipboard text and asks the user when needed. Returns the text to paste (LF line breaks), or null when
    /// there is nothing to paste or the user cancelled. <paramref name="linesRunAtOnce"/> tells that the line breaks of
    /// the text start the commands at once (a shell that does not use bracketed paste, the line editor of a COM or TCP
    /// terminal); where the text only lands in an editable line it is false and only the size is checked.
    /// </summary>
    public static string Prepare(string clipboardText, GlobalSettings settings, bool linesRunAtOnce, IWin32Window owner)
    {
        if (string.IsNullOrEmpty(clipboardText))
        {
            return null;
        }
        PasteSettings options = OptionsOf(settings);
        string text = Clean(clipboardText, options);
        if (text.Length == 0)
        {
            return null;
        }
        return Confirm(text, options, settings, linesRunAtOnce, owner) ? text : null;
    }

    private static bool Confirm(string text, PasteSettings options, GlobalSettings settings, bool linesRunAtOnce, IWin32Window owner)
    {
        int lineBreaks = PasteSanitizer.CountLineBreaks(text);
        int lines = PasteSanitizer.CountLines(text);
        bool endsWithLineBreak = text[text.Length - 1] == '\n';
        bool runs = linesRunAtOnce && lineBreaks > 0;
        List<string> dangers = new List<string>();
        if (options.WarnDangerous)
        {
            if (runs)
            {
                dangers = PasteSanitizer.FindDangerous(text);
            }
            else if (PasteSanitizer.HasControlCharacters(text))
            {
                dangers.Add(PasteSanitizer.ControlCharactersWarning);
            }
        }
        bool multiline = options.ConfirmMultiline && runs && (lines >= Math.Max(options.ConfirmLineThreshold, 1) || endsWithLineBreak);
        bool large = options.ConfirmSizeThreshold > 0 && text.Length >= options.ConfirmSizeThreshold;
        if (!multiline && !large && dangers.Count == 0)
        {
            return true;
        }
        StringBuilder summary = new StringBuilder();
        summary.Append(lines).Append(lines == 1 ? " line, " : " lines, ").Append(text.Length).Append(" characters.");
        if (runs)
        {
            summary.Append(endsWithLineBreak
                ? " The text ends with a line break: the last line runs as soon as it is pasted, without Enter."
                : " The line breaks run the lines one after another as soon as they are pasted.");
        }
        if (large)
        {
            summary.Append(" The text is large.");
        }
        using PasteConfirmForm form = new PasteConfirmForm(summary.ToString(), PasteSanitizer.BuildPreview(text, 30, 300), dangers, multiline);
        DialogResult result = owner != null ? form.ShowDialog(owner) : form.ShowDialog();
        if (result != DialogResult.OK)
        {
            return false;
        }
        if (multiline && form.DontAskAgain)
        {
            options.ConfirmMultiline = false;
            settings?.Save();
        }
        return true;
    }
}
