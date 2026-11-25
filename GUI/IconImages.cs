using SmarTTY.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SmarTTY.GUI;

/// <summary>
/// The icons of the application as vector images (stage 5A). They are the <c>DrawingImage</c>s of the resource dictionaries in
/// <c>GUI/Icons/</c> (converted from SVG, one dictionary for each folder the PNG files were in: <c>Root</c> for <c>icons/</c>,
/// <c>MainWindow</c>, <c>Editor</c>, <c>Menu</c>, <c>Keys</c>, <c>Transfers</c>, <c>FileTypes</c>), found by the path the PNG had
/// (the rules are <see cref="Utilities.IconPaths"/>). Each image is shown at the size WPF gave its PNG, so no layout changed with the
/// switch; the images are frozen and shared.
/// </summary>
internal static class IconImages
{
    private static readonly Dictionary<string, ResourceDictionary> _loaded = new Dictionary<string, ResourceDictionary>(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, ImageSource> _images = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The image of an icon path (see the class), or null when there is no such icon. Frozen, so any thread can use it.
    /// </summary>
    public static ImageSource Get(string path)
    {
        if (!IconPaths.TryParse(path, out string folder, out string name))
        {
            return null;
        }
        string id = folder.Length == 0 ? name : folder + "/" + name;
        lock (_images)
        {
            if (_images.TryGetValue(id, out ImageSource cached))
            {
                return cached;
            }
            ImageSource image = Load(folder, name);
            _images[id] = image;
            return image;
        }
    }

    /// <summary>
    /// The image of an icon path, or of a picture file of this computer (a connection may show one the user chose). Null when it is
    /// neither or the file cannot be read.
    /// </summary>
    public static ImageSource FromIconOrFile(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }
        try
        {
            if (Path.IsPathRooted(path) && File.Exists(path))
            {
                BitmapImage bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(path);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
        }
        catch (Exception)
        {
            return null;
        }
        return Get(path);
    }

    private static ImageSource Load(string folder, string name)
    {
        ResourceDictionary dictionary = Dictionary(IconPaths.DictionaryOf(folder));
        if (!(dictionary?[IconPaths.KeyOf(name)] is DrawingImage source) || source.Drawing == null)
        {
            return null;
        }
        Drawing drawing = source.Drawing;
        if (drawing.CanFreeze)
        {
            drawing.Freeze();
        }
        // The drawing is in the units of its SVG (the clip of its group is the view box), scaled to the size of the PNG.
        Rect box = drawing is DrawingGroup group && group.ClipGeometry != null ? group.ClipGeometry.Bounds : drawing.Bounds;
        if (box.IsEmpty || box.Width <= 0 || box.Height <= 0)
        {
            return null;
        }
        double size = IconPaths.SizeOf(folder, name);
        DrawingGroup scaled = new DrawingGroup
        {
            Transform = new MatrixTransform(size / box.Width, 0, 0, size / box.Height, -box.X * size / box.Width, -box.Y * size / box.Height)
        };
        scaled.Children.Add(drawing);
        DrawingGroup sized = new DrawingGroup { ClipGeometry = new RectangleGeometry(new Rect(0, 0, size, size)) };
        // A transparent square of the whole size, so the image is exactly that size whatever the drawing covers.
        sized.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, size, size))));
        sized.Children.Add(scaled);
        DrawingImage image = new DrawingImage(sized);
        if (image.CanFreeze)
        {
            image.Freeze();
        }
        return image;
    }

    private static ResourceDictionary Dictionary(string file)
    {
        lock (_loaded)
        {
            if (!_loaded.TryGetValue(file, out ResourceDictionary dictionary))
            {
                dictionary = (ResourceDictionary)Application.LoadComponent(new Uri("/SmarTTY;component/GUI/Icons/" + file + ".xaml", UriKind.Relative));
                _loaded[file] = dictionary;
            }
            return dictionary;
        }
    }
}

/// <summary>
/// An icon in XAML: <c>Source="{gui:Icon MainWindow/refresh16.png}"</c> is the image of <see cref="IconImages.Get"/> for that path (the
/// path the PNG had under <c>icons/</c>).
/// </summary>
[MarkupExtensionReturnType(typeof(ImageSource))]
public sealed class IconExtension : MarkupExtension
{
    public IconExtension()
    {
    }

    public IconExtension(string path)
    {
        Path = path;
    }

    /// <summary>The path of the icon under <c>icons/</c>, as the PNG had it.</summary>
    [ConstructorArgument("path")]
    public string Path { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return IconImages.Get(Path);
    }
}
