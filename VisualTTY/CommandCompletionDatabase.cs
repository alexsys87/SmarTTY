using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Threading;
using System.Xml;
using System.Xml.Serialization;

namespace VisualTTY;

public class CommandCompletionDatabase
{
    public CommandCompletion[] Completions = new CommandCompletion[0];

    // Keep diagnostics available without making an optional dictionary fatal.
    [XmlIgnore]
    public string LoadWarning { get; private set; }

    /// <summary>CommandCompletions.xml next to the exe (also used by the settings export/import).</summary>
    public static string DefaultPath
    {
        get
        {
            return Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "CommandCompletions.xml");
        }
    }

    private static int _changeCounter;

    /// <summary>
    /// Grows every time the database was saved by the editor. Sessions that cached a database compare it with the value
    /// they loaded theirs at and read the file again, so that an edit works without restarting the program.
    /// </summary>
    public static int ChangeCounter
    {
        get { return Volatile.Read(ref _changeCounter); }
    }

    public static void NotifyChanged()
    {
        Interlocked.Increment(ref _changeCounter);
    }

    /// <summary>True when only the gzip variant (CommandCompletions.xml.gz) exists, which is what <see cref="Load(string)"/> then reads.</summary>
    public static bool IsStoredCompressed(string xmlPath)
    {
        return !File.Exists(xmlPath) && File.Exists(xmlPath + ".gz");
    }

    public static CommandCompletionDatabase Load()
    {
        return Load(DefaultPath);
    }

    public static CommandCompletionDatabase Load(string path)
    {
        try
        {
            // Preserve the existing XML / XML.gz lookup order.
            bool compressed = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);
            string source = path;
            if (!compressed && !File.Exists(source))
            {
                if (File.Exists(source + ".gz"))
                {
                    source += ".gz";
                    compressed = true;
                }
                else
                {
                    CommandCompletionDatabase defaultDb = new CommandCompletionDatabase();
                    defaultDb.Save(path);
                    return defaultDb;
                }
            }
            else if (compressed && !File.Exists(source))
            {
                string uncompressedPath = path.Substring(0, path.Length - 3);
                if (File.Exists(uncompressedPath))
                {
                    source = uncompressedPath;
                    compressed = false;
                }
                else
                {
                    CommandCompletionDatabase defaultDb = new CommandCompletionDatabase();
                    defaultDb.Save(uncompressedPath);
                    return defaultDb;
                }
            }

            using (FileStream file = File.OpenRead(source))
            {
                if (compressed)
                {
                    using (GZipStream gzip = new GZipStream(file, CompressionMode.Decompress))
                    {
                        return ReadDatabase(gzip);
                    }
                }
                return ReadDatabase(file);
            }
        }
        // InvalidDataException signals a damaged gzip stream. It derives from SystemException,
        // so it is not covered by IOException and must be listed explicitly.
        catch (Exception exception) when (exception is IOException || exception is InvalidDataException ||
                                   exception is UnauthorizedAccessException ||
                                   exception is InvalidOperationException || exception is XmlException ||
                                   exception is SecurityException || exception is ArgumentException)
        {
            string warning = "Could not load command completions from '" + path + "': " + exception.Message;
            Trace.TraceWarning(warning);
            return new CommandCompletionDatabase { LoadWarning = warning };
        }
    }

    private static CommandCompletionDatabase ReadDatabase(Stream stream)
    {
        XmlReaderSettings settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 16 * 1024 * 1024
        };
        using (XmlReader reader = XmlReader.Create(stream, settings))
        {
            CommandCompletionDatabase database = (CommandCompletionDatabase)new XmlSerializer(typeof(CommandCompletionDatabase)).Deserialize(reader);
            if (database == null)
            {
                throw new InvalidOperationException("The command completion database is null.");
            }
            database.Completions = (database.Completions ?? new CommandCompletion[0])
                .Where(command => command != null &&
                    (!string.IsNullOrWhiteSpace(command.Command) || !string.IsNullOrWhiteSpace(command.CommandLineRegex)))
                .ToArray();
            foreach (CommandCompletion command in database.Completions)
            {
                command.Options = (command.Options ?? new CommandCompletion.Option[0])
                    .Where(option => option != null && !string.IsNullOrWhiteSpace(option.Value))
                    .ToArray();
            }
            return database;
        }
    }

    public CommandCompletion LookupCommand(string cmdLine)
    {
        return Completions?.FirstOrDefault(command => command != null && command.IsMatch(cmdLine));
    }

    /// <summary>
    /// Saves the database either as plain XML (<paramref name="xmlPath"/>) or as gzip (<paramref name="xmlPath"/> + ".gz").
    /// The file is written completely before it replaces the old one. The other variant is deleted afterwards:
    /// <see cref="Load(string)"/> prefers the plain file, so a leftover would hide the new data or override it with old data.
    /// </summary>
    public void SaveTo(string xmlPath, bool compressed)
    {
        byte[] xml;
        using (MemoryStream buffer = new MemoryStream())
        {
            using (XmlWriter writer = XmlWriter.Create(buffer, new XmlWriterSettings { Indent = true }))
            {
                new XmlSerializer(typeof(CommandCompletionDatabase)).Serialize(writer, this);
            }
            xml = buffer.ToArray();
        }

        string target = compressed ? xmlPath + ".gz" : xmlPath;
        string other = compressed ? xmlPath : xmlPath + ".gz";
        string directory = Path.GetDirectoryName(Path.GetFullPath(xmlPath));
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temp = target + ".tmp";
        try
        {
            using (FileStream file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                if (compressed)
                {
                    using (GZipStream gzip = new GZipStream(file, CompressionLevel.Optimal))
                    {
                        gzip.Write(xml, 0, xml.Length);
                    }
                }
                else
                {
                    file.Write(xml, 0, xml.Length);
                }
            }
            if (File.Exists(target))
            {
                File.Delete(target);
            }
            File.Move(temp, target);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            throw;
        }
        if (File.Exists(other))
        {
            File.Delete(other);
        }
    }

    public void Save(string path)
    {
        string directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        XmlWriterSettings settings = new XmlWriterSettings
        {
            Indent = true,
            OmitXmlDeclaration = false
        };

        using (XmlWriter writer = XmlWriter.Create(path, settings))
        {
            XmlSerializer serializer = new XmlSerializer(typeof(CommandCompletionDatabase));
            serializer.Serialize(writer, this);
        }
    }
}
