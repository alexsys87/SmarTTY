using SmarTTY.TextEditing;

namespace SmarTTY.CodeEditing;

/// <summary>A save in progress: the text and the temporary file it is uploaded to (moved from <c>RemoteFileEditor.SaveJob</c> as it was).</summary>
public sealed class RemoteSaveJob
{
    public byte[] Bytes;

    public string TemporaryPath;

    // A POSIX sh script (RemoteSaveCommand.BuildScript): run through PosixShell.Wrap, or through "sudo sh -c".
    public string ReplaceCommand;

    // The replacement failed and the old content could not be put back: the uploaded text and the restore copy are all there
    // is, so they are not deleted (RemoteFileSaver.RemoveTemporaryFile).
    public bool KeepTemporaryFiles;

    // The text version being saved (EditableTextBrowser.CurrentVersion), or the hex version
    // (HexDocument.CurrentVersion) when HexMode is set.
    public object Version;

    // The save came from the hex view: CompleteSave marks the hex document saved instead of the text.
    public bool HexMode;

    // Saves started later supersede this one (see the save generation of the editor).
    public int Generation;

    // The user already agreed to overwrite what changed on the server.
    public bool SkipConflictCheck;

    // The command also copies the old file to file~.
    public bool IncludesBackup;

    // The file on the server after the save (RemoteFileServer.FetchSignature).
    public string NewSignature;

    // The encoding the bytes were made with: they become the file as it is on the server.
    public TextFileEncoding Encoding;

    // The check run on the uploaded file before it replaces the target (null: none), and what it found if it failed.
    public ConfigValidator Validator;

    public ValidationResult Validation;
}
