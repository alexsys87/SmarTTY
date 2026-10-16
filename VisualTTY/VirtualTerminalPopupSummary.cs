
namespace VisualTTY;

public class VirtualTerminalPopupSummary
{
    public class PathInfo : VirtualTerminalPopupSummary
    {
        public string FullPath { get; set; }
    }

    public class OptionInfo : VirtualTerminalPopupSummary
    {
        public string Option { get; set; }

        public CommandCompletion.OptionDescription Description { get; set; }
    }

    public class FileOrDirectoryInfo : VirtualTerminalPopupSummary
    {
        public bool IsDirectory { get; set; }

        public string FullPath { get; set; }
    }
}
