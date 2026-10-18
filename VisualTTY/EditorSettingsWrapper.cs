
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

    public GlobalSettings.EditorColors Colors
    {
        get
        {
            if (_Settings.Theme == SmarTTYTheme.Dark)
            {
                return _Settings.Editor.DarkColors;
            }
            return _Settings.Editor.LightColors;
        }
    }

    public EditorSettingsWrapper(GlobalSettings globalSettings)
    {
        _Settings = globalSettings;
    }
}
