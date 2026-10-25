using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security;
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
