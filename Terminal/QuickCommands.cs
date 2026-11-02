using SmarTTY.IO;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace SmarTTY.Terminal;

/// <summary>What a quick command does when it is chosen in the menu of the terminal.</summary>
public enum QuickCommandKind
{
    /// <summary>The text is put into the command line of the terminal; nothing runs until the user presses Enter.</summary>
    Command,

    /// <summary>
    /// The text is a shell script: it is uploaded into the current folder of the terminal under <see cref="QuickCommand.FileName"/>,
    /// made executable, and "./name" is put into the command line; nothing runs until the user presses Enter.
    /// </summary>
    Script
}

/// <summary>A command or a script of Tools > Quick commands, shown in the menu of the terminal (right click).</summary>
public sealed class QuickCommand
{
    /// <summary>The text of the menu item.</summary>
    public string Name { get; set; }

    public QuickCommandKind Kind { get; set; }

    /// <summary>The command line (one line), or the body of the script.</summary>
    public string Text { get; set; }

    /// <summary>The name of the script file on the server ("setup.sh"); used by scripts only.</summary>
    public string FileName { get; set; }

    /// <summary>Shown in the menu of the terminal; an item that is switched off stays in the list.</summary>
    public bool Enabled { get; set; } = true;

    public QuickCommand Clone()
    {
        return (QuickCommand)MemberwiseClone();
    }

    /// <summary>The text of the menu item: the name, or the command itself when the item has no name.</summary>
    [XmlIgnore]
    public string MenuText
    {
        get
        {
            string text = string.IsNullOrWhiteSpace(Name) ? (Kind == QuickCommandKind.Script ? FileName : QuickCommandRules.ToCommandLine(Text)) : Name.Trim();
            text = text ?? "";
            return text.Length > 60 ? text.Substring(0, 57) + "..." : text;
        }
    }
}

/// <summary>The list of quick commands, stored as QuickCommands.xml next to Settings.xml (in %APPDATA%\SmarTTY or PortableSettings).</summary>
[XmlRoot(RootElement)]
public sealed class QuickCommandList
{
    public const string RootElement = "QuickCommandList";

    public const string FileName = "QuickCommands.xml";

    [XmlArray("Items")]
    [XmlArrayItem("QuickCommand")]
    public List<QuickCommand> Items { get; set; } = new List<QuickCommand>();

    private static readonly XmlSerializer Serializer = new XmlSerializer(typeof(QuickCommandList));

    /// <summary>The list in <paramref name="directory"/>; an empty list when there is no file. A damaged file throws.</summary>
    public static QuickCommandList Load(string directory)
    {
        string path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            return new QuickCommandList();
        }
        using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Parse(stream);
    }

    public static QuickCommandList Parse(Stream stream)
    {
        XmlReaderSettings settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using XmlReader reader = XmlReader.Create(stream, settings);
        QuickCommandList list = (QuickCommandList)Serializer.Deserialize(reader);
        list.Items = (list.Items ?? new List<QuickCommand>()).Where(item => item != null).ToList();
        foreach (QuickCommand item in list.Items)
        {
            // XML keeps line breaks as LF; a script must not get CR back from anywhere.
            item.Text = (item.Text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        }
        return list;
    }

    /// <summary>Writes the list to <paramref name="directory"/>: into a temporary file first, which then replaces the old one.</summary>
    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, FileName);
        string temporary = path + ".tmp";
        using (FileStream stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            Write(stream);
        }
        if (File.Exists(path))
        {
            File.Replace(temporary, path, null);
        }
        else
        {
            File.Move(temporary, path);
        }
    }

    public void Write(Stream stream)
    {
        XmlWriterSettings settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, NewLineHandling = NewLineHandling.None };
        using XmlWriter writer = XmlWriter.Create(stream, settings);
        Serializer.Serialize(writer, this);
    }
}

/// <summary>
/// The rules of quick commands and the shell commands that put a script on the server. Pure logic without WPF or SSH, tested by
/// tests/QuickCommandsTests.
/// </summary>
public static class QuickCommandRules
{
    public const int MaxCommandLength = 4096;

