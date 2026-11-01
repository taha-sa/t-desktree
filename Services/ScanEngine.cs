using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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

    public async Task<ScanResult> ScanAsync(string targetPath, CancellationToken cancellationToken)
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

        FsItem? root = null;

        await Task.Run(() =>
        {
            var dirInfo = new DirectoryInfo(targetPath);
            root = new FsItem
            {
                Name = string.IsNullOrEmpty(dirInfo.Name) ? targetPath : dirInfo.Name,
                FullPath = targetPath,
                IsDirectory = true,
                LastModified = dirInfo.Exists ? dirInfo.LastWriteTime : DateTime.Now,
                Attributes = dirInfo.Exists ? dirInfo.Attributes : FileAttributes.Directory
            };

            ScanNode(root, clusterSize, extStats, topFiles, ageBuckets, ageCounts, now, cancellationToken);
        }, cancellationToken);

        _stopwatch.Stop();

        if (root != null)
        {
            CalculatePercentagesAndSort(root, root.Size);
        }

        // Build Extension statistics
        long totalSize = root?.Size ?? 0;
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
            new() { RangeName = "< 1 Month", FileCount = ageCounts[0], TotalSize = ageBuckets[0], PercentOfTotal = totalSize > 0 ? (double)ageBuckets[0] / totalSize * 100.0 : 0 },
            new() { RangeName = "1 - 3 Months", FileCount = ageCounts[1], TotalSize = ageBuckets[1], PercentOfTotal = totalSize > 0 ? (double)ageBuckets[1] / totalSize * 100.0 : 0 },
            new() { RangeName = "3 - 6 Months", FileCount = ageCounts[2], TotalSize = ageBuckets[2], PercentOfTotal = totalSize > 0 ? (double)ageBuckets[2] / totalSize * 100.0 : 0 },
            new() { RangeName = "6 - 12 Months", FileCount = ageCounts[3], TotalSize = ageBuckets[3], PercentOfTotal = totalSize > 0 ? (double)ageBuckets[3] / totalSize * 100.0 : 0 },
            new() { RangeName = "> 1 Year", FileCount = ageCounts[4], TotalSize = ageBuckets[4], PercentOfTotal = totalSize > 0 ? (double)ageBuckets[4] / totalSize * 100.0 : 0 },
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
            TotalSize = root?.Size ?? 0,
            TotalAllocated = root?.AllocatedSize ?? 0,
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

        string searchPattern = Path.Combine(folder.FullPath, "*");
        IntPtr hFind = NativeFileSystem.FindFirstFileEx(
            searchPattern,
            1, // FindExInfoBasic
            out var findData,
            0, // FindExSearchNameMatch
            IntPtr.Zero,
            2); // FIND_FIRST_EX_LARGE_FETCH

        if (hFind == IntPtr.Zero || hFind.ToInt64() == -1)
        {
            // Access denied or empty directory
            return;
        }

        var subFolders = new List<FsItem>();

        try
        {
            do
            {
                if (ct.IsCancellationRequested) break;

                string name = findData.cFileName;
                if (name == "." || name == "..") continue;

                var attrs = (FileAttributes)findData.dwFileAttributes;
                bool isDir = (attrs & FileAttributes.Directory) != 0;
                string childPath = Path.Combine(folder.FullPath, name);
                DateTime modified = NativeFileSystem.FileTimeToDateTime(findData.ftLastWriteTime);

                if (isDir)
                {
                    // Avoid recursive loops through junction points / symlinks
                    bool isReparse = (attrs & FileAttributes.ReparsePoint) != 0;

                    var childFolder = new FsItem
                    {
                        Name = name,
                        FullPath = childPath,
                        IsDirectory = true,
                        Attributes = attrs,
                        LastModified = modified,
                        Parent = folder
                    };

                    if (!isReparse)
                    {
                        ScanNode(childFolder, clusterSize, extStats, topFiles, ageBuckets, ageCounts, now, ct);
                    }

                    folder.Size += childFolder.Size;
                    folder.AllocatedSize += childFolder.AllocatedSize;
                    folder.FileCount += childFolder.FileCount;
                    folder.FolderCount += 1 + childFolder.FolderCount;

                    subFolders.Add(childFolder);
                }
                else
                {
                    long fileSize = ((long)findData.nFileSizeHigh << 32) | (uint)findData.nFileSizeLow;
                    long allocated = NativeFileSystem.AlignToCluster(fileSize, clusterSize);

                    var childFile = new FsItem
                    {
                        Name = name,
                        FullPath = childPath,
                        IsDirectory = false,
                        Size = fileSize,
                        AllocatedSize = allocated,
                        Attributes = attrs,
                        LastModified = modified,
                        Parent = folder,
                        FileCount = 1,
                        FolderCount = 0
                    };

                    folder.Size += fileSize;
                    folder.AllocatedSize += allocated;
                    folder.FileCount += 1;

                    Interlocked.Increment(ref _filesScanned);
                    Interlocked.Add(ref _bytesScanned, fileSize);

                    // Track Extension
                    string ext = Path.GetExtension(name).ToLowerInvariant();
                    if (string.IsNullOrEmpty(ext)) ext = "[No Extension]";
                    extStats.AddOrUpdate(ext, (1, fileSize, allocated), (k, old) => (old.count + 1, old.size + fileSize, old.alloc + allocated));

                    // Track Top Files (keep candidate items if reasonably large)
                    if (fileSize > 1024 * 1024) // > 1 MB
                    {
                        topFiles.Add(new TopFileItem
                        {
                            Name = name,
                            DirectoryPath = folder.FullPath,
                            FullPath = childPath,
                            Extension = ext,
                            Size = fileSize,
                            AllocatedSize = allocated,
                            LastModified = modified
                        });
                    }

                    // Track Age
                    var ageDays = (now - modified).TotalDays;
                    int bucketIndex = ageDays switch
                    {
                        < 30 => 0,
                        < 90 => 1,
                        < 180 => 2,
                        < 365 => 3,
                        _ => 4
                    };
                    Interlocked.Add(ref ageBuckets[bucketIndex], fileSize);
                    Interlocked.Increment(ref ageCounts[bucketIndex]);

                    folder.Children.Add(childFile);
                }
            } while (NativeFileSystem.FindNextFile(hFind, out findData));
        }
        finally
        {
            NativeFileSystem.FindClose(hFind);
        }

        // Add subfolders to folder children
        foreach (var sub in subFolders)
        {
            folder.Children.Add(sub);
        }
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
            // Sort children descending by Size
            var sorted = item.Children.OrderByDescending(c => c.Size).ToList();
            item.Children.Clear();
            foreach (var child in sorted)
            {
                CalculatePercentagesAndSort(child, rootTotalSize);
                item.Children.Add(child);
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

    private static string GetExtensionDescription(string ext) => ext.ToLowerInvariant() switch
    {
        ".exe" => "Executable Application",
        ".dll" => "Dynamic Link Library",
        ".sys" => "System Driver",
        ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "Compressed Archive",
        ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" => "Video File",
        ".mp3" or ".wav" or ".flac" or ".aac" or ".ogg" => "Audio File",
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".svg" => "Image File",
        ".pdf" => "PDF Document",
        ".docx" or ".doc" => "Word Document",
        ".xlsx" or ".xls" => "Excel Spreadsheet",
        ".pptx" or ".ppt" => "PowerPoint Presentation",
        ".txt" or ".log" or ".md" => "Text / Log File",
        ".json" or ".xml" or ".yaml" or ".yml" => "Structured Data",
        ".iso" or ".img" or ".vhd" or ".vhdx" => "Disk Image",
        ".cs" or ".cpp" or ".c" or ".h" or ".py" or ".js" or ".ts" => "Source Code",
        _ => "File"
    };
}
