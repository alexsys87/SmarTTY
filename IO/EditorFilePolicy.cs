namespace SmarTTY.IO;

/// <summary>What the editor does with a file of a given size.</summary>
public enum LargeFileAction
{
    /// <summary>Opened for editing without questions.</summary>
    Normal,

    /// <summary>Big enough to be slow and to use much memory: the user chooses to edit it, to read it only, or not to open it.</summary>
    Warn,

    /// <summary>Too big to edit: only the first <see cref="EditorFilePolicy.PreviewBytes"/> can be shown, read-only.</summary>
    PreviewOnly
}

/// <summary>
/// Limits for opening remote files in the editor. The editor keeps the whole text in memory, parses it into lines and
/// highlights it, so a file of hundreds of megabytes (a log, a database dump) would freeze the program.
/// </summary>
public static class EditorFilePolicy
{
    /// <summary>From this size (inclusive) the user is asked before the file is opened for editing.</summary>
    public const long WarnBytes = 8L * 1024 * 1024;

    /// <summary>From this size (inclusive) the file can only be previewed read-only.</summary>
    public const long PreviewOnlyBytes = 64L * 1024 * 1024;

    /// <summary>How much of a file that is too large is shown.</summary>
    public const long PreviewBytes = 8L * 1024 * 1024;

    public static LargeFileAction Classify(long size)
    {
        if (size >= PreviewOnlyBytes)
        {
            return LargeFileAction.PreviewOnly;
        }
        return size >= WarnBytes ? LargeFileAction.Warn : LargeFileAction.Normal;
    }
}
