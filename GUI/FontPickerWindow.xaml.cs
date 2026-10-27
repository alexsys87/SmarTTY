using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace SmarTTY.GUI;

/// <summary>The font chosen in <see cref="FontPickerWindow"/>.</summary>
public sealed class FontSelection
{
    public string FamilyName { get; }

    public double Size { get; }

    public bool Bold { get; }

    public bool Italic { get; }

    internal FontSelection(string familyName, double size, bool bold, bool italic)
    {
        FamilyName = familyName;
        Size = size;
        Bold = bold;
        Italic = italic;
    }
}

/// <summary>A family of the picker list: the name shown to the user and the <see cref="FontFamily"/> to draw with.</summary>
internal sealed class FontEntry
{
    public string Name { get; }

    public FontFamily Family { get; }

    public FontEntry(string name, FontFamily family)
    {
        Name = name;
        Family = family;
    }
}

/// <summary>
/// Themed replacement for the WinForms <c>FontDialog</c>: the same job (family, size, bold and italic) drawn with the
/// application styles, so it follows the light/dark theme like the other dialogs.
/// </summary>
/// <remarks>
/// The console draws text on a fixed character grid, so a proportional font would break it. The list therefore hides
/// the proportional fonts unless the user asks for them, and the family is measured instead of trusted: only families
/// whose characters have the same advance width pass as monospace.
/// </remarks>
public partial class FontPickerWindow : Window
{
    private const string PreviewSample = "AaBbCcXxYyZz 0123 !?{}[]() | .,;:'\"  ~  ls -la /etc  ->  hello, world";

    private const double DefaultSize = 12;

    /// <summary>WPF sizes text in DIPs (1/96 inch) while <c>System.Drawing.Font.Size</c> is in points (1/72 inch).</summary>
    private const double PointsToDips = 96.0 / 72.0;

    private static readonly double[] CommonSizes = new double[] { 6, 7, 8, 9, 10, 11, 12, 14, 16, 18, 20, 22, 24, 26, 28, 32, 36, 48, 72 };

    /// <summary>Monospace answers per family name, so the list is measured once.</summary>
    private static readonly Dictionary<string, bool> _monospaceCache = new Dictionary<string, bool>();

    private readonly List<FontEntry> _allFamilies;

    private readonly double _pixelsPerDip;

    private readonly double _initialSize;

    private bool _ready;

    private FontPickerWindow(string familyName, double size, bool bold, bool italic)
    {
        InitializeComponent();
        // Needed by FormattedText for the monospace probe; the system DPI is the best answer before the window is shown.
        try
        {
            _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        }
        catch
        {
            _pixelsPerDip = 1.0;
        }

        // The system list is unsorted and holds several entries per family; one per name is enough.
        _allFamilies = Fonts.SystemFontFamilies
            .Where(family => family != null)
            .Select(family => new FontEntry(DisplayName(family), family))
            .GroupBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => group.First())
            .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        SizeBox.ItemsSource = CommonSizes;
        BoldBox.IsChecked = bold;
        ItalicBox.IsChecked = italic;
        _initialSize = size <= 0 ? DefaultSize : size;

