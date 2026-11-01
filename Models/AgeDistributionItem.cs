namespace AmberTree.Models;

public class AgeDistributionItem
{
    public string RangeName { get; set; } = string.Empty;
    public long FileCount { get; set; }
    public long TotalSize { get; set; }
    public double PercentOfTotal { get; set; }

    public string PercentDisplay => $"{PercentOfTotal:F1}%";
}
