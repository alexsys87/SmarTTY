using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace SmarTTY.Transport;

/// <summary>
/// A register (an address and a width) in the table of Tools > Memory (devmem): its value in every form, whether it is read by the
/// automatic refresh, and when it last changed, for the highlight. Pure logic without WPF, tested by tests/DevMemTests.
/// </summary>
public sealed class MemoryRow : INotifyPropertyChanged
{
    private ulong? _Value;

    private ulong? _Previous;

    private string _Error;

    private string _Name;

    private bool _Watch = true;

    private DateTime? _ChangedAt;

    private bool _Highlighted;

    private int _Changes;

    public MemoryRow(ulong address, int width)
    {
        Address = address;
        Width = width;
    }

    public event PropertyChangedEventHandler PropertyChanged;

    public ulong Address { get; }

    /// <summary>The access width in bits: 8, 16, 32 or 64.</summary>
    public int Width { get; }

    /// <summary>The first address of the list, for the offset column (set by the window).</summary>
    public ulong BaseAddress { get; set; }

    public string AddressText => DevMem.AddressText(Address);

    public string OffsetText => Address >= BaseAddress ? "+0x" + (Address - BaseAddress).ToString("X", CultureInfo.InvariantCulture) : "-0x" + (BaseAddress - Address).ToString("X", CultureInfo.InvariantCulture);

    public string WidthText => Width.ToString(CultureInfo.InvariantCulture);

    /// <summary>A name given by the user ("CTRL", "STATUS").</summary>
    public string Name
    {
        get => _Name;
        set
        {
            _Name = value;
            Raise(nameof(Name));
        }
    }

    /// <summary>Read by the automatic refresh.</summary>
    public bool Watch
    {
        get => _Watch;
        set
        {
            _Watch = value;
            Raise(nameof(Watch));
        }
    }

    public ulong? Value => _Value;

    /// <summary>The value before the last change; null until the value has changed once.</summary>
    public ulong? Previous => _Previous;

    public string Error => _Error;

    public bool HasError => _Error != null;

    /// <summary>How often the value changed since it was first read.</summary>
    public int Changes => _Changes;

    public DateTime? ChangedAt => _ChangedAt;

    /// <summary>The value changed within the highlight time (see <see cref="UpdateHighlight"/>).</summary>
    public bool IsHighlighted => _Highlighted;

    public string Hex => _Value.HasValue ? DevMem.FormatHex(_Value.Value, Width) : _Error != null ? "error" : "";

    public string Unsigned => _Value.HasValue ? DevMem.FormatUnsigned(_Value.Value, Width) : "";

    public string Signed => _Value.HasValue ? DevMem.FormatSigned(_Value.Value, Width) : "";

    public string Binary => _Value.HasValue ? DevMem.FormatBinary(_Value.Value, Width) : "";

    public string Ascii => _Value.HasValue ? DevMem.FormatAscii(_Value.Value, Width) : "";

    public string Float => _Value.HasValue ? DevMem.FormatFloat(_Value.Value, Width) : "";

    /// <summary>The bits that changed with the last change, as a hex mask; empty before the first change.</summary>
    public string ChangedBits => _Value.HasValue && _Previous.HasValue ? DevMem.FormatHex(_Value.Value ^ _Previous.Value, Width) : "";

    public string PreviousText => _Previous.HasValue ? DevMem.FormatHex(_Previous.Value, Width) : "";

    public string ChangedAtText => _ChangedAt.HasValue ? _ChangedAt.Value.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) : "";

    /// <summary>
    /// Takes a new reading. A value that differs from the one before counts as a change (the first reading is none): it is
    /// remembered as the previous one, the time is kept and the row is highlighted.
    /// </summary>
    public bool Apply(DevMemReading reading, DateTime now)
    {
        bool changed = false;
        if (reading.Value.HasValue)
        {
            ulong value = reading.Value.Value & DevMem.Mask(Width);
            if (_Value.HasValue && _Value.Value != value)
            {
                _Previous = _Value;
                _ChangedAt = now;
                _Changes++;
                _Highlighted = true;
                changed = true;
            }
            _Value = value;
            _Error = null;
        }
        else
        {
            _Error = reading.Error ?? "no value";
        }
        RaiseValue();
        return changed;
    }

    /// <summary>Ends the highlight once <paramref name="duration"/> has passed since the last change; true when it changed.</summary>
    public bool UpdateHighlight(DateTime now, TimeSpan duration)
    {
        bool highlighted = _ChangedAt.HasValue && now - _ChangedAt.Value < duration;
        if (highlighted == _Highlighted)
        {
            return false;
        }
        _Highlighted = highlighted;
        Raise(nameof(IsHighlighted));
        return true;
    }

    /// <summary>Forgets the changes: the counter, the previous value and the highlight (the value stays).</summary>
    public void ResetChanges()
    {
        _Changes = 0;
        _Previous = null;
        _ChangedAt = null;
        _Highlighted = false;
        RaiseValue();
    }

    private void RaiseValue()
    {
        foreach (string property in new[] { nameof(Value), nameof(Previous), nameof(Error), nameof(HasError), nameof(Changes), nameof(ChangedAt), nameof(IsHighlighted),
            nameof(Hex), nameof(Unsigned), nameof(Signed), nameof(Binary), nameof(Ascii), nameof(Float), nameof(ChangedBits), nameof(PreviousText), nameof(ChangedAtText) })
        {
            Raise(property);
        }
    }

    public void RaiseOffset()
    {
        Raise(nameof(OffsetText));
    }

    private void Raise(string property)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    /// <summary>The rows of a region: <paramref name="count"/> registers of <paramref name="width"/> bits from <paramref name="start"/>.</summary>
    public static List<MemoryRow> Region(ulong start, int count, int width)
    {
        if (count <= 0 || count > MemoryWorkspace.MaxRows)
        {
            throw new ArgumentException("The number of registers must be between 1 and " + MemoryWorkspace.MaxRows + ".", nameof(count));
        }
        ulong step = (ulong)(width / 8);
        if (start > ulong.MaxValue - step * (ulong)(count - 1))
        {
            throw new ArgumentException("The region runs past the end of the address space.", nameof(count));
        }
        List<MemoryRow> rows = new List<MemoryRow>();
        for (int i = 0; i < count; i++)
        {
            rows.Add(new MemoryRow(start + step * (ulong)i, width) { BaseAddress = start });
        }
        return rows;
    }

    /// <summary>A line of the table for the clipboard or a CSV file.</summary>
    public string ToCsv()
    {
        return string.Join(",", new[] { AddressText, Quote(Name), WidthText, Hex, Unsigned, Signed, Quote(Binary), Quote(Ascii), Float, Changes.ToString(CultureInfo.InvariantCulture), Quote(Error) });
    }

    public const string CsvHeader = "Address,Name,Width,Hex,Unsigned,Signed,Binary,ASCII,Float,Changes,Error";

    private static string Quote(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }
        return text.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 || text != text.Trim() ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }
}

