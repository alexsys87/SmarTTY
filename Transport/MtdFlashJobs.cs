using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace SmarTTY.Transport;

/// <summary>The outcome of a command on the server.</summary>
public sealed class RemoteCommandResult
{
    /// <summary>The exit status; null when the command could not be run or the server reported none.</summary>
    public int? ExitStatus { get; set; }

    /// <summary>The standard output, by lines (for the commands whose output is text).</summary>
    public List<string> Output { get; set; } = new List<string>();

    /// <summary>The standard error.</summary>
    public string Error { get; set; } = "";

    /// <summary>The bytes that went through the standard input or into the output stream.</summary>
    public long Bytes { get; set; }

    public bool ConnectionLost { get; set; }

    public bool Succeeded => ExitStatus == 0 && !ConnectionLost;

    /// <summary>The reason of a failure for a message: the error output, or the exit status.</summary>
    public string Describe()
    {
        if (ConnectionLost)
        {
            return "the connection to the server was lost";
        }
        string error = (Error ?? "").Trim();
        if (error.Length == 0)
        {
            error = string.Join(" ", (Output ?? new List<string>()).Where(l => !string.IsNullOrWhiteSpace(l)).Take(5)).Trim();
        }
        string status = ExitStatus.HasValue ? "exit status " + ExitStatus.Value : "no exit status";
        return error.Length > 0 ? error + " (" + status + ")" : status;
    }
}

/// <summary>
/// Runs commands on the server for the flash jobs: an SSH session in the window, a local shell in the tests. The jobs themselves
/// (<see cref="MtdBackupJob"/>, <see cref="MtdRestoreJob"/>) only use these three calls.
/// </summary>
public interface IRemoteShell
{
    /// <summary>Runs a command and returns its output as text.</summary>
    RemoteCommandResult Run(string command);

    /// <summary>Runs a command with <paramref name="input"/> as its standard input, which is closed at its end.</summary>
    RemoteCommandResult RunWithInput(string command, Stream input, Action<long> progress, Func<bool> isCancelled);

    /// <summary>Runs a command and copies its standard output to <paramref name="output"/> as it arrives.</summary>
    RemoteCommandResult RunToStream(string command, Stream output, Action<long> progress, Func<bool> isCancelled);
}

/// <summary>A failure of a flash job, with the text for the user.</summary>
public sealed class MtdJobException : Exception
{
    public MtdJobException(string message) : base(message)
    {
    }
}

/// <summary>Computes SHA-256 and MD5 of everything written through it, on the way to <paramref name="inner"/> (which may be null).</summary>
public sealed class HashingStream : Stream
{
    private readonly Stream _inner;

    private readonly SHA256 _sha256 = SHA256.Create();

    private readonly MD5 _md5 = MD5.Create();

    private bool _finished;

    public long Length64 { get; private set; }

