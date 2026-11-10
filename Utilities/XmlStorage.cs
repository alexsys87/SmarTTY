using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Xml;
using System.Xml.Serialization;

namespace SmarTTY.Utilities;

public class XmlStorage
{
    private class SerializerEntry
    {
        public class TypedSerializer
        {
            public Type[] ExtraTypes;

            public XmlSerializer Serializer;

            public TypedSerializer(Type type, Type[] items)
            {
                ExtraTypes = (Type[])items.Clone();
                this.Serializer = new XmlSerializer(type, items);
            }

            public bool MatchesExtraTypes(Type[] items)
            {
                if (items == null || ExtraTypes == null)
                {
                    return false;
                }
                if (items.Length != ExtraTypes.Length)
                {
                    return false;
                }
                for (int i = 0; i < items.Length; i++)
                {
                    if (items[i] != ExtraTypes[i])
                    {
                        return false;
                    }
                }
                return true;
            }
        }

        public readonly XmlSerializer Serializer;

        public List<TypedSerializer> Variants;

        public SerializerEntry(Type type)
        {
            Serializer = new XmlSerializer(type);
        }
    }

    public class SerializableObjectWithFileNameTracking
    {
        [XmlIgnore]
        public string SavedFileName;
    }

    private static Dictionary<Type, SerializerEntry> _cache = new Dictionary<Type, SerializerEntry>();

    private static XmlSerializer GetSerializer(Type type, Type[] items)
    {
        lock (_cache)
        {
            if (!_cache.TryGetValue(type, out var entry))
            {
                entry = (_cache[type] = new SerializerEntry(type));
            }
            if (items == null || items.Length == 0)
            {
                return entry.Serializer;
            }
            if (entry.Variants == null)
            {
                entry.Variants = new List<SerializerEntry.TypedSerializer>();
            }
            foreach (SerializerEntry.TypedSerializer typedSerializer in entry.Variants)
            {
                if (typedSerializer.MatchesExtraTypes(items))
                {
                    return typedSerializer.Serializer;
                }
            }
            SerializerEntry.TypedSerializer newTypedSerializer = new SerializerEntry.TypedSerializer(type, items);
            entry.Variants.Add(newTypedSerializer);
            return newTypedSerializer.Serializer;
        }
    }

    // A file that declares a DTD (an entity that expands to gigabytes, an external entity) is refused: XmlSerializer.Deserialize(Stream)
    // reads with the DTD processing of an XmlTextReader on, and the settings files are also read from archives that come from elsewhere.
    private static XmlReader CreateSafeReader(Stream stream)
    {
        return XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
    }

