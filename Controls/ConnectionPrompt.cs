using System.ComponentModel;

namespace SmarTTY.GUI.Backend;

/// <summary>
/// What ConnectionClosedPopup shows and does. The same popup serves the SSH sessions
/// (<see cref="SessionWrapper.SessionFixer"/>) and the device terminals (COM, TCP, netconsole), so the two never differ.
/// </summary>
public interface IConnectionPrompt : INotifyPropertyChanged
{
    /// <summary>The big heading ("Connection closed", "Connecting", ...).</summary>
    string Title { get; }

    /// <summary>The text under the heading.</summary>
    string Message { get; }

    /// <summary>False while a connection is being opened and there is nothing to choose.</summary>
    bool ShowActions { get; }

    /// <summary>The text next to the progress bar; null hides the progress bar, an empty string shows the bar alone.</summary>
    string ProgressText { get; }

    void Reconnect();

    void Close();

    void Ignore();
}

/// <summary>A prompt whose texts are set by its owner and whose actions are delegates.</summary>
public sealed class ConnectionPromptModel : IConnectionPrompt
{
    private string _Title = "";

    private string _Message = "";

    private bool _ShowActions = true;

    private string _ProgressText;

    private readonly System.Action _Reconnect;

    private readonly System.Action _Close;

    private readonly System.Action _Ignore;

    public ConnectionPromptModel(System.Action reconnect, System.Action close, System.Action ignore)
    {
        _Reconnect = reconnect;
        _Close = close;
        _Ignore = ignore;
    }

    public event PropertyChangedEventHandler PropertyChanged;

    public string Title
    {
        get => _Title;
        set => Set(ref _Title, value, nameof(Title));
    }

    public string Message
    {
        get => _Message;
        set => Set(ref _Message, value, nameof(Message));
    }

    public bool ShowActions
    {
        get => _ShowActions;
        set => Set(ref _ShowActions, value, nameof(ShowActions));
    }

    public string ProgressText
    {
        get => _ProgressText;
        set => Set(ref _ProgressText, value, nameof(ProgressText));
    }

    public void Reconnect()
    {
        _Reconnect?.Invoke();
    }

    public void Close()
    {
        _Close?.Invoke();
    }

    public void Ignore()
    {
        _Ignore?.Invoke();
    }

    private void Set<T>(ref T field, T value, string name)
    {
        if (!System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
