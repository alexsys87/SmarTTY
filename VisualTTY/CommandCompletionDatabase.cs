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

    public static CommandCompletionDatabase Load()
    {
        return Load(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "CommandCompletions.xml"));
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
                source += ".gz";
                compressed = true;
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
        catch (Exception ex) when (ex is IOException || ex is InvalidDataException ||
                                   ex is UnauthorizedAccessException ||
                                   ex is InvalidOperationException || ex is XmlException ||
                                   ex is SecurityException || ex is ArgumentException)
        {
            string warning = "Could not load command completions from '" + path + "': " + ex.Message;
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
}
