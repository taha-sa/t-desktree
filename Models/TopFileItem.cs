using System;
using System.Text.Json.Serialization;
using AmberTree.Converters;

namespace AmberTree.Models;

public class TopFileItem : ObservableBase
{
    public int Rank { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DirectoryPath { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public long Size { get; set; }
    public long AllocatedSize { get; set; }
    public DateTime LastModified { get; set; }
    public double PercentOfTotal { get; set; }

    public string PercentDisplay => $"{PercentOfTotal:F2}%";

    [JsonIgnore]
    public string SizeDisplay => SizeFormatter.FormatBytes(Size, SizeFormatter.CurrentMode);

    [JsonIgnore]
    public string AllocatedSizeDisplay => SizeFormatter.FormatBytes(AllocatedSize, SizeFormatter.CurrentMode);

    public void NotifyUnitChanged()
    {
        Raise(nameof(SizeDisplay));
        Raise(nameof(AllocatedSizeDisplay));
    }
}
