using System;
using System.Windows.Forms;

namespace SmarTTY.Controls;

/// <summary>
/// The small window of a paste that takes time (a large paste, a slow paste, a paste line by line): what is done so
/// far, a progress bar and Cancel. It does not take the keyboard focus, so the terminal keeps getting the keys (Esc
/// cancels the paste there). The controls that the Mono test harness compiles cannot reference <c>GUI/</c>, so they
/// create the window through <see cref="ThemedPasteProgress"/>, which returns this.
/// </summary>
internal interface IPasteProgress : IDisposable
{
    /// <summary>The user pressed Cancel.</summary>
    event EventHandler CancelRequested;

    /// <summary>Shows <paramref name="done"/> of <paramref name="total"/> (the bar), with <paramref name="text"/> above it.</summary>
    void SetProgress(long done, long total, string text);

    /// <summary>Opens the window over the bottom edge of <paramref name="anchor"/> without taking the focus from it.</summary>
    void ShowNear(Control anchor);

    /// <summary>Closes the window.</summary>
    void Close();
}

/// <summary>
/// Creates the progress window of a paste that takes time for the harness-compiled controls. Those cannot reference
/// <c>GUI/</c>, so they cannot show the themed WPF window (<c>GUI/PasteProgressWindow</c>) directly, and the WinForms
/// <c>Forms/PasteProgressForm</c> draws a plain window that ignores the application theme. This facade routes the
/// request to the themed window that <c>App</c> injects through <see cref="CreateImpl"/>; without one it falls back to
/// the WinForms form, so the controls still work on their own.
/// </summary>
internal static class ThemedPasteProgress
{
    /// <summary>Creates the themed progress window. Injected by <c>App</c>: (title) -> the window.</summary>
    public static Func<string, IPasteProgress> CreateImpl;

    /// <summary>Creates the progress window with the given title.</summary>
    public static IPasteProgress Create(string title)
    {
        Func<string, IPasteProgress> create = CreateImpl;
        return create != null ? create(title) : new NativePasteProgress(title);
    }
}

/// <summary>The fallback used when no themed window is installed (the Mono harness): the WinForms form.</summary>
internal sealed class NativePasteProgress : IPasteProgress
{
    private readonly SmarTTY.Forms.PasteProgressForm _form;

    public NativePasteProgress(string title)
    {
        _form = new SmarTTY.Forms.PasteProgressForm(title);
    }

    public event EventHandler CancelRequested
    {
        add { _form.CancelRequested += value; }
        remove { _form.CancelRequested -= value; }
    }

    public void SetProgress(long done, long total, string text)
    {
        _form.SetProgress(done, total, text);
    }

    public void ShowNear(Control anchor)
    {
        _form.ShowNear(anchor);
    }

    public void Close()
    {
        _form.Close();
    }

    public void Dispose()
    {
        _form.Dispose();
    }
}
