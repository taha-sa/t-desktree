using System.Text.Json.Serialization;
using AmberTree.Converters;

namespace AmberTree.Models;

public class ExtensionStat : ObservableBase
{
    public string Extension { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public long FileCount { get; set; }
    public long TotalSize { get; set; }
    public long AllocatedSize { get; set; }
    public double PercentOfTotal { get; set; }

    public string PercentDisplay => $"{PercentOfTotal:F1}%";

    [JsonIgnore]
    public string TotalSizeDisplay => SizeFormatter.FormatBytes(TotalSize, SizeFormatter.CurrentMode);

    [JsonIgnore]
    public string AllocatedSizeDisplay => SizeFormatter.FormatBytes(AllocatedSize, SizeFormatter.CurrentMode);

    public void NotifyUnitChanged()
    {
        Raise(nameof(TotalSizeDisplay));
        Raise(nameof(AllocatedSizeDisplay));
    }
}
