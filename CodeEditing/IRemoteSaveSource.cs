using SmarTTY.TextEditing;

namespace SmarTTY.CodeEditing;

/// <summary>
/// What <see cref="RemoteFileSession.PrepareSave"/> needs from the editor to take the bytes to save: the text or the hex view, the two
/// changes it may make to the text, and the two questions it may ask. All calls are on the UI thread.
/// </summary>
public interface IRemoteSaveSource
{
    /// <summary>The hex view is shown (and has its document): the bytes are saved as they are.</summary>
    bool HexMode { get; }

    /// <summary>The version of the hex document (<see cref="HexMode"/> only).</summary>
    object HexVersion { get; }

    /// <summary>The bytes of the hex document (<see cref="HexMode"/> only).</summary>
    byte[] HexBytes();

    /// <summary>The text of the editor.</summary>
    string GetText();

    /// <summary>The version of the text document.</summary>
    object TextVersion { get; }

    /// <summary>The line breaks of the file as it was loaded, or as the user converted them.</summary>
    LineEndingKind LineEndings { get; }

    /// <summary>The encoding the file is saved in.</summary>
    TextFileEncoding FileEncoding { get; }

    /// <summary>Converts the line endings of the text to LF (the editor's <c>SetLineEnding</c>).</summary>
    void ConvertLineEndingsToLf();

    /// <summary>Saves the text as UTF-8 without a byte order mark (the editor's <c>SetSaveEncoding</c>).</summary>
    void SaveWithoutByteOrderMark();

    /// <summary>Asks a yes or no question (the Question icon): true for Yes.</summary>
    bool Confirm(string question);

    /// <summary>Removes the inline messages: the problems the previous check named are about the text as it was.</summary>
    void ClearMessages();

    /// <summary>The syntax of the file (the one chosen, or detected from the name and the first line).</summary>
    SyntaxHighlighting.Language DetectSyntax();

    /// <summary>The text of the first line (the check of the file may look at the #! line).</summary>
    string FirstLine();
}

/// <summary>The settings and the facts about the file that a save reads, taken when the text has been settled (after the questions).</summary>
public sealed class RemoteSaveOptions
{
    /// <summary>The folder on the server of the temporary copy.</summary>
    public string TemporaryDirectory;

    /// <summary>The first save of a file after it was loaded copies the old file to file~.</summary>
    public bool BackupBeforeSave;

    /// <summary>The check of the file before it is replaced is switched off.</summary>
    public bool DisableConfigCheck;

    /// <summary>The file did not exist when the editor was opened.</summary>
    public bool IsNewFile;

    /// <summary>The server is a Windows host.</summary>
    public bool IsWindowsHost;
}
