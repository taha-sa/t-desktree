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

        var topFilesQueue = new PriorityQueue<TopFileItem, long>();
        var topFilesLock = new object();

        uint clusterSize = NativeFileSystem.GetClusterSize(targetPath);

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

        try
        {
            await Task.Run(() =>
            {
                ScanNode(root, clusterSize, topFilesQueue, topFilesLock, cancellationToken);
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // İptal edilse bile mevcut taranan kısmı sıralayıp yüzdeleri hesapla
        }

        _stopwatch.Stop();

        CalculatePercentagesAndSort(root, root.Size);

        long totalSize = root.Size;
        var extList = new List<ExtensionStat>();

        // Build Top 100 Largest Files
        var topList = new List<TopFileItem>(topFilesQueue.Count);
        while (topFilesQueue.Count > 0)
        {
            topList.Add(topFilesQueue.Dequeue());
        }
        topList.Reverse();
        for (int idx = 0; idx < topList.Count; idx++)
        {
            topList[idx].Rank = idx + 1;
            topList[idx].PercentOfTotal = totalSize > 0 ? (double)topList[idx].Size / totalSize * 100.0 : 0.0;
        }

        var ageList = new List<AgeDistributionItem>();

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
        PriorityQueue<TopFileItem, long> topFilesQueue,
        object topFilesLock,
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

                // Track Top 100 Largest Files (min-heap ile sabit 100 elemanli, boyut kısıtlamasız)
                lock (topFilesLock)
                {
                    if (topFilesQueue.Count < 100)
                    {
                        topFilesQueue.Enqueue(new TopFileItem
                        {
                            Name = childFile.Name,
                            DirectoryPath = folder.FullPath,
                            FullPath = childFile.FullPath,
                            Extension = Path.GetExtension(childFile.Name),
                            Size = childFile.Size,
                            AllocatedSize = childFile.AllocatedSize,
                            LastModified = childFile.LastModified
                        }, childFile.Size);
                    }
                    else if (childFile.Size > topFilesQueue.Peek().Size)
                    {
                        topFilesQueue.Dequeue();
                        topFilesQueue.Enqueue(new TopFileItem
                        {
                            Name = childFile.Name,
                            DirectoryPath = folder.FullPath,
                            FullPath = childFile.FullPath,
                            Extension = Path.GetExtension(childFile.Name),
                            Size = childFile.Size,
                            AllocatedSize = childFile.AllocatedSize,
                            LastModified = childFile.LastModified
                        }, childFile.Size);
                    }
                }

                if ((_filesScanned & 255) == 0)
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
                ScanNode(sub, clusterSize, topFilesQueue, topFilesLock, ct);
            }

            folder.Size += sub.Size;
            folder.AllocatedSize += sub.AllocatedSize;
            folder.FileCount += sub.FileCount;
            folder.FolderCount += 1 + sub.FolderCount;
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
            TreeMaintenance.SortChildren(item);

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