    public HashingStream(Stream inner)
    {
        _inner = inner;
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => Length64;

    public override long Position
    {
        get => Length64;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
        _inner?.Flush();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _inner?.Write(buffer, offset, count);
        _sha256.TransformBlock(buffer, offset, count, null, 0);
        _md5.TransformBlock(buffer, offset, count, null, 0);
        Length64 += count;
    }

    private void Finish()
    {
        if (!_finished)
        {
            _sha256.TransformFinalBlock(new byte[0], 0, 0);
            _md5.TransformFinalBlock(new byte[0], 0, 0);
            _finished = true;
        }
    }

    public string Sha256
    {
        get
        {
            Finish();
            return TarArchiveReader.ToHex(_sha256.Hash);
        }
    }

    public string Md5
    {
        get
        {
            Finish();
            return TarArchiveReader.ToHex(_md5.Hash);
        }
    }

    public string Hash(string algorithm)
    {
        return algorithm == "sha256" ? Sha256 : Md5;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _sha256.Dispose();
            _md5.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Reads at most <paramref name="length"/> bytes of <paramref name="inner"/> from where it stands (a partition inside a full flash image).</summary>
public sealed class SliceStream : Stream
{
    private readonly Stream _inner;

    private long _left;

    public SliceStream(Stream inner, long length)
    {
        _inner = inner;
        _left = length;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_left <= 0)
        {
            return 0;
        }
        int read = _inner.Read(buffer, offset, (int)Math.Min(count, _left));
        _left -= Math.Max(0, read);
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// The backup of flash partitions: each one is read with cat into <paramref name="output"/> (the file on this computer) while its
/// SHA-256 and MD5 are computed here, the size must be the size of the partition, and then the server computes the checksum of
/// the partition again from the flash: the two must agree, otherwise the flash reads differently each time and the image is not
/// trusted. Nothing is written on the server.
/// </summary>
public static class MtdBackupJob
{
    public static MtdImagePart BackUp(IRemoteShell shell, MtdHostInfo info, MtdPartition partition, Stream output, string fileName, long fileOffset, bool useSudo,
        Action<string> phase, Action<long> progress, Func<bool> isCancelled, Action<string> log)
    {
        phase?.Invoke("Reading " + partition.Label + "...");
        using HashingStream hashing = new HashingStream(output);
        RemoteCommandResult read = shell.RunToStream(MtdFlash.BuildReadCommand(partition, useSudo), hashing, progress, isCancelled);
        if (isCancelled?.Invoke() == true)
        {
            throw new OperationCanceledException();
        }
        if (!read.Succeeded)
        {
            throw new MtdJobException("Reading " + partition.Label + " failed: " + read.Describe() + (partition.IsNand ? " (an uncorrectable ECC error on NAND makes cat stop)" : ""));
        }
        if (hashing.Length64 != partition.Size)
        {
            throw new MtdJobException("Reading " + partition.Label + " gave " + hashing.Length64 + " bytes instead of " + partition.Size + ".");
        }
        MtdImagePart part = new MtdImagePart
        {
            File = fileName,
            FileOffset = fileOffset,
            Size = partition.Size,
            MtdIndex = partition.Index,
            Name = partition.Name,
            FlashOffset = partition.Offset,
            EraseSize = partition.EraseSize,
            Type = partition.Type,
            Sha256 = hashing.Sha256,
            Md5 = hashing.Md5
        };
        string algorithm = info.HashAlgorithm;
        if (algorithm == null)
        {
            log?.Invoke(partition.Label + ": the server has neither sha256sum nor md5sum, so the image is not compared with the flash.");
            return part;
        }
        phase?.Invoke("Checking " + partition.Label + " (" + MtdFlash.HashProgram(algorithm) + " on the server)...");
        RemoteCommandResult hash = shell.Run(MtdFlash.BuildDeviceHashCommand(partition.Device, partition.Size, algorithm, info.HeadHasBytes, useSudo));
        string remote = hash.Succeeded ? MtdFlash.ParseHash(hash.Output, algorithm) : null;
        if (remote == null)
        {
            throw new MtdJobException("The checksum of " + partition.Label + " could not be computed on the server: " + hash.Describe());
        }
        string local = hashing.Hash(algorithm);
        if (remote != local)
        {
            throw new MtdJobException("The image of " + partition.Label + " differs from the flash (" + algorithm + " here " + local + ", on the server " + remote + "): the flash reads differently each time, the image is not trusted.");
        }
        log?.Invoke(partition.Label + ": " + MtdFlash.FormatBytes(partition.Size) + ", " + algorithm + " " + local + " matches the flash.");
        return part;
    }
}

/// <summary>The steps of a restore, for the progress of the window.</summary>
public enum MtdRestoreStep
{
    CheckingImage,
    CheckingMemory,
    Uploading,
    CheckingUpload,
    Writing,
    CheckingFlash,
    CleaningUp
}

/// <summary>
/// The restore of one image into one partition, in the order that leaves nothing to chance:
/// 1. the image is read here and compared with the checksum stored with the backup (a damaged file stops it);
/// 2. the server is asked how much memory it has, and a RAM file system with room for the image is chosen;
/// 3. the image is copied into it; 4. the server computes its checksum there, which must be the one of the image;
/// 5. the flash tool writes it into the partition; 6. the server reads the partition back and its checksum must again be the one
/// of the image. Only then is the write done. The image in RAM is removed in any case.
/// </summary>
public static class MtdRestoreJob
{
    public static void Restore(IRemoteShell shell, Func<MtdHostInfo> refreshInfo, MtdImagePart part, Func<Stream> openImage, MtdPartition partition, bool useSudo, string unique,
        Action<MtdRestoreStep, string> phase, Action<long> progress, Func<bool> isCancelled, Action<string> log)
    {
        // 1. The image on this computer.
        phase?.Invoke(MtdRestoreStep.CheckingImage, "Checking the image of " + partition.Label + " on this computer...");
        string sha256;
        string md5;
        using (Stream image = openImage())
        using (HashingStream hashing = new HashingStream(null))
        {
            CopyAll(image, hashing, part.Size, progress, isCancelled);
            if (hashing.Length64 != part.Size)
            {
                throw new MtdJobException("The image file ends after " + hashing.Length64 + " of " + part.Size + " bytes.");
            }
            sha256 = hashing.Sha256;
            md5 = hashing.Md5;
        }
        if (part.Sha256 != null && part.Sha256 != sha256 || part.Md5 != null && part.Md5 != md5)
        {
            throw new MtdJobException("The image file is damaged: its checksum is not the one stored with the backup (" + (part.Sha256 != null ? "sha256 " + part.Sha256 + ", now " + sha256 : "md5 " + part.Md5 + ", now " + md5) + ").");
        }
        log?.Invoke(partition.Label + ": the image (" + MtdFlash.FormatBytes(part.Size) + ") " + (part.Sha256 != null || part.Md5 != null ? "matches the checksum of the backup" : "has no stored checksum; sha256 " + sha256));
        ThrowIfCancelled(isCancelled);

        // 2. Memory of the server.
        phase?.Invoke(MtdRestoreStep.CheckingMemory, "Checking the memory of the server...");
        MtdHostInfo info = refreshInfo();
        string algorithm = info.HashAlgorithm ?? throw new MtdJobException("The server has neither sha256sum nor md5sum: the image cannot be checked there.");
        string expected = algorithm == "sha256" ? sha256 : md5;
        string writeCommand0 = MtdFlash.BuildWriteCommand(info, partition, "/x", useSudo, out _, out string writeReason);
        if (writeCommand0 == null)
        {
            throw new MtdJobException("Cannot write " + partition.Label + ": " + writeReason + ".");
        }
        MtdRamDirectory directory = MtdFlash.ChooseRamDirectory(info, part.Size, out string ramReason);
        if (directory == null)
        {
            throw new MtdJobException("The image cannot be put in RAM: " + ramReason + ". Nothing was written.");
        }
        string ramPath = MtdFlash.RamImagePath(directory, unique);
        log?.Invoke("Memory available: " + MtdFlash.FormatBytes(info.UsableMemoryBytes ?? 0) + "; the image goes to " + ramPath + " (" + directory.Type + ").");
        try
        {
            // 3. Into RAM.
            phase?.Invoke(MtdRestoreStep.Uploading, "Copying the image into the RAM of the server...");
            RemoteCommandResult upload;
            using (Stream image = openImage())
            {
                upload = shell.RunWithInput(MtdFlash.BuildUploadCommand(ramPath, useSudo), new SliceStream(image, part.Size), progress, isCancelled);
            }
            ThrowIfCancelled(isCancelled);
            if (!upload.Succeeded)
            {
                throw new MtdJobException("The image could not be copied to " + ramPath + ": " + upload.Describe() + ". Nothing was written.");
            }

            // 4. Checked in RAM.
            phase?.Invoke(MtdRestoreStep.CheckingUpload, "Checking the image in RAM (" + MtdFlash.HashProgram(algorithm) + ")...");
            RemoteCommandResult ramHash = shell.Run(MtdFlash.BuildFileHashCommand(ramPath, algorithm, useSudo));
            string inRam = ramHash.Succeeded ? MtdFlash.ParseHash(ramHash.Output, algorithm) : null;
            if (inRam != expected)
            {
                throw new MtdJobException("The image in RAM is not the one sent (" + algorithm + " " + (inRam ?? ramHash.Describe()) + " instead of " + expected + "). Nothing was written.");
            }
            log?.Invoke(partition.Label + ": the image in RAM matches (" + algorithm + " " + expected + ").");
            ThrowIfCancelled(isCancelled);

            // 5. Into the flash: from here on the write is not stopped.
            string writeCommand = MtdFlash.BuildWriteCommand(info, partition, ramPath, useSudo, out string tool, out _);
            phase?.Invoke(MtdRestoreStep.Writing, "Writing " + partition.Label + " with " + tool + " (do not switch the device off)...");
            RemoteCommandResult write = shell.Run(writeCommand);
            foreach (string line in write.Output.Concat((write.Error ?? "").Split('\n')).Select(l => l.Trim()).Where(l => l.Length > 0).Take(20))
            {
                log?.Invoke(tool + ": " + line);
            }
            if (!write.Succeeded)
            {
                throw new MtdJobException("Writing " + partition.Label + " with " + tool + " failed: " + write.Describe() + ". The partition may be partly written: do not restart the device before it is written successfully.");
            }

            // 6. Read back from the flash.
            phase?.Invoke(MtdRestoreStep.CheckingFlash, "Reading " + partition.Label + " back from the flash...");
            RemoteCommandResult flashHash = shell.Run(MtdFlash.BuildDeviceHashCommand(partition.Device, part.Size, algorithm, info.HeadHasBytes, useSudo));
            string inFlash = flashHash.Succeeded ? MtdFlash.ParseHash(flashHash.Output, algorithm) : null;
            if (inFlash != expected)
            {
                throw new MtdJobException("After the write, " + partition.Label + " does not hold the image (" + algorithm + " " + (inFlash ?? flashHash.Describe()) + " instead of " + expected + ")."
                    + (partition.IsNand ? " On NAND, a bad block skipped by the write tool shifts the data." : "") + " Do not restart the device before it is written successfully.");
            }
            log?.Invoke(partition.Label + ": written and read back, " + algorithm + " " + expected + " matches.");
        }
        finally
        {
            // An exception out of this cleanup (the connection just dropped, which is exactly how a write can fail
            // half way) would replace whatever the try block above is throwing - the one warning the user needs to
            // see, not to restart the device before the partition is written successfully. Best effort only.
            try
            {
                phase?.Invoke(MtdRestoreStep.CleaningUp, "Removing the image from RAM...");
                RemoteCommandResult remove = shell.Run(MtdFlash.BuildRemoveCommand(ramPath, useSudo));
                if (!remove.Succeeded)
                {
                    log?.Invoke("Could not remove " + ramPath + ": " + remove.Describe());
                }
            }
            catch (Exception exception)
            {
                log?.Invoke("Could not remove " + ramPath + ": " + exception.Message);
            }
        }
    }

    private static void ThrowIfCancelled(Func<bool> isCancelled)
    {
        if (isCancelled?.Invoke() == true)
        {
            throw new OperationCanceledException();
        }
    }

    private static void CopyAll(Stream source, Stream destination, long length, Action<long> progress, Func<bool> isCancelled)
    {
        byte[] buffer = new byte[1 << 16];
        long done = 0;
        while (done < length)
        {
            ThrowIfCancelled(isCancelled);
            int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, length - done));
            if (read <= 0)
            {
                break;
            }
            destination.Write(buffer, 0, read);
            done += read;
            progress?.Invoke(done);
        }
    }
}