    /// <summary>Why the item cannot be saved; null when it can.</summary>
    public static string Validate(QuickCommand item)
    {
        if (item == null)
        {
            return "The item is empty.";
        }
        if (item.Kind == QuickCommandKind.Command)
        {
            string line = ToCommandLine(item.Text);
            if (line.Length == 0)
            {
                return "Enter the command.";
            }
            if (line.Length > MaxCommandLength)
            {
                return "The command is longer than " + MaxCommandLength + " characters: make it a script.";
            }
            return null;
        }
        string nameProblem = ValidateFileName(item.FileName);
        if (nameProblem != null)
        {
            return nameProblem;
        }
        if (string.IsNullOrWhiteSpace(item.Text))
        {
            return "The script is empty.";
        }
        return RemoteScript.Validate(ScriptBytes(item.Text)) is string problem ? "The script: " + problem : null;
    }

    /// <summary>Why <paramref name="name"/> cannot be the name of the script file; null when it can.</summary>
    public static string ValidateFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Enter the file name of the script (for example setup.sh).";
        }
        if (name != name.Trim())
        {
            return "The file name starts or ends with a space.";
        }
        if (name == "." || name == ".." || name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0 || name.Any(c => c < ' ' || c == 0x7F))
        {
            return "The file name must be a name in the current folder, without / or control characters.";
        }
        if (name.Length > 200)
        {
            return "The file name is too long.";
        }
        return null;
    }

    /// <summary>
    /// The command as one line of the command line: the line breaks of a text pasted into the field are joined with a space,
    /// and the spaces around it are dropped.
    /// </summary>
    public static string ToCommandLine(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        return string.Join(" ", lines.Select(line => line.Trim()).Where(line => line.Length > 0)).Trim();
    }

    /// <summary>The bytes of the script as they are stored on the server: UTF-8 without a BOM, Unix line ends, a final line break.</summary>
    public static byte[] ScriptBytes(string text)
    {
        string body = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        if (body.Length > 0 && !body.EndsWith("\n", StringComparison.Ordinal))
        {
            body += "\n";
        }
        return RemoteScript.Normalize(new UTF8Encoding(false).GetBytes(body));
    }

    /// <summary>The full path of the script on the server.</summary>
    public static string RemotePath(string directory, string fileName)
    {
        string folder = string.IsNullOrEmpty(directory) ? "." : directory.TrimEnd('/');
        return (folder.Length == 0 ? "" : folder) + "/" + fileName;
    }

    /// <summary>Prints "exists" when a file or folder of that name is already there.</summary>
    public static string BuildExistsCommand(string remotePath)
    {
        return SmarTTY.Utilities.PosixShell.Wrap("if [ -e " + RemotePathUtils.ShellQuote(remotePath) + " ] || [ -L " + RemotePathUtils.ShellQuote(remotePath) + " ]; then echo exists; fi");
    }

    /// <summary>
    /// The command line that stores its standard input as the script and makes it executable: it is written into a temporary
    /// file next to it first and renamed at the end, so that a broken upload never leaves half a script under the name. Only
    /// cat, chmod and mv are used, which BusyBox has; no scp or sftp server is needed.
    /// </summary>
    public static string BuildUploadCommand(string remotePath)
    {
        string target = RemotePathUtils.ShellQuote(remotePath);
        string temporary = RemotePathUtils.ShellQuote(remotePath + ".smartty-part");
        return SmarTTY.Utilities.PosixShell.Wrap("umask 022; cat > " + temporary + " && chmod 755 " + temporary + " && mv -f " + temporary + " " + target + " || { rm -f " + temporary + "; exit 1; }");
    }

    /// <summary>
    /// What is put into the command line to run the script: "./setup.sh" in the current folder (quoted when the name needs it),
    /// or its full path when it was put into another folder.
    /// </summary>
    public static string BuildInvocation(string fileName, string directory = null)
    {
        if (directory != null)
        {
            return QuoteWhenNeeded(RemotePath(directory, fileName));
        }
        return "./" + QuoteWhenNeeded(fileName);
    }

    private static string QuoteWhenNeeded(string text)
    {
        foreach (char c in text)
        {
            if (!(char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-' || c == '/' || c == '+' || c == ',' || c == '@' || c == '%' || c == ':'))
            {
                return RemotePathUtils.ShellQuote(text);
            }
        }
        return text;
    }

    /// <summary>The items that the menu of the terminal shows: the switched on ones, scripts only where they can run.</summary>
    public static List<QuickCommand> MenuItems(QuickCommandList list, bool scriptsPossible)
    {
        return (list?.Items ?? new List<QuickCommand>())
            .Where(item => item.Enabled && Validate(item) == null && (scriptsPossible || item.Kind == QuickCommandKind.Command))
            .ToList();
    }
}
