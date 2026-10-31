using SmarTTY.Controls;
using SmarTTY.Utilities;
using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
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

        // A grey line under each tab character, to tell tab-indented lines from space-indented ones.
        public bool ShowTabs;

        // Marks for spaces, tabs, line ends and other characters that would otherwise leave no ink (see ShowTabs).
        public bool ShowAllCharacters;

        public bool SyntaxHighlighting;

        // The editor offers words (keywords and identifiers of the file) while typing; Ctrl+Space opens the list.
        public bool Autocompletion;

        // The completion popup also opens by itself after a couple of letters of an identifier (on by default).
        public bool AutocompletionOnTyping;

        // The completion list and the parameter hint also offer the standard API symbols of the language
        // (Python builtins, the C library, the shell builtins, ...) from ApiSymbols.xml.
        public bool ApiSymbols;

        // Tab and indentation insert TabSize spaces instead of a tab character.
        public bool InsertSpaces;

        // The zoom of the editor text (Ctrl+wheel, Ctrl+0); 1 is the size of the font, 0 means the setting was never saved.
        public float FontZoom;

        // The editor wraps long lines at the window edge instead of scrolling sideways.
        public bool WordWrap;

        // The first save of a file copies the old one to file~ on the server.
        public bool BackupBeforeSave;

        // The editor checks a file before it replaces it on the server (sh -n, python syntax, sshd -t, ...). The two
        // flags are "off" switches so that the old settings files, which lack them, get the checks (the struct has no
        // field initializers, so a missing element reads as false).
        public bool DisableConfigCheck;

        // After saving the configuration of a service (sshd, nginx) the editor does not ask to reload it.
        public bool DisableServiceReloadOffer;

        public EditorColors DarkColors;

        public EditorColors LightColors;

        public EditorColors GetEffectiveColors(SmarTTYTheme theme)
        {
            return GetEffectiveColors(theme.IsLight());
        }

        public EditorColors GetEffectiveColors(bool lightTheme)
        {
            // The editor colours come in a dark and a light family, not one per palette: the dark themes edit with
            // the dark family, the light ones (Light, Solarized Light, Sepia, a custom light theme) with the light family.
            if (lightTheme)
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

    /// <summary>
    /// Whether SSH terminal tabs work in byte mode, for a shell that edits its command line in UTF-8 bytes rather than
    /// characters (see TerminalEmulatorControl.ByteOrientedLineEditing and SessionWrapper.UsesByteOrientedLineEditing).
    /// Used for classic tabs (<see cref="ByteOrientedLineEditing"/>) and Smart Terminal tabs
    /// (VirtualTerminalSettings.ByteOrientedLineEditing); decided when a tab opens, as its PTY depends on it.
    /// </summary>
    public enum ByteOrientedLineEditingMode
    {
        [EnumDisplayText("Detect (BusyBox without UTF-8, e.g. OpenWrt)")]
        Auto,
        [EnumDisplayText("Always")]
        Always,
        [EnumDisplayText("Never")]
        Never
    }

    private string _DefaultDownloadDirectory;

    public bool AutoCloseTabs = true;

    public bool AutoWrapLines = true;

    private XServerType _XServer;

    public const string DefaultXServerArguments = "-multiwindow";

    private string _BeepFileName;

    public string FontName;

    public float FontSize;

    /// <summary>Bold variant of the terminal font, chosen in the font picker.</summary>
    public bool FontBold;

    /// <summary>Italic variant of the terminal font, chosen in the font picker.</summary>
    public bool FontItalic;

    public bool ShowMinimalisticModeWarning = true;

    public ConsolePalette PaletteOverride;

    public EditorSettings Editor = new EditorSettings
    {
        TemporaryDirectory = "/tmp",
        DarkColors = EditorColors.DarkColors,
        LightColors = EditorColors.LightColors,
        TabSize = 4,
        ShowLineNumbers = true,
        SyntaxHighlighting = true,
        Autocompletion = true,
        AutocompletionOnTyping = true,
        ApiSymbols = true
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

    /// <summary>
    /// SCP by default; SFTP is used where the server provides it and SCP otherwise.
    /// </summary>
    public SmarTTY.Transport.FileTransferProtocol TransferProtocol { get; set; }

    public int MaxParallelTransfers { get; set; } = 2;

    /// <summary>
    /// The limit for all transfers of a session together, in KB/s; 0 means unlimited.
    /// </summary>
    public int TransferSpeedLimitKBps { get; set; }

    public bool VerifyTransfers { get; set; } = true;

    public bool ResumeTransfers { get; set; } = true;

    public bool AtomicUploads { get; set; } = true;

    public bool SaveTransferQueue { get; set; } = true;

    public bool ShowCtrlInsertWarnings { get; set; } = true;

    public bool UseCtrlShortcuts { get; set; }

    public bool EmulateDirectionKeysWithMouseWheel { get; set; } = true;

    public bool ShiftPageXXToScroll { get; set; } = true;

    public bool InterceptCtrlClipboardShortcuts { get; set; }

    public bool SendAltPlusLetterToSession { get; set; } = true;

    public bool CopyOnSelection { get; set; } = true;

    public ByteOrientedLineEditingMode ByteOrientedLineEditing { get; set; }

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

    public string XServerArguments { get; set; } = DefaultXServerArguments;

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

    /// <summary>The bold/italic variant chosen in the font picker.</summary>
    [XmlIgnore]
    public FontStyle FontVariant => (FontBold ? FontStyle.Bold : FontStyle.Regular) | (FontItalic ? FontStyle.Italic : FontStyle.Regular);

    [XmlIgnore]
    public Font EffectiveFont => new Font(string.IsNullOrEmpty(FontName) ? "Courier New" : FontName, (FontSize == 0f) ? 12f : FontSize, FontVariant);

    /// <summary>
    /// The configured terminal font, or null when nothing is configured — the caller then keeps its own default
    /// instead of overriding it with Courier New.
    /// </summary>
    [XmlIgnore]
    public Font ConfiguredFont => (string.IsNullOrEmpty(FontName) || FontSize == 0f) ? null : new Font(FontName, FontSize, FontVariant);

    public bool ShowUnrecognizedCharactersAsHex { get; set; }

    public SmarTTYTheme Theme { get; set; }

    /// <summary>
    /// The file name of the custom theme (a <c>*.theme</c> file in the <c>Themes</c> folder next to the program) that is
    /// selected, or empty for a built-in theme. When the file is gone the built-in <see cref="Theme"/> is used, which a
    /// custom theme keeps set to its base theme.
    /// </summary>
    public string CustomTheme { get; set; }

    /// <summary>The selected theme, built-in or custom; the settings window binds its theme list to it.</summary>
    [XmlIgnore]
    public ThemeChoice ThemeChoice
    {
        get => ThemeLibrary.Find(Theme, CustomTheme);
        set
        {
            if (value == null)
            {
                return;
            }
            Theme = value.BuiltIn;
            CustomTheme = value.IsCustom ? value.FileName : null;
        }
    }

    /// <summary>Every theme the program offers (the built-in ones and those in the themes folder).</summary>
    [XmlIgnore]
    public System.Collections.Generic.IReadOnlyList<ThemeChoice> AvailableThemes => ThemeLibrary.All;

    /// <summary>Whether the selected theme is a light one; it picks the light or the dark editor colours.</summary>
    [XmlIgnore]
    public bool IsLightTheme => ThemeChoice.IsLight;

    public HeaderOrientation TabHeaderOrientation { get; set; } = HeaderOrientation.Bottom;

    public HeaderOrientation SessionHeaderOrientation { get; set; } = HeaderOrientation.Left;

    public DeviceTerminalSettings.ColorWrapper DefaultTextColor { get; set; }

    public ScrollbarMode VerticalScrollbarMode { get; set; }

    public ScrollbarMode HorizontalScrollbarMode { get; set; } = ScrollbarMode.Auto;

    public VirtualTerminalSettings VirtualTerminal { get; set; } = new VirtualTerminalSettings();

    /// <summary>Cleaning and checking of text pasted into terminals.</summary>
    public PasteSettings Paste { get; set; } = new PasteSettings();

    public bool CompletionTriggersMigratedV2 { get; set; }

    public bool SyntaxHighlightingMigrated { get; set; }

    public bool ShowTabsMigrated { get; set; }

    public bool EditorAutocompletionMigrated { get; set; }

    public bool EditorAutocompletionOnTypingMigrated { get; set; }

    public bool EditorApiSymbolsMigrated { get; set; }

    public bool AutoLinkExtensionsMigrated { get; set; }

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
            XDocument document = XDocument.Load(Path.Combine(DataDirectory, "Settings.xml"));
            bool migrated = false;
            XElement root = document.Root;
            XElement xServer = root?.Element(nameof(XServer));
            if (xServer != null && xServer.Value == "XMing")
            {
                // The XMing X server option was removed. Map old settings to vcxsrv
                // instead of letting XmlSerializer reject the whole file and reset
                // every other setting to its default.
                xServer.Value = nameof(XServerType.Vcxsrv);
                migrated = true;
            }
            XElement legacyArguments = root?.Element("XMingMode");
            if (legacyArguments != null)
            {
                if (root.Element(nameof(XServerArguments)) == null)
                {
                    legacyArguments.Name = nameof(XServerArguments);
                }
                else
                {
                    legacyArguments.Remove();
                }
                migrated = true;
            }
            GlobalSettings settings;
            using (XmlReader reader = document.CreateReader())
            {
                settings = (GlobalSettings)new XmlSerializer(typeof(GlobalSettings)).Deserialize(reader);
            }
            if (migrated)
            {
                settings.Save();
            }
            return settings;
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
        Paste ??= new PasteSettings();
        Paste.Snippets ??= new System.Collections.Generic.List<PasteSnippet>();
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
        if (!ShowTabsMigrated)
        {
            // The tab underline is a new setting: turn it on once for existing configurations,
            // as the feature is meant to be visible by default and can be switched off in Settings afterwards.
            Editor.ShowTabs = true;
            ShowTabsMigrated = true;
            Save();
        }
        if (!EditorAutocompletionMigrated)
        {
            // Settings files written before the editor completion existed have no <Autocompletion>
            // element, so XmlSerializer leaves the flag false: turn the feature on once for existing
            // configurations; it can be switched off in Settings afterwards.
            Editor.Autocompletion = true;
            EditorAutocompletionMigrated = true;
            Save();
        }
        if (!EditorAutocompletionOnTypingMigrated)
        {
            // The completion list used to open only on Ctrl+Space (the setting was off by default). It now opens
            // while typing by default: turn it on once for existing configurations; it can be switched off in
            // Settings afterwards.
            Editor.AutocompletionOnTyping = true;
            EditorAutocompletionOnTypingMigrated = true;
            Save();
        }
        if (!EditorApiSymbolsMigrated)
        {
            // The API symbols are a new setting: turn them on once for existing configurations,
            // as the feature is meant to be visible by default and can be switched off in Settings afterwards.
            Editor.ApiSymbols = true;
            EditorApiSymbolsMigrated = true;
            Save();
        }
        if (!AutoLinkExtensionsMigrated)
        {
            // The list of extensions that are underlined in the terminal output used to be short (.c .cpp .h .sh .txt .js)
            // and Settings.xml keeps it, so a new default would never reach existing configurations. A list that is still
            // exactly the old default is replaced once; a list the user edited is left alone.
            VirtualTerminal ??= new VirtualTerminalSettings();
            if (string.Equals(VirtualTerminal.AutoLinkExtensions, VirtualTerminalSettings.OldDefaultAutoLinkExtensions, StringComparison.Ordinal))
            {
                VirtualTerminal.AutoLinkExtensions = VirtualTerminalSettings.DefaultAutoLinkExtensions;
            }
            AutoLinkExtensionsMigrated = true;
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
