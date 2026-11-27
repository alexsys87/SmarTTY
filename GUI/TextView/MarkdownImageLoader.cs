using SharpVectors.Converters;
using SharpVectors.Dom;
using SharpVectors.Renderers.Wpf;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SmarTTY.GUI.TextView;

/// <summary>
/// The pictures of the images of a Markdown preview: read in the background (a file of the server through <see cref="ReadServerFile"/>, a web
/// address over HTTP, a data address from itself), decoded (the raster formats by the codecs of Windows, SVG by SharpVectors without its external
/// resources) and kept by their key (<see cref="MarkdownImages.Locate"/>) for the life of the preview, so that drawing the preview again after
/// an edit finds them at once. A picture that cannot be read or decoded is kept as its error. The callbacks run on the thread of the preview.
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

    private static readonly Lazy<HttpClient> Http = new Lazy<HttpClient>(() =>
    {
        HttpClient client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5
        })
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SmarTTY-MarkdownPreview/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("image/*");
        return client;
    });

    // A few pictures at a time: a README with fifty badges should not open fifty channels or connections at once.
    private static readonly SemaphoreSlim Parallel = new SemaphoreSlim(4);

    private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

    private readonly Dispatcher _dispatcher;

    private int _generation;

    public MarkdownImageLoader(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <summary>Reads a file of the server whole, up to a number of bytes (called on a background thread); null: no files of the server are read.</summary>
    public Func<string, int, byte[]> ReadServerFile { get; set; }

    /// <summary>Forgets the pictures (the document was loaded again from the server: its pictures may have changed too).</summary>
    public void Clear()
    {
        _generation++;
        _entries.Clear();
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
        Task.Run(async () =>
        {
            byte[] bytes = null;
            string error = null;
            ImageSource raster = null;
            await Parallel.WaitAsync().ConfigureAwait(false);
            try
            {
                bytes = await ReadAsync(origin, key, readServerFile).ConfigureAwait(false);
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

    private static async Task<byte[]> ReadAsync(MarkdownImageOrigin origin, string key, Func<string, int, byte[]> readServerFile)
    {
        switch (origin)
        {
            case MarkdownImageOrigin.Data:
                if (!MarkdownImages.TryDecodeDataUri(key, out byte[] data))
                {
                    throw new InvalidDataException("the data address does not decode");
                }
                return data;
            case MarkdownImageOrigin.ServerFile:
                return readServerFile(key, MarkdownImages.MaxBytes);
            default:
            {
                using (HttpResponseMessage response = await Http.Value.GetAsync(key, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength > MarkdownImages.MaxBytes)
                    {
                        throw new InvalidDataException("the picture is larger than 16 MB");
                    }
                    using (Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    {
                        MemoryStream bytes = new MemoryStream();
                        byte[] buffer = new byte[64 * 1024];
                        int read;
                        while ((read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                        {
                            bytes.Write(buffer, 0, read);
                            if (bytes.Length > MarkdownImages.MaxBytes)
                            {
                                throw new InvalidDataException("the picture is larger than 16 MB");
                            }
                        }
                        return bytes.ToArray();
                    }
                }
            }
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
