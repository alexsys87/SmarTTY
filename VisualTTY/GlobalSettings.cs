using SmarTTY.Controls;
using SmarTTY.Utilities;
using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Xml.Serialization;
using VisualTTY.DeviceTerminal;

namespace VisualTTY;

public class GlobalSettings : INotifyPropertyChanged
{
    public class EditorColors
    {
        public DeviceTerminalSettings.ColorWrapper TextColor { get; set; }

        public DeviceTerminalSettings.ColorWrapper BackColor { get; set; }

        public DeviceTerminalSettings.ColorWrapper LineNumberBackground { get; set; }

        public DeviceTerminalSettings.ColorWrapper LineNumberForeground { get; set; }

        // ---- Цвета токенов подсветки синтаксиса (типы 1..7 из SyntaxHighlighting).
        //      Тип 0 (Normal) всегда использует TextColor. ----
        public DeviceTerminalSettings.ColorWrapper KeywordColor { get; set; }

        public DeviceTerminalSettings.ColorWrapper StringColor { get; set; }

        public DeviceTerminalSettings.ColorWrapper CommentColor { get; set; }

        public DeviceTerminalSettings.ColorWrapper NumberColor { get; set; }

        public DeviceTerminalSettings.ColorWrapper VariableColor { get; set; }

        public DeviceTerminalSettings.ColorWrapper SectionColor { get; set; }

        public DeviceTerminalSettings.ColorWrapper PreprocessorColor { get; set; }

        public static EditorColors DarkColors => new EditorColors
        {
            TextColor = Color.White,
            BackColor = Color.FromArgb(58, 46, 15),
            LineNumberBackground = Color.FromArgb(64, 64, 64),
            LineNumberForeground = Color.LightGray,
            KeywordColor = Color.FromArgb(86, 156, 214),
            StringColor = Color.FromArgb(214, 157, 133),
            CommentColor = Color.FromArgb(131, 148, 105),
            NumberColor = Color.FromArgb(181, 206, 168),
            VariableColor = Color.FromArgb(156, 220, 254),
            SectionColor = Color.FromArgb(78, 201, 176),
            PreprocessorColor = Color.FromArgb(189, 99, 197)
        };

        public static EditorColors LightColors => new EditorColors
        {
            TextColor = Color.Black,
            BackColor = Color.LightGoldenrodYellow,
            LineNumberBackground = SystemColors.ButtonFace,
            LineNumberForeground = Color.DarkGray,
            KeywordColor = Color.FromArgb(0, 0, 150),
            StringColor = Color.FromArgb(163, 21, 21),
            CommentColor = Color.FromArgb(0, 99, 49),
            NumberColor = Color.FromArgb(135, 91, 33),
            VariableColor = Color.FromArgb(0, 115, 165),
            SectionColor = Color.FromArgb(0, 122, 117),
            PreprocessorColor = Color.FromArgb(159, 23, 138)
        };
    }

    public struct EditorSettings
    {
        public string TemporaryDirectory;

        public int TabSize;

        public bool ShowLineNumbers;

        public bool SyntaxHighlighting;

        public EditorColors DarkColors;

        public EditorColors LightColors;

        public EditorColors GetEffectiveColors(SmarTTYTheme theme)
        {
            if (theme != SmarTTYTheme.Dark)
            {
                return LightColors;
            }
            return DarkColors;
        }
    }

    public enum ScrollbarMode
    {
        [EnumDisplayText("Always visible")]
        Show,
        [EnumDisplayText("Always hidden")]
        Hide,
        [EnumDisplayText("Hover mouse to show")]
        Auto
    }

    private string _DefaultDownloadDirectory;

    public bool AutoCloseTabs = true;

    public bool AutoWrapLines = true;

    private XServerType _XServer;

    public const string DefaultXMingMode = "-multiwindow";

    private string _BeepFileName;

    public string FontName;

    public float FontSize;

    public bool ShowMinimalisticModeWarning = true;

    public ConsolePalette PaletteOverride;

    public EditorSettings Editor = new EditorSettings
    {
        TemporaryDirectory = "/tmp",
        DarkColors = EditorColors.DarkColors,
        LightColors = EditorColors.LightColors,
        TabSize = 4,
        ShowLineNumbers = true,
        SyntaxHighlighting = true
    };

    public int LargeBlockCutoffPoint = 1048576;

