using SharpVectors.Converters;
using SharpVectors.Dom;
using SharpVectors.Renderers.Wpf;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SmarTTY.GUI.TextView;

/// <summary>
/// The pictures of the images of a Markdown preview: read in the background (a file of the server through <see cref="ReadServerFile"/>, a path
/// from <c>/</c> first under the root of the git repository of the document, as GitHub shows it; a data address from itself; nothing from the
/// internet), decoded (the raster formats by the codecs of Windows, SVG by SharpVectors without its external resources) and kept by their key
/// (<see cref="MarkdownImages.Locate"/>) for the life of the preview, so that drawing the preview again after an edit finds them at once. A
/// picture that cannot be read or decoded is kept as its error. The callbacks run on the thread of the preview.
/// </summary>
internal sealed class MarkdownImageLoader
{
    /// <summary>Where a picture is.</summary>
    internal enum State
    {
        Loading,
        Loaded,
        Failed
    }

    /// <summary>A picture: its state, the image once it is loaded, the error once it failed.</summary>
    internal sealed class Entry
    {
        public State State;

        public ImageSource Image;

        public double Width;

        public double Height;

        public string Error;

        public readonly List<Action<Entry>> Waiting = new List<Action<Entry>>();
    }

    // A few pictures at a time: a README with fifty pictures should not open fifty channels at once.
    private static readonly SemaphoreSlim Parallel = new SemaphoreSlim(4);

    private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

    private readonly Dispatcher _dispatcher;

    private int _generation;

    // The root of the git repository of the document (the folder with .git), looked for once, on the first picture from "/".
    private readonly object _rootLock = new object();

    private bool _rootKnown;

    private string _repositoryRoot;

