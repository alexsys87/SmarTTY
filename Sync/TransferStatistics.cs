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
    public static TransferStatistics Add(TransferStatistics transferStatistics, TransferStatistics transferStatistics2)
    {
        return new TransferStatistics
        {
            TotalBytes = transferStatistics.TotalBytes + transferStatistics2.TotalBytes,
            TransferredBytes = transferStatistics.TransferredBytes + transferStatistics2.TransferredBytes,
            UpToDateBytes = transferStatistics.UpToDateBytes + transferStatistics2.UpToDateBytes,
            TotalFiles = transferStatistics.TotalFiles + transferStatistics2.TotalFiles,
            CopiedFiles = transferStatistics.CopiedFiles + transferStatistics2.CopiedFiles,
            UpToDateFiles = transferStatistics.UpToDateFiles + transferStatistics2.UpToDateFiles,
            Directories = transferStatistics.Directories + transferStatistics2.Directories,
            FilesToDelete = transferStatistics.FilesToDelete + transferStatistics2.FilesToDelete,
            DeletedFiles = transferStatistics.DeletedFiles + transferStatistics2.DeletedFiles,
            Symlinks = transferStatistics.Symlinks + transferStatistics2.Symlinks
        };
    }

    public string Describe()
    {
        List<string> list = new List<string>();
        if (CopiedFiles > 0)
        {
            list.Add($"{CopiedFiles} files copied ({StringUtils.FormatSize(TotalBytes, flag: true)})");
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