    private static XmlReader CreateSafeReader(TextReader reader)
    {
        return XmlReader.Create(reader, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
    }

    public static _Ty LoadFromFile<_Ty>(string filePath, params Type[] items)
    {
        XmlSerializer xmlSerializer = GetSerializer(typeof(_Ty), items);
        if (!filePath.EndsWith(".gz", StringComparison.InvariantCultureIgnoreCase) && File.Exists(filePath))
        {
            using (FileStream fileStream = File.OpenRead(filePath))
            {
                try
                {
                    using XmlReader reader = CreateSafeReader(fileStream);
                    return (_Ty)xmlSerializer.Deserialize(reader);
                }
                catch (XmlException xmlEx)
                {
                    throw new XmlException("Error parsing " + filePath + ": " + xmlEx.Message, xmlEx);
                }
            }
        }
        string gzFilePath = filePath;
        if (!gzFilePath.EndsWith(".gz", StringComparison.InvariantCultureIgnoreCase))
        {
            gzFilePath += ".gz";
        }
        if (File.Exists(gzFilePath))
        {
            using (FileStream gzFileStream = File.OpenRead(gzFilePath))
            {
                using GZipStream gzipStream = new GZipStream(gzFileStream, CompressionMode.Decompress);
                try
                {
                    using XmlReader reader = CreateSafeReader(gzipStream);
                    return (_Ty)xmlSerializer.Deserialize(reader);
                }
                catch (XmlException gzXmlEx)
                {
                    throw new XmlException("Error parsing " + gzFilePath + ": " + gzXmlEx.Message, gzXmlEx);
                }
            }
        }
        throw new Exception(filePath + " does not exist!");
    }

    public static void SaveToFile(object item, string filePath, params Type[] items)
    {
        XmlSerializer xmlSerializer = GetSerializer(item.GetType(), items);
        WriteAtomically(filePath, stream => xmlSerializer.Serialize(stream, item));
    }

    /// <summary>
    /// Writes a file through a temporary one next to it and puts it in place in one step. Creating the file itself empties it first, and a
    /// failure in the middle (power loss, a full disk, a virus scanner, an exception of the serializer) would leave a truncated settings
    /// file that is read as empty and then overwritten. The old file is kept as <paramref name="backupPath"/> when that is given.
    /// </summary>
    public static void WriteAtomically(string path, Action<Stream> write, string backupPath = null)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
        try
        {
            using (FileStream stream = File.Create(temp))
            {
                write(stream);
            }
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(temp, path, backupPath);
                    return;
                }
                catch (Exception ex) when (ex is IOException || ex is PlatformNotSupportedException || ex is UnauthorizedAccessException)
                {
                    // A file system without the replace call (or a lock that a plain copy will meet as well and report): the copy below.
                }
                File.Copy(temp, path, overwrite: true);
                File.Delete(temp);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch
            {
            }
            throw;
        }
    }

    /// <summary>
    /// Moves a file that could not be read aside as "name.bad" (and never over an earlier one), so that the next save does not
    /// overwrite the only copy of what the user had. Returns the new name, or null when it could not be moved.
    /// </summary>
    public static string SetAsideUnreadable(string path)
    {
        try
        {
            string target = path + ".bad";
            for (int i = 2; File.Exists(target); i++)
            {
                target = path + ".bad" + i;
            }
            File.Move(path, target);
            return target;
        }
        catch
        {
            return null;
        }
    }

    private static readonly object ProblemSync = new object();

    private static Action<string> _problemNotice;

    private static string _pendingProblem;

    private static string _lastProblem;

    /// <summary>
    /// Where problems with the user's files (a settings file that could not be saved or read) are shown. A problem that arrives before the
    /// handler is set (the settings are read before the first window) is shown as soon as it is.
    /// </summary>
    public static Action<string> ProblemNotice
    {
        set
        {
            string pending;
            lock (ProblemSync)
            {
                _problemNotice = value;
                pending = _pendingProblem;
                _pendingProblem = null;
            }
            if (pending != null && value != null)
            {
                value(pending);
            }
        }
    }

    /// <summary>Shows <paramref name="message"/> once: the same text again (every save fails the same way) is not repeated.</summary>
    public static void ReportProblem(string message)
    {
        Action<string> notice;
        lock (ProblemSync)
        {
            if (message == _lastProblem)
            {
                return;
            }
            _lastProblem = message;
            notice = _problemNotice;
            if (notice == null)
            {
                _pendingProblem = message;
            }
        }
        notice?.Invoke(message);
    }

    /// <summary>A save worked: the next failure is a new problem again.</summary>
    public static void ClearProblem()
    {
        lock (ProblemSync)
        {
            _lastProblem = null;
        }
    }

    public static string Serialize(object sender, params Type[] items)
    {
        if (sender == null)
        {
            return null;
        }
        XmlSerializer xmlSerializer = GetSerializer(sender.GetType(), items);
        using StringWriter stringWriter = new StringWriter();
        xmlSerializer.Serialize(stringWriter, sender);
        return stringWriter.ToString();
    }

    public static _Ty Deserialize<_Ty>(string xmlString, params Type[] items)
    {
        if (string.IsNullOrEmpty(xmlString))
        {
            return default;
        }
        XmlSerializer xmlSerializer = GetSerializer(typeof(_Ty), items);
        using StringReader textReader = new StringReader(xmlString);
        using XmlReader reader = CreateSafeReader(textReader);
        return (_Ty)xmlSerializer.Deserialize(reader);
    }
}
