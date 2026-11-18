using System;

namespace SmarTTY.CodeEditing;

/// <summary>
/// What the loading thread of the remote file editor needs from the editor: whether it is a reload, whether the tab was closed, and the
/// four things it tells it. The four methods are called on the UI thread (through <see cref="IUiDispatcher"/>).
/// </summary>
public interface IRemoteFileLoadView
{
    /// <summary>A reload of the opened file is running (the first load of the file is not).</summary>
    bool IsReloading { get; }

    /// <summary>The tab was closed: nothing is waiting for the file any more.</summary>
    bool IsClosed { get; }

    /// <summary>Shows the text downloaded so far (a preview of a file that is still coming).</summary>
    void ShowPreview(string text);

    /// <summary>The whole file has been downloaded (null for a new file) with its signature on the server.</summary>
    void Finish(byte[] file, string signature);

    /// <summary>The user chose not to open the file.</summary>
    void Cancelled();

    /// <summary>The file could not be loaded.</summary>
    void Failed(Exception exception);
}
