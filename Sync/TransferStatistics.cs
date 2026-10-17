using SmarTTY.Utilities;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace SmarTTY.Sync;

public struct TransferStatistics
{
    public long TotalBytes;

    public long TransferredBytes;

    public long UpToDateBytes;

    public int TotalFiles;

    public int CopiedFiles;

    public int UpToDateFiles;

    public int Directories;

    public int FilesToDelete;

    public int DeletedFiles;

    public int Symlinks;

    [SpecialName]
    public static TransferStatistics Add(TransferStatistics left, TransferStatistics right)
    {
        return new TransferStatistics
        {
            TotalBytes = left.TotalBytes + right.TotalBytes,
            TransferredBytes = left.TransferredBytes + right.TransferredBytes,
            UpToDateBytes = left.UpToDateBytes + right.UpToDateBytes,
            TotalFiles = left.TotalFiles + right.TotalFiles,
            CopiedFiles = left.CopiedFiles + right.CopiedFiles,
            UpToDateFiles = left.UpToDateFiles + right.UpToDateFiles,
            Directories = left.Directories + right.Directories,
            FilesToDelete = left.FilesToDelete + right.FilesToDelete,
            DeletedFiles = left.DeletedFiles + right.DeletedFiles,
            Symlinks = left.Symlinks + right.Symlinks
        };
    }

    public string Describe()
    {
        List<string> list = new List<string>();
        if (CopiedFiles > 0)
        {
            list.Add($"{CopiedFiles} files copied ({StringUtils.FormatSize(TotalBytes, appendUnit: true)})");
        }
        if (CopiedFiles != TotalFiles)
        {
            list.Add($"{TotalFiles - CopiedFiles} files failed");
        }
        if (UpToDateFiles != 0)
        {
            list.Add($"{UpToDateFiles} files are up-to-date");
        }
        if (FilesToDelete != 0)
        {
            list.Add($"{FilesToDelete} files deleted");
        }
        string text = string.Join(", ", list.ToArray());
        if (text.Trim() == "")
        {
            text = "Could not find any files";
        }
        return text + ".";
    }

    public override string ToString()
    {
        return Describe();
    }
}
