
namespace VisualTTY;

public class EditorSettingsWrapper
{
    private GlobalSettings _Settings;

    public string TemporaryDirectory
    {
        get
        {
            return _Settings.Editor.TemporaryDirectory;
        }
        set
        {
            _Settings.Editor.TemporaryDirectory = value;
        }
    }

    public int TabSize
    {
        get
        {
            return _Settings.Editor.TabSize;
        }
        set
        {
            _Settings.Editor.TabSize = value;
        }
    }

    public bool InsertSpaces
    {
        get
        {
            return _Settings.Editor.InsertSpaces;
        }
        set
        {
            _Settings.Editor.InsertSpaces = value;
        }
    }

    public bool BackupBeforeSave
    {
        get
        {
            return _Settings.Editor.BackupBeforeSave;
        }
        set
        {
            _Settings.Editor.BackupBeforeSave = value;
        }
    }

    public bool SyntaxHighlighting
    {
        get
        {
            return _Settings.Editor.SyntaxHighlighting;
        }
        set
        {
            _Settings.Editor.SyntaxHighlighting = value;
        }
    }

    public bool ShowTabs
    {
        get
        {
            return _Settings.Editor.ShowTabs;
        }
        set
        {
            _Settings.Editor.ShowTabs = value;
        }
    }

    public bool WordWrap
    {
        get
        {
            return _Settings.Editor.WordWrap;
        }
        set
        {
            _Settings.Editor.WordWrap = value;
        }
    }

    public bool ShowAllCharacters
    {
        get
        {
            return _Settings.Editor.ShowAllCharacters;
        }
        set
        {
            _Settings.Editor.ShowAllCharacters = value;
        }
    }

    public bool Autocompletion
    {
        get
        {
            return _Settings.Editor.Autocompletion;
        }
        set
        {
            _Settings.Editor.Autocompletion = value;
        }
    }

    public bool AutocompletionOnTyping
    {
        get
        {
            return _Settings.Editor.AutocompletionOnTyping;
        }
        set
        {
            _Settings.Editor.AutocompletionOnTyping = value;
        }
    }

    public bool ApiSymbols
    {
        get
        {
            return _Settings.Editor.ApiSymbols;
        }
        set
        {
            _Settings.Editor.ApiSymbols = value;
        }
    }

    public GlobalSettings.EditorColors Colors
    {
        get
        {
            if (_Settings.IsLightTheme)
            {
                return _Settings.Editor.LightColors;
            }
            return _Settings.Editor.DarkColors;
        }
    }

    public EditorSettingsWrapper(GlobalSettings globalSettings)
    {
        _Settings = globalSettings;
    }
}