        _ready = true;
        // RefreshFontList fills the list and selects its first entry; the configured family is preferred when it
        // survived the monospace filter, so an uninstalled or proportional one simply falls back to the first entry.
        RefreshFontList();
        FontEntry requested = _allFamilies.FirstOrDefault(entry => string.Equals(entry.Name, familyName, StringComparison.CurrentCultureIgnoreCase));
        if (requested != null && FontList.Items.Contains(requested))
        {
            FontList.SelectedItem = requested;
        }
        SetSize(_initialSize);
        UpdatePreview();
        Loaded += (sender, e) =>
        {
            // The editable text box only exists once the template is applied, so the typed size is set again here.
            SetSize(_initialSize);
            FontList.Focus();
        };
    }

    /// <summary>
    /// Shows the picker over <paramref name="owner"/>. Returns the chosen font, or null when the dialog was cancelled.
    /// </summary>
    public static FontSelection Pick(Window owner, string familyName, double size, bool bold, bool italic)
    {
        FontPickerWindow window = new FontPickerWindow(familyName, size, bold, italic);
        WindowOwnership.Attach(window, owner ?? WindowOwnership.ActiveWindow());
        return window.ShowDialog() == true ? window.Selection : null;
    }

    private FontSelection Selection
    {
        get
        {
            FontEntry entry = FontList.SelectedItem as FontEntry;
            if (entry == null)
            {
                return null;
            }
            return new FontSelection(entry.Name, CurrentSize(), BoldBox.IsChecked == true, ItalicBox.IsChecked == true);
        }
    }

    // ------------------------------------------------------------------ list and filter

    private void RefreshFontList()
    {
        string filter = FilterBox.Text?.Trim() ?? string.Empty;
        bool monospaceOnly = MonospaceOnlyBox.IsChecked == true;

        List<FontEntry> visible = _allFamilies
            .Where(entry => filter.Length == 0 || entry.Name.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0)
            .Where(entry => !monospaceOnly || IsMonospace(entry))
            .ToList();

        FontEntry selected = FontList.SelectedItem as FontEntry;
        FontList.ItemsSource = visible;
        // The selection has to survive a filter change, so a font that is still visible stays selected.
        FontList.SelectedItem = (selected != null && visible.Contains(selected)) ? selected : visible.FirstOrDefault();
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_ready)
        {
            RefreshFontList();
        }
    }

    private void MonospaceOnly_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready)
        {
            RefreshFontList();
        }
    }

    private void FontList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready)
        {
            UpdatePreview();
        }
    }

    // ------------------------------------------------------------------ size

    private void SetSize(double size)
    {
        // The selected entry is set before the text: clearing the selection would wipe the text of an editable box.
        double match = CommonSizes.FirstOrDefault(candidate => Math.Abs(candidate - size) < 0.01);
        if (match > 0)
        {
            SizeBox.SelectedItem = match;
        }
        else
        {
            // A size that is not in the list stays a typed value, so the selection is dropped without touching the text.
            SizeBox.SelectedIndex = -1;
        }
        SizeBox.Text = size.ToString(CultureInfo.CurrentCulture);
    }

    private double CurrentSize()
    {
        double size;
        if (!double.TryParse(SizeBox.Text?.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out size) || size <= 0)
        {
            // Unparsable or empty: keep the size that was there instead of failing the dialog.
            return _initialSize;
        }
        return Math.Min(size, 200);
    }

    private void SizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && SizeBox.SelectedItem is double)
        {
            UpdatePreview();
        }
    }

    private void SizeBox_KeyUp(object sender, KeyEventArgs e)
    {
        if (_ready && e.Key == Key.Enter)
        {
            UpdatePreview();
        }
    }

    private void SizeBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_ready)
        {
            // Typed values are the point of the editable box, so a valid one is kept even when it is not in the list.
            SetSize(CurrentSize());
            UpdatePreview();
        }
    }

    private void Style_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready)
        {
            UpdatePreview();
        }
    }

    // ------------------------------------------------------------------ preview

    private void UpdatePreview()
    {
        FontEntry entry = FontList.SelectedItem as FontEntry;
        if (entry == null)
        {
            PreviewText.Text = string.Empty;
            return;
        }
        PreviewText.FontFamily = entry.Family;
        // The preview shows the point size at its real point size, not at the same number of DIPs, so what the user
        // sees is what the terminal gets.
        PreviewText.FontSize = CurrentSize() * PointsToDips;
        PreviewText.FontWeight = BoldBox.IsChecked == true ? FontWeights.Bold : FontWeights.Normal;
        PreviewText.FontStyle = ItalicBox.IsChecked == true ? FontStyles.Italic : FontStyles.Normal;
        PreviewText.Text = PreviewSample;
    }

    // ------------------------------------------------------------------ monospace check

    private static string DisplayName(FontFamily family)
    {
        // The English name is what System.Drawing.Font expects when the font is applied, so it is the one to keep.
        if (family.FamilyNames.TryGetValue(XmlLanguage.GetLanguage("en-US"), out string name) && !string.IsNullOrEmpty(name))
        {
            return name;
        }
        return family.Source;
    }

    private bool IsMonospace(FontEntry entry)
    {
        if (entry == null)
        {
            return false;
        }
        lock (_monospaceCache)
        {
            if (_monospaceCache.TryGetValue(entry.Name, out bool cached))
            {
                return cached;
            }
        }
        bool monospace = MeasureMonospace(entry.Family);
        lock (_monospaceCache)
        {
            _monospaceCache[entry.Name] = monospace;
        }
        return monospace;
    }

    /// <summary>
    /// True when the glyphs of a monospace probe have the same advance width. 'i', 'l', 'W' and 'M' differ in every
    /// proportional font, so comparing the extremes catches them; a symbol font has no glyphs to compare and counts
    /// as proportional.
    /// </summary>
    private bool MeasureMonospace(FontFamily family)
    {
        try
        {
            double min = double.MaxValue;
            double max = double.MinValue;
            Typeface typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            foreach (char character in new[] { 'i', 'l', 'W', 'M' })
            {
                FormattedText text = new FormattedText(character.ToString(), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    typeface, 20, Brushes.Black, _pixelsPerDip);
                if (text.Width <= 0)
                {
                    return false;
                }
                min = Math.Min(min, text.Width);
                max = Math.Max(max, text.Width);
            }
            // WPF snaps advances to whole pixels, so a small tolerance keeps the extremes matching in a real
            // monospace font.
            return max - min <= 0.5;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ OK

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        FontSelection selection = Selection;
        if (selection == null)
        {
            return;
        }
        if (!IsMonospace(FontList.SelectedItem as FontEntry))
        {
            MessageDialog.Show("The font you have selected is not a monospace font. The console window requires a monospace font, e.g. Courier New.",
                "SmarTTY", MessageDialogIcon.Warning, MessageDialogButtons.Ok, owner: this);
            return;
        }
        DialogResult = true;
    }
}