    public MarkdownImageLoader(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <summary>Reads a file of the server whole, up to a number of bytes (called on a background thread); null: no files of the server are read.</summary>
    public Func<string, int, byte[]> ReadServerFile { get; set; }

    /// <summary>Whether a path exists on the server (called on a background thread): finds the root of the repository; null: it is not looked for.</summary>
    public Func<string, bool> ServerPathExists { get; set; }

    /// <summary>Forgets the pictures (the document was loaded again from the server: its pictures may have changed too).</summary>
    public void Clear()
    {
        _generation++;
        _entries.Clear();
        lock (_rootLock)
        {
            _rootKnown = false;
            _repositoryRoot = null;
        }
    }

    /// <summary>
    /// The picture of an image address: loaded or failed already, or loading (then <paramref name="done"/> is called on the thread of the preview
    /// when it is). Null when the address is nowhere the preview reads.
    /// </summary>
    public Entry Request(string url, string documentPath, Action<Entry> done)
    {
        MarkdownImageOrigin origin = MarkdownImages.Locate(url, documentPath, out string key);
        if (origin == MarkdownImageOrigin.None || (origin == MarkdownImageOrigin.ServerFile && ReadServerFile == null))
        {
            return null;
        }
        if (_entries.TryGetValue(key, out Entry entry))
        {
            if (entry.State == State.Loading && done != null)
            {
                entry.Waiting.Add(done);
            }
            return entry;
        }
        entry = new Entry { State = State.Loading };
        if (done != null)
        {
            entry.Waiting.Add(done);
        }
        _entries[key] = entry;
        int generation = _generation;
        Func<string, int, byte[]> readServerFile = ReadServerFile;
        bool rootRelative = MarkdownImages.IsRootRelative(url);
        Task.Run(async () =>
        {
            byte[] bytes = null;
            string error = null;
            ImageSource raster = null;
            await Parallel.WaitAsync().ConfigureAwait(false);
            try
            {
                bytes = Read(origin, key, rootRelative, documentPath, readServerFile);
                switch (MarkdownImages.Detect(bytes))
                {
                    case MarkdownImageFormat.Raster:
                        raster = DecodeRaster(bytes);
                        break;
                    case MarkdownImageFormat.Svg:
                        // SVG is drawn on the thread of the preview (the renderer makes WPF objects).
                        break;
                    default:
                        error = "not a picture the preview can show";
                        break;
                }
            }
            catch (Exception exception)
            {
                error = exception.Message;
            }
            finally
            {
                Parallel.Release();
            }
            _ = _dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() => Finish(entry, generation, bytes, raster, error)));
        });
        return entry;
    }

    private byte[] Read(MarkdownImageOrigin origin, string key, bool rootRelative, string documentPath, Func<string, int, byte[]> readServerFile)
    {
        if (origin == MarkdownImageOrigin.Data)
        {
            if (!MarkdownImages.TryDecodeDataUri(key, out byte[] data))
            {
                throw new InvalidDataException("the data address does not decode");
            }
            return data;
        }
        string[] paths = MarkdownImages.ServerFileCandidates(key, rootRelative, rootRelative ? RepositoryRoot(documentPath) : null);
        Exception first = null;
        foreach (string path in paths)
        {
            try
            {
                return readServerFile(path, MarkdownImages.MaxBytes);
            }
            catch (Exception exception)
            {
                first = first ?? exception;
            }
        }
        throw first;
    }

    // The folder of the document or above it that holds .git, or null (asked of the server once).
    private string RepositoryRoot(string documentPath)
    {
        lock (_rootLock)
        {
            if (_rootKnown)
            {
                return _repositoryRoot;
            }
            _rootKnown = true;
            Func<string, bool> exists = ServerPathExists;
            if (exists == null)
            {
                return null;
            }
            foreach (string folder in MarkdownImages.RepositoryRootCandidates(documentPath))
            {
                try
                {
                    if (exists(folder + "/.git"))
                    {
                        _repositoryRoot = folder;
                        break;
                    }
                }
                catch (Exception)
                {
                    // No answer: the pictures are read from the root of the file system only.
                    break;
                }
            }
            return _repositoryRoot;
        }
    }

    // A frozen bitmap can be made on any thread and used on the thread of the preview.
    private static ImageSource DecodeRaster(byte[] bytes)
    {
        BitmapImage image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.StreamSource = new MemoryStream(bytes);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static ImageSource DecodeSvg(byte[] bytes)
    {
        WpfDrawingSettings settings = new WpfDrawingSettings
        {
            IncludeRuntime = false,
            TextAsGeometry = false,
            OptimizePath = true,
            // An SVG of a server or of the web reads no files and no addresses of its own.
            ExternalResourcesAccessMode = ExternalResourcesAccessModes.Ignore
        };
        using (FileSvgReader reader = new FileSvgReader(settings))
        using (MemoryStream stream = new MemoryStream(bytes))
        {
            DrawingGroup drawing = reader.Read(stream);
            if (drawing == null)
            {
                throw new InvalidDataException("the SVG has nothing to draw");
            }
            DrawingImage image = new DrawingImage(drawing);
            image.Freeze();
            return image;
        }
    }

    private void Finish(Entry entry, int generation, byte[] bytes, ImageSource image, string error)
    {
        if (generation != _generation)
        {
            // Forgotten meanwhile: a newer request reads it again.
            return;
        }
        if (image == null && error == null)
        {
            try
            {
                image = DecodeSvg(bytes);
            }
            catch (Exception exception)
            {
                error = exception.Message;
            }
        }
        if (image != null && (image.Width <= 0 || image.Height <= 0 || double.IsNaN(image.Width) || double.IsNaN(image.Height)))
        {
            image = null;
            error = "the picture has no size";
        }
        if (image != null)
        {
            entry.State = State.Loaded;
            entry.Image = image;
            // The pixels of a bitmap, not its size at the DPI written in the file (as a browser shows it).
            BitmapSource bitmap = image as BitmapSource;
            entry.Width = bitmap != null ? bitmap.PixelWidth : image.Width;
            entry.Height = bitmap != null ? bitmap.PixelHeight : image.Height;
        }
        else
        {
            entry.State = State.Failed;
            entry.Error = error;
        }
        List<Action<Entry>> waiting = new List<Action<Entry>>(entry.Waiting);
        entry.Waiting.Clear();
        foreach (Action<Entry> callback in waiting)
        {
            try
            {
                callback(entry);
            }
            catch (Exception)
            {
                // A preview that was drawn again meanwhile; the next drawing takes the picture from the entry.
            }
        }
    }
}
