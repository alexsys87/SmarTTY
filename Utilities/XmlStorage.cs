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

    public static _Ty LoadFromFile<_Ty>(string filePath, params Type[] items)
    {
        XmlSerializer xmlSerializer = GetSerializer(typeof(_Ty), items);
        if (!filePath.EndsWith(".gz", StringComparison.InvariantCultureIgnoreCase) && File.Exists(filePath))
        {
            using (FileStream fileStream = File.OpenRead(filePath))
            {
                try
                {
                    return (_Ty)xmlSerializer.Deserialize(fileStream);
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
                    return (_Ty)xmlSerializer.Deserialize(gzipStream);
                }
                catch (XmlException gzXmlEx)
                {
                    throw new XmlException("Error parsing " + gzFilePath + ": " + gzXmlEx.Message, gzXmlEx);
                }
            }
        }
        throw new Exception(filePath + " does not exist!");
    }

    public static _Ty LoadFromBytes<_Ty>(byte[] buffer, params Type[] items)
    {
        XmlSerializer xmlSerializer = GetSerializer(typeof(_Ty), items);
        using MemoryStream stream = new MemoryStream(buffer);
        return (_Ty)xmlSerializer.Deserialize(stream);
    }

    public static _Ty LoadFromStream<_Ty>(Stream stream, params Type[] items)
    {
        return (_Ty)GetSerializer(typeof(_Ty), items).Deserialize(stream);
    }

    public static void SaveToFile(object item, string filePath, params Type[] items)
    {
        XmlSerializer xmlSerializer = GetSerializer(item.GetType(), items);
        using FileStream stream = File.Create(filePath);
        xmlSerializer.Serialize(stream, item);
    }

    public static void SaveToStream(object sender, Stream stream, params Type[] items)
    {
        GetSerializer(sender.GetType(), items).Serialize(stream, sender);
    }

    public static byte[] SaveToBytes(object sender, params Type[] items)
    {
        XmlSerializer xmlSerializer = GetSerializer(sender.GetType(), items);
        using MemoryStream memoryStream = new MemoryStream();
        xmlSerializer.Serialize(memoryStream, sender);
        return memoryStream.ToArray();
    }

    public static void SaveToStreamTyped<_Ty>(object sender, Stream stream, params Type[] items)
    {
        GetSerializer(typeof(_Ty), items).Serialize(stream, sender);
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

    public static string SerializeAs<_Type>(object sender)
    {
        if (sender == null)
        {
            return null;
        }
        XmlSerializer xmlSerializer = GetSerializer(typeof(_Type), new Type[0]);
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
        return (_Ty)xmlSerializer.Deserialize(textReader);
    }

    public static void SaveToAppData<_Ty>(_Ty item, string subfolder, string namePrefix, string extension) where _Ty : SerializableObjectWithFileNameTracking
    {
        if (item.SavedFileName == null)
        {
            string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            folderPath = Path.Combine(folderPath, subfolder);
            Directory.CreateDirectory(folderPath);
            char[] sanitizedChars = namePrefix.ToCharArray();
            for (int i = 0; i < sanitizedChars.Length; i++)
            {
                if ("/\\@:<>*?[].".IndexOf(sanitizedChars[i]) != -1)
                {
                    sanitizedChars[i] = '_';
                }
            }
            item.SavedFileName = Path.Combine(folderPath, new string(sanitizedChars) + extension);
        }
        SaveToFile(item, item.SavedFileName);
    }

    public static List<_Ty> LoadAllFromAppData<_Ty>(string subfolder, string extension) where _Ty : SerializableObjectWithFileNameTracking
    {
        List<_Ty> itemsList = new List<_Ty>();
        try
        {
            XmlSerializer xmlSerializer = new XmlSerializer(typeof(_Ty));
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), subfolder);
            Directory.CreateDirectory(path);
            string[] filePaths = Directory.GetFiles(path, "*" + extension);
            foreach (string filePath in filePaths)
            {
                try
                {
                    using FileStream stream = File.OpenRead(filePath);
                    _Ty deserializedItem = (_Ty)xmlSerializer.Deserialize(stream);
                    deserializedItem.SavedFileName = filePath;
                    itemsList.Add(deserializedItem);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
        return itemsList;
    }
}
