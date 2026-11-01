using System;
using System.Collections.Generic;
using AmberTree.Models;

namespace AmberTree.Services;

public class ScanProgress
{
    public string CurrentPath { get; set; } = string.Empty;
    public long FilesScanned { get; set; }
    public long FoldersScanned { get; set; }
    public long BytesScanned { get; set; }
    public TimeSpan Elapsed { get; set; }
    public double FilesPerSecond => Elapsed.TotalSeconds > 0 ? FilesScanned / Elapsed.TotalSeconds : 0;
    public IReadOnlyList<DiscoveredFileItem> RecentFiles { get; set; } = Array.Empty<DiscoveredFileItem>();
}

public class ScanResult
{
    public FsItem? Root { get; set; }
    public List<ExtensionStat> Extensions { get; set; } = new();
    public List<TopFileItem> TopFiles { get; set; } = new();
    public List<AgeDistributionItem> AgeDistribution { get; set; } = new();
    public TimeSpan Elapsed { get; set; }
    public long TotalFiles { get; set; }
    public long TotalFolders { get; set; }
    public long TotalSize { get; set; }
    public long TotalAllocated { get; set; }
    public bool WasCancelled { get; set; }
}
