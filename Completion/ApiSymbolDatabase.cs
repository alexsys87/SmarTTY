using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using System.Xml.Serialization;
using SmarTTY.CodeEditing;

namespace SmarTTY.Completion;

/// <summary>
/// A database of the standard functions of the supported languages (the "API symbols"): Python's builtins,
/// the C library, the shell builtins and the like. It feeds both the editor's completion list (a symbol
/// appears with its short description) and the parameter hint (its signature is shown for the call).
///
/// The database has no UI dependency. The default set is embedded in the assembly
/// ("SmarTTY.Completion.ApiSymbols.xml", the file ApiSymbols.xml next to the sources) and can be replaced by
/// an ApiSymbols.xml next to the executable, so a user can extend it without a rebuild.
/// </summary>
[XmlRoot("ApiSymbols")]
public class ApiSymbolDatabase
{
    /// <summary>One standard symbol: the name, the language it belongs to and its documentation.</summary>
    public class Symbol
    {
        [XmlAttribute("language")]
        public string Language { get; set; }

        [XmlAttribute("name")]
        public string Name { get; set; }

        /// <summary>The signature ("print(value, sep=' ')"); empty for a constant like "True".</summary>
        [XmlAttribute("signature")]
        public string Signature { get; set; }

        /// <summary>A one-line description shown next to the name in the completion list.</summary>
        [XmlElement("Summary")]
        public string Summary { get; set; }

        /// <summary>A longer description shown in the hint popup.</summary>
        [XmlElement("Description")]
        public string Description { get; set; }

        [XmlIgnore]
        private ParameterSignature _parsed;

        [XmlIgnore]
        private bool _parsedOnce;

        [XmlIgnore]
        public ParameterSignature Parsed
        {
            get
            {
                if (!_parsedOnce)
                {
                    _parsed = ParameterSignature.Parse(Signature);
                    _parsedOnce = true;
                }
                return _parsed;
            }
        }
    }

    public Symbol[] Symbols { get; set; } = new Symbol[0];

    [XmlIgnore]
    public string LoadWarning { get; private set; }

    private readonly Dictionary<string, Dictionary<string, Symbol>> _byLanguage =
        new Dictionary<string, Dictionary<string, Symbol>>(StringComparer.Ordinal);

    // The last segment of a qualified name ("sqrt" for "math.sqrt"), for the parameter hint: the editor
    // reads the name of a call as the identifier before "(", which never contains the module part.
    private readonly Dictionary<string, Dictionary<string, Symbol>> _byShortName =
        new Dictionary<string, Dictionary<string, Symbol>>(StringComparer.Ordinal);

    /// <summary>ApiSymbols.xml next to the exe; it replaces the embedded set when it exists.</summary>
    public static string DefaultPath
    {
        get
        {
            // Location is empty for an assembly that was loaded from a byte array or a bundle.
            string location = Assembly.GetExecutingAssembly().Location;
            string directory = string.IsNullOrEmpty(location) ? AppDomain.CurrentDomain.BaseDirectory : Path.GetDirectoryName(location);
            return Path.Combine(directory ?? "", "ApiSymbols.xml");
        }
    }

    /// <summary>Loads the file next to the exe when it exists, otherwise the embedded default set.</summary>
    public static ApiSymbolDatabase Load()
    {
        try
        {
            if (File.Exists(DefaultPath))
            {
                using (FileStream file = File.OpenRead(DefaultPath))
                {
                    return Read(file);
                }
            }
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException ||
                                          exception is InvalidOperationException || exception is XmlException)
        {
            // A broken user file must not stop the editor: fall through to the embedded default set.
            System.Diagnostics.Trace.TraceWarning("Could not load API symbols from '" + DefaultPath + "': " + exception.Message);
        }
        return LoadEmbedded();
    }

    public static ApiSymbolDatabase LoadEmbedded()
    {
        try
        {
            using (Stream stream = typeof(ApiSymbolDatabase).Assembly.GetManifestResourceStream("SmarTTY.Completion.ApiSymbols.xml"))
            {
                if (stream == null)
                {
                    return new ApiSymbolDatabase();
                }
                return Read(stream);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException || exception is XmlException)
        {
            System.Diagnostics.Trace.TraceWarning("Could not read the embedded API symbols: " + exception.Message);
            return new ApiSymbolDatabase();
        }
    }

    /// <summary>Loads the symbols from an XML stream (used by the tests with the file from the sources).</summary>
    public static ApiSymbolDatabase Read(Stream stream)
    {
        XmlReaderSettings settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 4 * 1024 * 1024
        };
        using (XmlReader reader = XmlReader.Create(stream, settings))
        {
            ApiSymbolDatabase database = (ApiSymbolDatabase)new XmlSerializer(typeof(ApiSymbolDatabase)).Deserialize(reader);
            if (database == null)
            {
                throw new InvalidOperationException("The API symbol database is null.");
            }
            database.Symbols = (database.Symbols ?? new Symbol[0]).Where(symbol => symbol != null).ToArray();
            database.Index();
            return database;
        }
    }

