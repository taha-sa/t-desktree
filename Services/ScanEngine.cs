using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    private long _lastProgressMs;

    public event Action<ScanProgress>? ProgressChanged;

    public async Task<ScanResult> ScanAsync(
        string targetPath,
        CancellationToken cancellationToken,
        FsItem? existingRoot = null,
        TreeSortColumn sortColumn = TreeSortColumn.Size,
        bool sortAscending = false)
    {
        _stopwatch.Restart();
        _filesScanned = 0;
        _foldersScanned = 0;
        _bytesScanned = 0;
        _lastProgressMs = 0;

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

        int maxWorkers = Math.Clamp(Environment.ProcessorCount, 2, 8);
        List<FsItem> rootChildren = new();

        try
        {
            await Task.Run(() =>
            {
                rootChildren = ScanFolder(root, clusterSize, depth: 0, maxWorkers, cancellationToken);
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // İptal edilse bile mevcut taranan kısmı sıralayıp yüzdeleri hesapla
        }

        FinalizeTree(root, rootChildren, root.Size, sortColumn, sortAscending);
        _stopwatch.Stop();

        return new ScanResult
        {
            Root = root,
            Extensions = new List<ExtensionStat>(),
            TopFiles = new List<TopFileItem>(),
            AgeDistribution = new List<AgeDistributionItem>(),
            Elapsed = _stopwatch.Elapsed,
            TotalFiles = _filesScanned,
            TotalFolders = _foldersScanned,
            TotalSize = root.Size,
            TotalAllocated = root.AllocatedSize,
            WasCancelled = cancellationToken.IsCancellationRequested
        };
    }

    private List<FsItem> ScanFolder(FsItem folder, uint clusterSize, int depth, int maxWorkers, CancellationToken ct)
    {
        var items = new List<FsItem>();
        if (ct.IsCancellationRequested) return items;

        Interlocked.Increment(ref _foldersScanned);
        ReportProgressThrottled(folder.FullPath);

        List<FsItem>? subFolders = null;
        long directBytes = 0;
        long directAllocated = 0;
        long directFiles = 0;

        try
        {
            string searchPattern = NativeFileSystem.ToSearchPattern(folder.FullPath);
            IntPtr hFind = NativeFileSystem.FindFirstFileEx(
                searchPattern,
                1, // FindExInfoBasic
                out var findData,
                0,
                IntPtr.Zero,
                2); // FIND_FIRST_EX_LARGE_FETCH

            if (hFind != IntPtr.Zero && hFind.ToInt64() != -1)
            {
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
                            var dirNode = new FsItem
                            {
                                Name = name,
                                FullPath = childPath,
                                IsDirectory = true,
                                Attributes = attrs,
                                LastModified = modified,
                                Parent = folder
                            };
                            items.Add(dirNode);
                            if ((attrs & FileAttributes.ReparsePoint) == 0)
                            {
                                subFolders ??= new List<FsItem>();
                                subFolders.Add(dirNode);
                            }
                        }
                        else
                        {
                            long fileSize = ((long)findData.nFileSizeHigh << 32) | (uint)findData.nFileSizeLow;
                            long allocated = NativeFileSystem.AlignToCluster(fileSize, clusterSize);
                            items.Add(new FsItem
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
                            });
                            directBytes += fileSize;
                            directAllocated += allocated;
                            directFiles++;
                        }
                    } while (NativeFileSystem.FindNextFile(hFind, out findData));
                }
                finally
                {
                    NativeFileSystem.FindClose(hFind);
                }
            }
        }
        catch
        {
            // Erişim reddedildi veya okunamayan dizin
        }

        if (directFiles > 0)
        {
            Interlocked.Add(ref _filesScanned, directFiles);
            Interlocked.Add(ref _bytesScanned, directBytes);
        }

        long totalSize = directBytes;
        long totalAllocated = directAllocated;
        long totalFiles = directFiles;
        long totalFolders = 0;

        if (subFolders != null && subFolders.Count > 0 && !ct.IsCancellationRequested)
        {
            // İlk 2 derinlikte SSD/NVMe I/O paralelliği: alt ağaçları eşzamanlı tara
            if (depth < 2 && subFolders.Count > 1)
            {
                var childLists = new List<FsItem>[subFolders.Count];
                var opts = new ParallelOptions { MaxDegreeOfParallelism = maxWorkers };
                Parallel.For(0, subFolders.Count, opts, i =>
                {
                    if (ct.IsCancellationRequested) return;
                    var sub = subFolders[i];
                    childLists[i] = ScanFolder(sub, clusterSize, depth + 1, maxWorkers, ct);
                });

                for (int i = 0; i < subFolders.Count; i++)
                {
                    var sub = subFolders[i];
                    if (childLists[i] != null && childLists[i].Count > 0)
                    {
                        sub.SetChildrenBulk(childLists[i]);
                    }
                    totalSize += sub.Size;
                    totalAllocated += sub.AllocatedSize;
                    totalFiles += sub.FileCount;
                    totalFolders += 1 + sub.FolderCount;
                }
            }
            else
            {
                for (int i = 0; i < subFolders.Count; i++)
                {
                    if (ct.IsCancellationRequested) break;
                    var sub = subFolders[i];
                    var grandChildren = ScanFolder(sub, clusterSize, depth + 1, maxWorkers, ct);
                    if (grandChildren.Count > 0)
                    {
                        sub.SetChildrenBulk(grandChildren);
                    }
                    totalSize += sub.Size;
                    totalAllocated += sub.AllocatedSize;
                    totalFiles += sub.FileCount;
                    totalFolders += 1 + sub.FolderCount;
                }
            }
        }

        // Bağlama noktaları (reparse points) klasör sayısına dahil edilir (döngü olmadan)
        int nonReparseCount = subFolders?.Count ?? 0;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].IsDirectory && (items[i].Attributes & FileAttributes.ReparsePoint) != 0)
            {
                totalFolders++;
            }
        }

        folder.Size = totalSize;
        folder.AllocatedSize = totalAllocated;
        folder.FileCount = totalFiles;
        folder.FolderCount = totalFolders;
        return items;
    }

    private static void FinalizeTree(FsItem root, List<FsItem> rootChildren, long rootTotalSize, TreeSortColumn sortColumn, bool sortAscending)
    {
        root.PercentOfParent = 100.0;
        root.PercentOfTotal = rootTotalSize > 0 ? 100.0 : 0.0;

        // Kök alt öğelerinin yüzdelerini hesapla ve sırala, sonra tek Reset ile köke bağla
        var stack = new Stack<FsItem>(1024);
        for (int i = 0; i < rootChildren.Count; i++)
        {
            var c = rootChildren[i];
            c.PercentOfTotal = rootTotalSize > 0 ? (double)c.Size / rootTotalSize * 100.0 : 0.0;
            c.PercentOfParent = root.Size > 0 ? (double)c.Size / root.Size * 100.0 : 0.0;
            if (c.IsDirectory && c.HasChildren) stack.Push(c);
        }
        TreeMaintenance.SortList(rootChildren, root.FullPath, sortColumn, sortAscending);
        root.SetChildrenBulk(rootChildren);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            var col = dir.Children;
            int count = col.Count;
            long parentSize = dir.Size;
            var list = new List<FsItem>(count);
            for (int i = 0; i < count; i++)
            {
                var c = col[i];
                c.PercentOfTotal = rootTotalSize > 0 ? (double)c.Size / rootTotalSize * 100.0 : 0.0;
                c.PercentOfParent = parentSize > 0 ? (double)c.Size / parentSize * 100.0 : 0.0;
                list.Add(c);
                if (c.IsDirectory && c.HasChildren) stack.Push(c);
            }
            TreeMaintenance.SortList(list, dir.FullPath, sortColumn, sortAscending);
            col.ReplaceAll(list);
        }
    }

    private void ReportProgressThrottled(string currentPath)
    {
        long now = Environment.TickCount64;
        long prev = Volatile.Read(ref _lastProgressMs);
        if (now - prev >= 80 && Interlocked.CompareExchange(ref _lastProgressMs, now, prev) == prev)
        {
            ProgressChanged?.Invoke(new ScanProgress
            {
                CurrentPath = currentPath,
                FilesScanned = Volatile.Read(ref _filesScanned),
                FoldersScanned = Volatile.Read(ref _foldersScanned),
                BytesScanned = Volatile.Read(ref _bytesScanned),
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
