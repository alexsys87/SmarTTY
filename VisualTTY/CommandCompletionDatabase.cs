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

    /// <summary>Name of the template built into the program: the dictionary that is used while there is no file next to the exe.</summary>
    public const string TemplateResourceName = "VisualTTY.CommandCompletions.xml";

    /// <summary>
    /// CommandCompletions.xml next to the exe (also used by the settings export/import). The file is optional: it is created by
    /// the editor on the first save and takes priority over the template built into the program.
    /// </summary>
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
                    return LoadTemplate();
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
                    return LoadTemplate();
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

    /// <summary>
    /// The dictionary built into the program (an empty one when this build has none). Nothing is written to disk: the editor
    /// saves the template together with the user's additions into the file next to the exe, which then takes priority.
    /// </summary>
    public static CommandCompletionDatabase LoadTemplate()
    {
        try
        {
            using (Stream stream = typeof(CommandCompletionDatabase).Assembly.GetManifestResourceStream(TemplateResourceName))
            {
                return stream == null ? new CommandCompletionDatabase() : ReadDatabase(stream);
            }
        }
        catch (Exception exception) when (exception is IOException || exception is InvalidOperationException || exception is XmlException)
        {
            string warning = "Could not load the built-in command completions: " + exception.Message;
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
    /// The new data is written completely to a temporary file and then swapped in with
    /// <see cref="File.Replace(string, string, string, bool)"/>, so the old file is still there if anything goes wrong on the way
    /// (a crash, a virus scanner holding a file, a refused rename).
    /// The other variant is removed afterwards: <see cref="Load(string)"/> prefers the plain file, so a leftover plain file would
    /// hide the new gzip data. If such a leftover cannot be removed the method throws, because the saved data would not be the
    /// data that is read. A leftover gzip file is harmless (it is ignored while the plain file exists), so failing to remove
    /// that one is not an error.
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
            ReplaceFile(temp, target);
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
        RemoveOtherVariant(other, otherHidesNewData: compressed);
    }

    private const int FileOperationAttempts = 5;

    /// <summary>
    /// Puts <paramref name="temporary"/> in place of <paramref name="target"/> in one step, so a failure leaves the old file as it
    /// was (deleting the old file first and moving the new one afterwards loses both if the move fails). A virus scanner or an
    /// indexer may hold either file for a moment, so a failure is tried again a few times. Metadata errors (ACLs, attributes)
    /// are ignored: the folder next to the exe can be on a file system that does not support them.
    /// </summary>
    private static void ReplaceFile(string temporary, string target)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(target))
                {
                    try
                    {
                        File.Replace(temporary, target, null, true);
                        return;
                    }
                    catch (FileNotFoundException) when (!File.Exists(target))
                    {
                        // The old file was removed between the check and the swap: there is nothing to replace.
                    }
                }
                File.Move(temporary, target);
                return;
            }
            catch (Exception exception) when (attempt < FileOperationAttempts &&
                                              (exception is IOException || exception is UnauthorizedAccessException))
            {
                Thread.Sleep(50 * attempt);
            }
        }
    }

    /// <summary>
    /// Removes the variant of the file that was not just written. When <paramref name="otherHidesNewData"/> is set (the leftover
    /// is the plain file, which <see cref="Load(string)"/> prefers) a leftover that cannot be removed is an error: the new data
    /// would be saved but never read.
    /// </summary>
    private static void RemoveOtherVariant(string other, bool otherHidesNewData)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (!File.Exists(other))
                {
                    return;
                }
                // A read-only leftover refuses to be deleted.
                File.SetAttributes(other, FileAttributes.Normal);
                File.Delete(other);
                return;
            }
            catch (Exception exception) when (attempt < FileOperationAttempts &&
                                              (exception is IOException || exception is UnauthorizedAccessException))
            {
                Thread.Sleep(50 * attempt);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                if (!otherHidesNewData)
                {
                    return;
                }
                throw new IOException("The changes were saved, but the old file '" + other + "' could not be removed (" +
                    exception.Message + "). It is read instead of the new one, so the changes take no effect until it is deleted.",
                    exception);
            }
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
