namespace AmberTree.Models;

public class ExtensionStat
{
    public string Extension { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public long FileCount { get; set; }
    public long TotalSize { get; set; }
    public long AllocatedSize { get; set; }
    public double PercentOfTotal { get; set; }

    public string PercentDisplay => $"{PercentOfTotal:F1}%";
}
