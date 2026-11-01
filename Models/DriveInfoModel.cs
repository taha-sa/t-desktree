using System.IO;

namespace AmberTree.Models;

public class DriveInfoModel
{
    public string Name { get; set; } = string.Empty;
    public string RootDirectory { get; set; } = string.Empty;
    public string VolumeLabel { get; set; } = string.Empty;
    public string DriveFormat { get; set; } = string.Empty;
    public DriveType DriveType { get; set; }
    public long TotalSize { get; set; }
    public long AvailableFreeSpace { get; set; }
    public long UsedSpace => TotalSize > AvailableFreeSpace ? TotalSize - AvailableFreeSpace : 0;
    public double UsedPercentage => TotalSize > 0 ? (double)UsedSpace / TotalSize * 100.0 : 0.0;

    public string DisplayTitle => string.IsNullOrWhiteSpace(VolumeLabel)
        ? $"{Name}"
        : $"{VolumeLabel} ({Name})";
}