/// <summary>A register of a saved list.</summary>
public sealed class MemoryWorkspaceEntry
{
    [XmlAttribute("address")]
    public string Address { get; set; }

    [XmlAttribute("width")]
    public int Width { get; set; } = 32;

    [XmlAttribute("name")]
    public string Name { get; set; }

    [XmlAttribute("watch")]
    public bool Watch { get; set; } = true;
}

/// <summary>
/// A list of registers of the memory window with their names and the settings of the window, saved as XML: the last list is kept
/// next to Settings.xml (MemoryViewer.xml) and comes back when the window opens; lists can also be saved and opened as files.
/// </summary>
[XmlRoot("MemoryWorkspace")]
public sealed class MemoryWorkspace
{
    public const int MaxRows = 4096;

    public const string FileName = "MemoryViewer.xml";

    [XmlArray("Registers")]
    [XmlArrayItem("Register")]
    public List<MemoryWorkspaceEntry> Registers { get; set; } = new List<MemoryWorkspaceEntry>();

    /// <summary>The interval of the automatic refresh in milliseconds.</summary>
    public int RefreshMilliseconds { get; set; } = 1000;

    /// <summary>How long a change stays highlighted, in milliseconds.</summary>
    public int HighlightMilliseconds { get; set; } = 3000;

    /// <summary>The columns shown: a comma separated list of Hex, Unsigned, Signed, Binary, Ascii, Float, Changes.</summary>
    public string Columns { get; set; } = "Hex,Unsigned,Binary,Ascii,Changes";

    private static readonly XmlSerializer Serializer = new XmlSerializer(typeof(MemoryWorkspace));

    public static MemoryWorkspace FromRows(IEnumerable<MemoryRow> rows)
    {
        MemoryWorkspace workspace = new MemoryWorkspace();
        foreach (MemoryRow row in rows)
        {
            workspace.Registers.Add(new MemoryWorkspaceEntry { Address = row.AddressText, Width = row.Width, Name = string.IsNullOrWhiteSpace(row.Name) ? null : row.Name.Trim(), Watch = row.Watch });
        }
        return workspace;
    }

    /// <summary>The rows of the list; entries with a bad address or width are skipped and named in <paramref name="skipped"/>.</summary>
    public List<MemoryRow> ToRows(out List<string> skipped)
    {
        List<MemoryRow> rows = new List<MemoryRow>();
        skipped = new List<string>();
        ulong? first = null;
        foreach (MemoryWorkspaceEntry entry in Registers ?? new List<MemoryWorkspaceEntry>())
        {
            if (entry == null || !DevMem.TryParseAddress(entry.Address, out ulong address, out _) || !new[] { 8, 16, 32, 64 }.Contains(entry.Width) || rows.Count >= MaxRows)
            {
                skipped.Add(entry?.Address ?? "(empty)");
                continue;
            }
            first ??= address;
            rows.Add(new MemoryRow(address, entry.Width) { Name = entry.Name, Watch = entry.Watch, BaseAddress = first.Value });
        }
        return rows;
    }

    public void Save(string path)
    {
        string directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        string temporary = path + ".tmp";
        using (FileStream stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (XmlWriter writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
        {
            Serializer.Serialize(writer, this);
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

    /// <summary>The list in the file; throws for a damaged file or another kind of XML.</summary>
    public static MemoryWorkspace Load(string path)
    {
        using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        MemoryWorkspace workspace = (MemoryWorkspace)Serializer.Deserialize(reader);
        workspace.Registers ??= new List<MemoryWorkspaceEntry>();
        return workspace;
    }

    public IEnumerable<string> ColumnNames => (Columns ?? "").Split(',').Select(c => c.Trim()).Where(c => c.Length > 0);
}
