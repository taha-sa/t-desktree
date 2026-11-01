using System.Text.Json.Serialization;
using AmberTree.Converters;

namespace AmberTree.Models;

public class AgeDistributionItem : ObservableBase
{
    public string RangeName { get; set; } = string.Empty;
    public long FileCount { get; set; }
    public long TotalSize { get; set; }
    public double PercentOfTotal { get; set; }

    public string PercentDisplay => $"{PercentOfTotal:F1}%";

    /// <summary>Birim moduna göre biçimlenmiş boyut. UI bunu bağlamalı.</summary>
    [JsonIgnore]
    public string TotalSizeDisplay => SizeFormatter.FormatBytes(TotalSize, SizeFormatter.CurrentMode);

    public void NotifyUnitChanged() => Raise(nameof(TotalSizeDisplay));
}