    private void Index()
    {
        _byLanguage.Clear();
        _byShortName.Clear();
        foreach (Symbol symbol in Symbols)
        {
            if (string.IsNullOrEmpty(symbol.Name) || string.IsNullOrEmpty(symbol.Language))
            {
                continue;
            }
            Dictionary<string, Symbol> language = GetLanguage(_byLanguage, symbol.Language);
            // The first definition wins, so a hand-written file can override the embedded set by prepending.
            if (!language.ContainsKey(symbol.Name))
            {
                language.Add(symbol.Name, symbol);
            }
            // The hint sees only the identifier before "(", so a "math.sqrt" or "std::sort" entry is also
            // reachable as "sqrt" or "sort".
            string shortName = ShortName(symbol.Name);
            if (shortName.Length < symbol.Name.Length)
            {
                Dictionary<string, Symbol> shortNames = GetLanguage(_byShortName, symbol.Language);
                if (!shortNames.ContainsKey(shortName))
                {
                    shortNames.Add(shortName, symbol);
                }
            }
        }

        // C++ programs use the C library, so the C++ profile inherits the C entries. The C++ symbols were
        // indexed first, so a C definition with the same name does not override them. C files never see the
        // C++ additions: the merge goes in one direction only.
        if (_byLanguage.TryGetValue("CLike", out Dictionary<string, Symbol> cLibrary))
        {
            Dictionary<string, Symbol> cpp = GetLanguage(_byLanguage, "Cpp");
            foreach (KeyValuePair<string, Symbol> pair in cLibrary)
            {
                if (!cpp.ContainsKey(pair.Key))
                {
                    cpp.Add(pair.Key, pair.Value);
                }
            }
            if (_byShortName.TryGetValue("CLike", out Dictionary<string, Symbol> cShortNames))
            {
                Dictionary<string, Symbol> cppShortNames = GetLanguage(_byShortName, "Cpp");
                foreach (KeyValuePair<string, Symbol> pair in cShortNames)
                {
                    if (!cppShortNames.ContainsKey(pair.Key))
                    {
                        cppShortNames.Add(pair.Key, pair.Value);
                    }
                }
            }
        }
    }

    /// <summary>
    /// The last segment of a qualified name: "sqrt" for "math.sqrt", "sort" for "std::sort" and
    /// "push_back" for "std::vector::push_back". The bare name itself is returned when it is not qualified.
    /// </summary>
    public static string ShortName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }
        int start = 0;
        int dot = name.LastIndexOf('.');
        if (dot >= 0)
        {
            start = dot + 1;
        }
        int colon = name.LastIndexOf("::", StringComparison.Ordinal);
        if (colon >= 0 && colon + 2 > start)
        {
            start = colon + 2;
        }
        return name.Substring(start);
    }

    private static Dictionary<string, Symbol> GetLanguage(Dictionary<string, Dictionary<string, Symbol>> index, string language)
    {
        if (!index.TryGetValue(language, out Dictionary<string, Symbol> symbols))
        {
            symbols = new Dictionary<string, Symbol>(StringComparer.Ordinal);
            index.Add(language, symbols);
        }
        return symbols;
    }

    /// <summary>The symbols of a language; empty for a language without an API set (plain text, diff, ...).</summary>
    public IReadOnlyCollection<Symbol> ForLanguage(SyntaxHighlighting.Language language)
    {
        if (!_byLanguage.TryGetValue(LanguageName(language), out Dictionary<string, Symbol> symbols))
        {
            return new List<Symbol>();
        }
        return symbols.Values;
    }

    public Symbol Find(SyntaxHighlighting.Language language, string name)
    {
        if (name == null || !_byLanguage.TryGetValue(LanguageName(language), out Dictionary<string, Symbol> symbols))
        {
            return null;
        }
        return symbols.TryGetValue(name, out Symbol symbol) ? symbol : null;
    }

    /// <summary>
    /// The signature of a standard function of a language, for the parameter hint. The name is looked up
    /// as written ("math.sqrt") and then by its last segment ("sqrt"), so both a qualified and a bare call
    /// find their symbol.
    /// </summary>
    public ParameterSignature FindSignature(SyntaxHighlighting.Language language, string name)
    {
        if (name == null)
        {
            return null;
        }
        Symbol symbol = Find(language, name);
        if (symbol == null && _byShortName.TryGetValue(LanguageName(language), out Dictionary<string, Symbol> shortNames))
        {
            shortNames.TryGetValue(name, out symbol);
        }
        if (symbol == null && language == SyntaxHighlighting.Language.CMake &&
            _byLanguage.TryGetValue(LanguageName(language), out Dictionary<string, Symbol> cmake))
        {
            // CMake command names are case-insensitive: ADD_EXECUTABLE is the same as add_executable.
            foreach (KeyValuePair<string, Symbol> pair in cmake)
            {
                if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    symbol = pair.Value;
                    break;
                }
            }
        }
        if (symbol == null || string.IsNullOrEmpty(symbol.Signature))
        {
            return null;
        }
        return symbol.Parsed;
    }

    /// <summary>The value of the "language" attribute for a language (the name of the enum member).</summary>
    public static string LanguageName(SyntaxHighlighting.Language language)
    {
        return language.ToString();
    }
}
