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
                for (int num = 0; num < items.Length; num++)
                {
                    if (items[num] != ExtraTypes[num])
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
            if (!_cache.TryGetValue(type, out var value))
            {
                value = (_cache[type] = new SerializerEntry(type));
            }
            if (items == null || items.Length == 0)
            {
                return value.Serializer;
            }
            if (value.Variants == null)
            {
                value.Variants = new List<SerializerEntry.TypedSerializer>();
            }
            foreach (SerializerEntry.TypedSerializer item in value.Variants)
            {
                if (item.MatchesExtraTypes(items))
                {
                    return item.Serializer;
                }
            }
            SerializerEntry.TypedSerializer typedSerializer = new SerializerEntry.TypedSerializer(type, items);
            value.Variants.Add(typedSerializer);
            return typedSerializer.Serializer;
        }
    }

    public static _Ty LoadFromFile<_Ty>(string text2, params Type[] items)
    {
        XmlSerializer xmlSerializer = GetSerializer(typeof(_Ty), items);
        if (!text2.EndsWith(".gz", StringComparison.InvariantCultureIgnoreCase) && File.Exists(text2))
        {
            using (FileStream stream = File.OpenRead(text2))
            {
                try
                {
                    return (_Ty)xmlSerializer.Deserialize(stream);
                }
                catch (XmlException ex)
                {
                    throw new XmlException("Error parsing " + text2 + ": " + ex.Message, ex);
                }
            }
        }
        string text = text2;
        if (!text.EndsWith(".gz", StringComparison.InvariantCultureIgnoreCase))
        {
            text += ".gz";
        }
        if (File.Exists(text))
        {
            using (FileStream stream2 = File.OpenRead(text))
            {
                using GZipStream stream3 = new GZipStream(stream2, CompressionMode.Decompress);
                try
                {
                    return (_Ty)xmlSerializer.Deserialize(stream3);
                }
                catch (XmlException ex2)
                {
                    throw new XmlException("Error parsing " + text + ": " + ex2.Message, ex2);
                }
            }
        }
        throw new Exception(text2 + " does not exist!");
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

    public static void SaveToFile(object obj, string text, params Type[] items)
    {
        XmlSerializer xmlSerializer = GetSerializer(obj.GetType(), items);
        using FileStream stream = File.Create(text);
        xmlSerializer.Serialize(stream, obj);
    }

    public static void SaveToStream(object obj, Stream stream, params Type[] items)
    {
        GetSerializer(obj.GetType(), items).Serialize(stream, obj);
    }

    public static byte[] SaveToBytes(object obj, params Type[] items)
    {
        XmlSerializer xmlSerializer = GetSerializer(obj.GetType(), items);
        using MemoryStream memoryStream = new MemoryStream();
        xmlSerializer.Serialize(memoryStream, obj);
        return memoryStream.ToArray();
    }

    public static void SaveToStreamTyped<_Ty>(object obj, Stream stream, params Type[] items)
    {
        GetSerializer(typeof(_Ty), items).Serialize(stream, obj);
    }

    public static string Serialize(object obj, params Type[] items)
    {
        if (obj == null)
        {
            return null;
        }
        XmlSerializer xmlSerializer = GetSerializer(obj.GetType(), items);
        using StringWriter stringWriter = new StringWriter();
        xmlSerializer.Serialize(stringWriter, obj);
        return stringWriter.ToString();
    }

    public static string SerializeAs<_Type>(object obj)
    {
        if (obj == null)
        {
            return null;
        }
        XmlSerializer xmlSerializer = GetSerializer(typeof(_Type), new Type[0]);
        using StringWriter stringWriter = new StringWriter();
        xmlSerializer.Serialize(stringWriter, obj);
        return stringWriter.ToString();
    }

    public static _Ty Deserialize<_Ty>(string text, params Type[] items)
    {
        if (string.IsNullOrEmpty(text))
        {
            return default;
        }
        XmlSerializer xmlSerializer = GetSerializer(typeof(_Ty), items);
        using StringReader textReader = new StringReader(text);
        return (_Ty)xmlSerializer.Deserialize(textReader);
    }

    public static void SaveToAppData<_Ty>(_Ty item, string text, string text2, string text3) where _Ty : SerializableObjectWithFileNameTracking
    {
        if (item.SavedFileName == null)
        {
            string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            folderPath = Path.Combine(folderPath, text);
            Directory.CreateDirectory(folderPath);
            char[] array = text2.ToCharArray();
            for (int num = 0; num < array.Length; num++)
            {
                if ("/\\@:<>*?[].".IndexOf(array[num]) != -1)
                {
                    array[num] = '_';
                }
            }
            item.SavedFileName = Path.Combine(folderPath, new string(array) + text3);
        }
        SaveToFile(item, item.SavedFileName);
    }

    public static List<_Ty> LoadAllFromAppData<_Ty>(string text2, string text3) where _Ty : SerializableObjectWithFileNameTracking
    {
        List<_Ty> list = new List<_Ty>();
        try
        {
            XmlSerializer xmlSerializer = new XmlSerializer(typeof(_Ty));
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), text2);
            Directory.CreateDirectory(path);
            string[] files = Directory.GetFiles(path, "*" + text3);
            foreach (string text in files)
            {
                try
                {
                    using FileStream stream = File.OpenRead(text);
                    _Ty value = (_Ty)xmlSerializer.Deserialize(stream);
                    value.SavedFileName = text;
                    list.Add(value);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
        return list;
    }
}
