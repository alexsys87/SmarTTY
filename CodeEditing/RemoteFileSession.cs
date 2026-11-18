using SmarTTY.IO;
using SmarTTY.TextEditing;
using System;

namespace SmarTTY.CodeEditing;

/// <summary>
/// The state a remote file editor keeps about saving, and the taking of the bytes to save. It is what <c>RemoteFileEditor</c> had in its fields
/// (<c>_convertCrLf</c>, <c>_bomAnswered</c>, <c>_backupDone</c>, <c>_forceOverwrite</c>, <c>_skipValidation</c>, <c>_saveGeneration</c>, the bytes kept
/// from the hex view) and in <c>PrepareSave</c>, moved here as it was. The loading, the server and the draft have their own classes
/// (<see cref="RemoteFileLoader"/>, <see cref="RemoteFileServer"/>, <see cref="RemoteFileDrafts"/>); this class is where the rest of the session will go.
/// </summary>
public sealed class RemoteFileSession
{
    private readonly string _remotePath;

    /// <param name="remotePath">The path of the file on the server.</param>
    public RemoteFileSession(string remotePath)
    {
        _remotePath = remotePath;
    }

    /// <summary>The user's answer to "Convert CRLF line endings to LF?", so that it is asked once per file.</summary>
    public bool? ConvertCrLf { get; set; }

    /// <summary>The question about a byte order mark before "#!" is asked once per file.</summary>
    public bool BomAnswered { get; set; }

    /// <summary>The file~ copy of the original is made by the first save of the file after it was loaded.</summary>
    public bool BackupDone { get; set; }

    /// <summary>The conflict question was answered "overwrite": the next save does not check the server again.</summary>
    public bool ForceOverwrite { get; set; }

    /// <summary>The user chose to save although the check of the file found problems: the next save does not run the check.</summary>
    public bool SkipValidation { get; set; }

    /// <summary>The number of the last save that was prepared: a save started later supersedes the ones before it.</summary>
    public int SaveGeneration { get; private set; }

    /// <summary>
    /// The bytes the hex view had when it was left, and the text they were shown as: saving the text before it is edited writes these bytes
    /// back exactly, which keeps a file in an encoding that does not round-trip intact.
    /// </summary>
    public byte[] HexBytesForTextSave { get; set; }

    /// <summary>The text that <see cref="HexBytesForTextSave"/> were shown as.</summary>
    public string HexTextSnapshot { get; set; }

    /// <summary>
    /// Takes the bytes to save (UI thread), asking once whether to convert CRLF line endings that a file which did not
    /// have them has got, and whether to drop a byte order mark that breaks a script.
    /// </summary>
    public RemoteSaveJob PrepareSave(IRemoteSaveSource source, Func<RemoteSaveOptions> getOptions)
    {
        // The problems the previous check named are about the text as it was: the check runs again now.
        source.ClearMessages();
        bool hexMode = source.HexMode;
        object version;
        byte[] bytes;
        if (hexMode)
        {
            // The bytes are saved as they are: the CRLF and BOM questions are about text that is not shown here.
            version = source.HexVersion;
            bytes = source.HexBytes();
        }
        else
        {
            string value = source.GetText();
            // Bytes that came from the hex view are saved back exactly while the text has not been edited since: an
            // encoding that does not round-trip intact must not change the file.
            bool exactBytes = HexBytesForTextSave != null && HexTextSnapshot == value;
            if (!exactBytes)
            {
                if (value.Contains("\r\n") && source.LineEndings != LineEndingKind.CrLf)
                {
                    if (!ConvertCrLf.HasValue)
                    {
                        ConvertCrLf = source.Confirm("The text has CRLF line endings, which break shell scripts and OpenWrt configuration files. Convert them to LF endings?");
                    }
                    if (ConvertCrLf.Value)
                    {
                        source.ConvertLineEndingsToLf();
                    }
                }
                value = source.GetText();
                if (source.FileEncoding == TextFileEncodings.Utf8Bom && value.StartsWith("#!", StringComparison.Ordinal) && !BomAnswered)
                {
                    BomAnswered = true;
                    if (source.Confirm("The file starts with #! and is saved with a UTF-8 byte order mark, which makes the system fail to run it as a script. Save it without the mark?"))
                    {
                        source.SaveWithoutByteOrderMark();
                    }
                }
                value = source.GetText();
                exactBytes = HexBytesForTextSave != null && HexTextSnapshot == value;
            }
            version = source.TextVersion;
            bytes = exactBytes ? HexBytesForTextSave : TextFileEncodings.Encode(value, source.FileEncoding);
        }
        RemoteSaveOptions options = getOptions();
        string temporaryPath = options.TemporaryDirectory + "/" + Guid.NewGuid().ToString() + ".txt";
        bool backup = options.BackupBeforeSave && !options.IsNewFile && !BackupDone;
        // The file is written in place, which keeps its owner, mode and links, and that truncates it first. So the old content is
        // kept until the result has been read back, and the script puts it back when the space ran out or the write failed half
        // way. A failed backup copy ("~", as with other editors) does not stop the save. The paths are quoted by the script: a
        // file name with ", $ or ` must neither break the command nor expand.
        string replaceCommand = RemoteSaveCommand.BuildScript(_remotePath, temporaryPath, bytes.Length, backup);
        // The check of the file runs on the text; the bytes of the hex view are saved as they are.
        ConfigValidator validator = null;
        if (!hexMode && !options.IsWindowsHost && !SkipValidation && !options.DisableConfigCheck)
        {
            validator = ConfigValidators.Find(_remotePath, source.DetectSyntax(), source.FirstLine());
        }
        RemoteSaveJob job = new RemoteSaveJob
        {
            Bytes = bytes,
            TemporaryPath = temporaryPath,
            ReplaceCommand = replaceCommand,
            Version = version,
            HexMode = hexMode,
            Generation = ++SaveGeneration,
            SkipConflictCheck = ForceOverwrite,
            IncludesBackup = backup,
            Encoding = source.FileEncoding,
            Validator = validator
        };
        ForceOverwrite = false;
        SkipValidation = false;
        return job;
    }
}