    public string DefaultDownloadDirectory
    {
        get
        {
            return _DefaultDownloadDirectory;
        }
        set
        {
            _DefaultDownloadDirectory = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("DefaultDownloadDirectory"));
        }
    }

    public string DefaultUploadDirectory { get; set; } = "/tmp";

    public bool OpenLocalDirAfterDownload { get; set; }

    public bool ShowCtrlInsertWarnings { get; set; } = true;

    public bool UseCtrlShortcuts { get; set; }

    public bool EmulateDirectionKeysWithMouseWheel { get; set; } = true;

    public bool ShiftPageXXToScroll { get; set; } = true;

    public bool InterceptCtrlClipboardShortcuts { get; set; }

    public bool SendAltPlusLetterToSession { get; set; } = true;

    public bool CopyOnSelection { get; set; } = true;

    public XServerType XServer
    {
        get
        {
            return _XServer;
        }
        set
        {
            if (value != _XServer)
            {
                _XServer = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("XServer"));
            }
        }
    }

    public string CustomXServerPath { get; set; }

    public string XMingMode { get; set; } = "-multiwindow";

    public TerminalEmulatorControl.BeepMode BeepMode { get; set; } = TerminalEmulatorControl.BeepMode.SystemBeep;

    public string BeepFileName
    {
        get
        {
            return _BeepFileName;
        }
        set
        {
            _BeepFileName = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("BeepFileName"));
        }
    }

    [XmlIgnore]
    public Font EffectiveFont => new Font(string.IsNullOrEmpty(FontName) ? "Courier New" : FontName, (FontSize == 0f) ? 12f : FontSize);

    public bool ShowUnrecognizedCharactersAsHex { get; set; }

    public SmarTTYTheme Theme { get; set; }

    public HeaderOrientation TabHeaderOrientation { get; set; } = HeaderOrientation.Bottom;

    public HeaderOrientation SessionHeaderOrientation { get; set; } = HeaderOrientation.Left;

    public DeviceTerminalSettings.ColorWrapper DefaultTextColor { get; set; }

    public ScrollbarMode VerticalScrollbarMode { get; set; }

    public ScrollbarMode HorizontalScrollbarMode { get; set; } = ScrollbarMode.Auto;

    public VirtualTerminalSettings VirtualTerminal { get; set; } = new VirtualTerminalSettings();

    public bool CompletionTriggersMigratedV2 { get; set; }

    public bool SyntaxHighlightingMigrated { get; set; }

    [XmlIgnore]
    public EditorSettingsWrapper EditorWrapper { get; }

    public static string DataDirectory
    {
        get
        {
            bool changed = false;
            try
            {
                changed = File.Exists(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "portable.txt"));
            }
            catch
            {
            }
            string text = Assembly.GetExecutingAssembly().GetName().Name;
            if (text.EndsWith("Core", StringComparison.InvariantCultureIgnoreCase))
            {
                text = text.Substring(0, text.Length - 4);
            }
            if (changed)
            {
                return Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "PortableSettings");
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), text);
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;

    public GlobalSettings()
    {
        EditorWrapper = new EditorSettingsWrapper(this);
    }

    private static GlobalSettings DoLoad()
    {
        try
        {
            using FileStream stream = File.OpenRead(Path.Combine(DataDirectory, "Settings.xml"));
            return (GlobalSettings)new XmlSerializer(typeof(GlobalSettings)).Deserialize(stream);
        }
        catch
        {
            return new GlobalSettings();
        }
    }

    private static void FixTokenColors(EditorColors colors, EditorColors defaults)
    {
        if (colors.KeywordColor.Alpha == 0)
        {
            colors.KeywordColor = defaults.KeywordColor;
        }
        if (colors.StringColor.Alpha == 0)
        {
            colors.StringColor = defaults.StringColor;
        }
        if (colors.CommentColor.Alpha == 0)
        {
            colors.CommentColor = defaults.CommentColor;
        }
        if (colors.NumberColor.Alpha == 0)
        {
            colors.NumberColor = defaults.NumberColor;
        }
        if (colors.VariableColor.Alpha == 0)
        {
            colors.VariableColor = defaults.VariableColor;
        }
        if (colors.SectionColor.Alpha == 0)
        {
            colors.SectionColor = defaults.SectionColor;
        }
        if (colors.PreprocessorColor.Alpha == 0)
        {
            colors.PreprocessorColor = defaults.PreprocessorColor;
        }
    }

    public static GlobalSettings Load()
    {
        GlobalSettings globalSettings = DoLoad();
        if (globalSettings != null)
        {
            globalSettings.FixDefaults();
            return globalSettings;
        }
        return globalSettings;
    }

    private void FixDefaults()
    {
        if (DefaultTextColor.Alpha == 0)
        {
            DefaultTextColor = TerminalEmulatorControl.DefaultTextColor;
        }
        if (Editor.LightColors == null)
        {
            Editor.LightColors = EditorColors.LightColors;
        }
        if (Editor.DarkColors == null)
        {
            Editor.DarkColors = EditorColors.DarkColors;
        }
        // Settings.xml files saved before syntax highlighting was added have no token
        // colors: XmlSerializer leaves them as an all-zero (fully transparent) ColorWrapper,
        // which would make keywords, strings, comments etc. invisible.
        FixTokenColors(Editor.LightColors, EditorColors.LightColors);
        FixTokenColors(Editor.DarkColors, EditorColors.DarkColors);
        if (!SyntaxHighlightingMigrated)
        {
            // The same old files also lack the <SyntaxHighlighting> element, so XmlSerializer
            // resets the flag to false and the editor never highlights anything. Turn it on
            // once for existing configurations; it can be switched off in Settings afterwards.
            Editor.SyntaxHighlighting = true;
            SyntaxHighlightingMigrated = true;
            Save();
        }
        if (!CompletionTriggersMigratedV2)
        {
            // The "first character in a word" completion trigger used to default
            // to true and auto-opened the completion popup on the first typed
            // character, which made the Smart Terminal hijack typing (Space/Enter
            // substituted the auto-selected command). Turn it off once for existing
            // configurations; it can still be re-enabled in Settings afterwards.
            // Guard against a missing/empty node surviving deserialization: if that
            // happened, leaving VirtualTerminal/CompletionTriggers null would make
            // the Smart Terminal console throw a NullReferenceException on the very
            // first keypress (in SshTerminalControl.OnKeyPress), so typed characters
            // would never appear and Enter would not execute the command.
            VirtualTerminal ??= new VirtualTerminalSettings();
            VirtualTerminal.CompletionTriggers ??= new AutoCompletionTriggers();
            VirtualTerminal.CompletionTriggers.FirstCharacterInWord = false;
            CompletionTriggersMigratedV2 = true;
            Save();
        }
    }

    public void Save()
    {
        try
        {
            string dataDirectory = DataDirectory;
            Directory.CreateDirectory(dataDirectory);
            using FileStream stream = File.Create(Path.Combine(dataDirectory, "Settings.xml"));
            new XmlSerializer(typeof(GlobalSettings)).Serialize(stream, this);
        }
        catch
        {
        }
    }

    public GlobalSettings Clone()
    {
        return XmlStorage.Deserialize<GlobalSettings>(XmlStorage.Serialize(this), new Type[0]);
    }

    private static bool ShouldCopyMemberwise(Type type)
    {
        if (type.IsClass)
        {
            if (type.Assembly == typeof(string).Assembly)
            {
                return false;
            }
            if (type.IsArray)
            {
                return false;
            }
            return true;
        }
        return false;
    }

    private static bool CopyValuesRecursively(object source, object dest)
    {
        if (source == null || dest == null)
        {
            return false;
        }
        if (source.GetType() != dest.GetType())
        {
            return false;
        }
        FieldInfo[] fields = source.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public);
        foreach (FieldInfo fieldInfo in fields)
        {
            if (!ShouldCopyMemberwise(fieldInfo.FieldType) || !CopyValuesRecursively(fieldInfo.GetValue(source), fieldInfo.GetValue(dest)))
            {
                fieldInfo.SetValue(dest, fieldInfo.GetValue(source));
            }
        }
        PropertyInfo[] properties = source.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public);
        foreach (PropertyInfo propertyInfo in properties)
        {
            if (propertyInfo.CanWrite && (!ShouldCopyMemberwise(propertyInfo.PropertyType) || !CopyValuesRecursively(propertyInfo.GetValue(source, new object[0]), propertyInfo.GetValue(dest, new object[0]))))
            {
                propertyInfo.SetValue(dest, propertyInfo.GetValue(source, new object[0]), new object[0]);
            }
        }
        return true;
    }

    public void CopyValuesTo(GlobalSettings originalSettings)
    {
        CopyValuesRecursively(this, originalSettings);
    }
}
