using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AmberTree.Models;

namespace AmberTree.Services;

public class ScanEngine
{
    private readonly Stopwatch _stopwatch = new();
    private long _filesScanned;
    private long _foldersScanned;
    private long _bytesScanned;
    private DateTime _lastProgressReport = DateTime.MinValue;

    public event Action<ScanProgress>? ProgressChanged;

    public async Task<ScanResult> ScanAsync(string targetPath, CancellationToken cancellationToken, FsItem? existingRoot = null)
    {
        _stopwatch.Restart();
        _filesScanned = 0;
        _foldersScanned = 0;
        _bytesScanned = 0;
        _lastProgressReport = DateTime.MinValue;

        var extStats = new ConcurrentDictionary<string, (long count, long size, long alloc)>(StringComparer.OrdinalIgnoreCase);
        var topFiles = new ConcurrentBag<TopFileItem>();
        var ageBuckets = new long[5]; // <1mo, 1-3mo, 3-6mo, 6-12mo, >1yr (size)
        var ageCounts = new long[5];

        uint clusterSize = NativeFileSystem.GetClusterSize(targetPath);
        var now = DateTime.Now;

        var dirInfo = new DirectoryInfo(targetPath);
        FsItem root = existingRoot ?? new FsItem
        {
            Name = string.IsNullOrEmpty(dirInfo.Name) ? targetPath : dirInfo.Name,
            FullPath = targetPath,
            IsDirectory = true,
            LastModified = dirInfo.Exists ? dirInfo.LastWriteTime : DateTime.Now,
            Attributes = dirInfo.Exists ? dirInfo.Attributes : FileAttributes.Directory,
            PercentOfParent = 100.0,
            PercentOfTotal = 100.0,
            IsExpanded = true
        };

        await Task.Run(() =>
        {
            ScanNode(root, clusterSize, extStats, topFiles, ageBuckets, ageCounts, now, cancellationToken);
        }, cancellationToken);

        _stopwatch.Stop();

        CalculatePercentagesAndSort(root, root.Size);

        // Build Extension statistics
        long totalSize = root.Size;
        var extList = extStats.Select(kv => new ExtensionStat
        {
            Extension = kv.Key,
            Description = GetExtensionDescription(kv.Key),
            FileCount = kv.Value.count,
            TotalSize = kv.Value.size,
            AllocatedSize = kv.Value.alloc,
            PercentOfTotal = totalSize > 0 ? (double)kv.Value.size / totalSize * 100.0 : 0.0
        })
        .OrderByDescending(e => e.TotalSize)
        .ToList();

        // Build Top 100 Largest Files
        var topList = topFiles
            .OrderByDescending(f => f.Size)
            .Take(100)
            .Select((f, idx) =>
            {
                f.Rank = idx + 1;
                f.PercentOfTotal = totalSize > 0 ? (double)f.Size / totalSize * 100.0 : 0.0;
                return f;
            })
            .ToList();

        // Build Age distribution
        var ageList = new List<AgeDistributionItem>
        {
            new() { RangeName = TreeMaintenance.AgeRangeNames[0], FileCount = ageCounts[0], TotalSize = ageBuckets[0], PercentOfTotal = totalSize > 0 ? (double)ageBuckets[0] / totalSize * 100.0 : 0 },
            new() { RangeName = TreeMaintenance.AgeRangeNames[1], FileCount = ageCounts[1], TotalSize = ageBuckets[1], PercentOfTotal = totalSize > 0 ? (double)ageBuckets[1] / totalSize * 100.0 : 0 },
            new() { RangeName = TreeMaintenance.AgeRangeNames[2], FileCount = ageCounts[2], TotalSize = ageBuckets[2], PercentOfTotal = totalSize > 0 ? (double)ageBuckets[2] / totalSize * 100.0 : 0 },
            new() { RangeName = TreeMaintenance.AgeRangeNames[3], FileCount = ageCounts[3], TotalSize = ageBuckets[3], PercentOfTotal = totalSize > 0 ? (double)ageBuckets[3] / totalSize * 100.0 : 0 },
            new() { RangeName = TreeMaintenance.AgeRangeNames[4], FileCount = ageCounts[4], TotalSize = ageBuckets[4], PercentOfTotal = totalSize > 0 ? (double)ageBuckets[4] / totalSize * 100.0 : 0 },
        };

        return new ScanResult
        {
            Root = root,
            Extensions = extList,
            TopFiles = topList,
            AgeDistribution = ageList,
            Elapsed = _stopwatch.Elapsed,
            TotalFiles = _filesScanned,
            TotalFolders = _foldersScanned,
            TotalSize = root.Size,
            TotalAllocated = root.AllocatedSize,
            WasCancelled = cancellationToken.IsCancellationRequested
        };
    }

    private void ScanNode(
        FsItem folder,
        uint clusterSize,
        ConcurrentDictionary<string, (long count, long size, long alloc)> extStats,
        ConcurrentBag<TopFileItem> topFiles,
        long[] ageBuckets,
        long[] ageCounts,
        DateTime now,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;

        Interlocked.Increment(ref _foldersScanned);
        ReportProgressThrottled(folder.FullPath);

        // Ensure direct children of this folder are enumerated
        folder.EnsureDirectChildrenLoaded();

        List<FsItem> subFolders;
        lock (folder.SyncLock)
        {
            subFolders = folder.Children.Where(c => c.IsDirectory).ToList();

            foreach (var childFile in folder.Children.Where(c => !c.IsDirectory))
            {
                folder.Size += childFile.Size;
                folder.AllocatedSize += childFile.AllocatedSize;
                folder.FileCount += 1;

                Interlocked.Increment(ref _filesScanned);
                Interlocked.Add(ref _bytesScanned, childFile.Size);

                // Track Extension
                string ext = Path.GetExtension(childFile.Name).ToLowerInvariant();
                if (string.IsNullOrEmpty(ext)) ext = TreeMaintenance.NoExtensionLabel;
                extStats.AddOrUpdate(ext, (1, childFile.Size, childFile.AllocatedSize), (k, old) => (old.count + 1, old.size + childFile.Size, old.alloc + childFile.AllocatedSize));

                // Track Top Files (keep candidate items if reasonably large)
                if (childFile.Size > 1024 * 1024) // > 1 MB
                {
                    topFiles.Add(new TopFileItem
                    {
                        Name = childFile.Name,
                        DirectoryPath = folder.FullPath,
                        FullPath = childFile.FullPath,
                        Extension = ext,
                        Size = childFile.Size,
                        AllocatedSize = childFile.AllocatedSize,
                        LastModified = childFile.LastModified
                    });
                }

                // Track Age
                var ageDays = (now - childFile.LastModified).TotalDays;
                int bucketIndex = ageDays switch
                {
                    < 30 => 0,
                    < 90 => 1,
                    < 180 => 2,
                    < 365 => 3,
                    _ => 4
                };
                Interlocked.Add(ref ageBuckets[bucketIndex], childFile.Size);
                Interlocked.Increment(ref ageCounts[bucketIndex]);

                if ((_filesScanned & 63) == 0)
                {
                    ReportProgressThrottled(childFile.FullPath);
                }
            }
        }

        // Recurse into subfolders
        foreach (var sub in subFolders)
        {
            if (ct.IsCancellationRequested) break;

            bool isReparse = (sub.Attributes & FileAttributes.ReparsePoint) != 0;
            if (!isReparse)
            {
                ScanNode(sub, clusterSize, extStats, topFiles, ageBuckets, ageCounts, now, ct);
            }

            folder.Size += sub.Size;
            folder.AllocatedSize += sub.AllocatedSize;
            folder.FileCount += sub.FileCount;
            folder.FolderCount += 1 + sub.FolderCount;

            sub.NotifySizesChanged();
        }

        folder.NotifySizesChanged();
    }

    private void CalculatePercentagesAndSort(FsItem item, long rootTotalSize)
    {
        item.PercentOfTotal = rootTotalSize > 0 ? (double)item.Size / rootTotalSize * 100.0 : 0.0;
        if (item.Parent != null && item.Parent.Size > 0)
        {
            item.PercentOfParent = (double)item.Size / item.Parent.Size * 100.0;
        }
        else
        {
            item.PercentOfParent = 100.0;
        }

        if (item.Children.Count > 0)
        {
            lock (item.SyncLock)
            {
                var sorted = item.Children.OrderByDescending(c => c.Size).ToList();
                for (int i = 0; i < sorted.Count; i++)
                {
                    int oldIndex = item.Children.IndexOf(sorted[i]);
                    if (oldIndex != i && oldIndex >= 0)
                    {
                        item.Children.Move(oldIndex, i);
                    }
                }
            }

            foreach (var child in item.Children)
            {
                CalculatePercentagesAndSort(child, rootTotalSize);
            }
        }
    }

    private void ReportProgressThrottled(string currentPath)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastProgressReport).TotalMilliseconds >= 80)
        {
            _lastProgressReport = now;
            ProgressChanged?.Invoke(new ScanProgress
            {
                CurrentPath = currentPath,
                FilesScanned = _filesScanned,
                FoldersScanned = _foldersScanned,
                BytesScanned = _bytesScanned,
                Elapsed = _stopwatch.Elapsed
            });
        }
    }

    public static string GetExtensionDescription(string ext) => ext.ToLowerInvariant() switch
    {
        ".exe" => "Çalıştırılabilir uygulama",
        ".dll" => "Dinamik bağlantı kitaplığı (DLL)",
        ".sys" => "Sistem sürücüsü",
        ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "Sıkıştırılmış arşiv",
        ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" => "Video dosyası",
        ".mp3" or ".wav" or ".flac" or ".aac" or ".ogg" => "Ses dosyası",
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".svg" => "Resim dosyası",
        ".pdf" => "PDF belgesi",
        ".docx" or ".doc" => "Word belgesi",
        ".xlsx" or ".xls" => "Excel çalışma sayfası",
        ".pptx" or ".ppt" => "PowerPoint sunusu",
        ".txt" or ".log" or ".md" => "Metin / günlük dosyası",
        ".json" or ".xml" or ".yaml" or ".yml" => "Yapılandırılmış veri",
        ".iso" or ".img" or ".vhd" or ".vhdx" => "Disk görüntüsü",
        ".cs" or ".cpp" or ".c" or ".h" or ".py" or ".js" or ".ts" => "Kaynak kod",
        _ => "Dosya"
    };
}
