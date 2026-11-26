using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime;
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

        // Önceki tarama/ön-yükleme kalıntılarını sıfırla
        root.Size = 0;
        root.AllocatedSize = 0;
        root.FileCount = 0;
        root.FolderCount = 0;

        int maxWorkers = Math.Clamp(Environment.ProcessorCount, 2, 8);

        try
        {
            await Task.Run(() =>
            {
                ScanFolder(root, targetPath, clusterSize, depth: 0, maxWorkers, sortColumn, sortAscending, cancellationToken);
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // İptal edilse bile mevcut taranan kısmı sıralayıp yüzdeleri hesapla
        }

        FinalizeTree(root, root.Size, sortColumn, sortAscending);
        _stopwatch.Stop();

        // Tarama sırasındaki geçici tamponları ve LOH parçalanmasını hemen geri ver
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);

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

    private void ScanFolder(
        FsItem folder,
        string folderPath,
        uint clusterSize,
        int depth,
        int maxWorkers,
        TreeSortColumn sortColumn,
        bool sortAscending,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;

        Interlocked.Increment(ref _foldersScanned);
        ReportProgressThrottled(folderPath);

        List<FsItem>? items = null;
        List<(FsItem node, string path)>? subFolders = null;
        long directBytes = 0;
        long directAllocated = 0;
        long directFiles = 0;
        long directFolders = 0;

        try
        {
            string searchPattern = NativeFileSystem.ToSearchPattern(folderPath);
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

                        items ??= new List<FsItem>(16);
                        var attrs = (FileAttributes)findData.dwFileAttributes;
                        bool isDir = (attrs & FileAttributes.Directory) != 0;
                        DateTime modified = NativeFileSystem.FileTimeToDateTime(findData.ftLastWriteTime);

                        if (isDir)
                        {
                            var dirNode = new FsItem
                            {
                                Name = name,
                                IsDirectory = true,
                                Attributes = attrs,
                                LastModified = modified,
                                Parent = folder
                            };
                            items.Add(dirNode);
                            directFolders++;
                            if ((attrs & FileAttributes.ReparsePoint) == 0)
                            {
                                subFolders ??= new List<(FsItem, string)>(8);
                                subFolders.Add((dirNode, Path.Combine(folderPath, name)));
                            }
                        }
                        else
                        {
                            long fileSize = ((long)findData.nFileSizeHigh << 32) | (uint)findData.nFileSizeLow;
                            long allocated = NativeFileSystem.AlignToCluster(fileSize, clusterSize);
                            items.Add(new FsItem
                            {
                                Name = name,
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

        // Doğrudan çocukları sıkı dizi olarak hemen bağla (tarama sırasında açılan klasörlerde canlı görünür)
        if (items != null && items.Count > 0)
        {
            var arr = items.ToArray();
            if (depth == 0)
            {
                TreeMaintenance.SortArray(arr, folderPath, sortColumn, sortAscending);
            }
            folder.SetChildArray(arr);
            items = null; // List belleğini erken serbest bırak
        }
        else if (depth == 0)
        {
            folder.SetChildArray(null);
        }

        // Doğrudan dosya ve klasör artışlarını canlı olarak kök zincirine yansıt (sıfır çift sayım)
        if (directBytes != 0 || directAllocated != 0 || directFiles != 0 || directFolders != 0)
        {
            if (directFiles > 0)
            {
                Interlocked.Add(ref _filesScanned, directFiles);
                Interlocked.Add(ref _bytesScanned, directBytes);
            }
            for (var p = folder; p != null; p = p.Parent)
            {
                p.AddLiveDelta(directBytes, directAllocated, directFiles, directFolders);
            }
        }

        if (subFolders != null && subFolders.Count > 0 && !ct.IsCancellationRequested)
        {
            // İlk 2 derinlikte SSD/NVMe I/O paralelliği: alt ağaçları eşzamanlı tara
            if (depth < 2 && subFolders.Count > 1)
            {
                var opts = new ParallelOptions { MaxDegreeOfParallelism = maxWorkers };
                Parallel.For(0, subFolders.Count, opts, i =>
                {
                    if (ct.IsCancellationRequested) return;
                    var (subNode, subPath) = subFolders[i];
                    ScanFolder(subNode, subPath, clusterSize, depth + 1, maxWorkers, sortColumn, sortAscending, ct);
                });
            }
            else
            {
                for (int i = 0; i < subFolders.Count; i++)
                {
                    if (ct.IsCancellationRequested) break;
                    var (subNode, subPath) = subFolders[i];
                    ScanFolder(subNode, subPath, clusterSize, depth + 1, maxWorkers, sortColumn, sortAscending, ct);
                }
            }
        }
    }

    private static void FinalizeTree(FsItem root, long rootTotalSize, TreeSortColumn sortColumn, bool sortAscending)
    {
        root.PercentOfParent = 100.0;
        root.PercentOfTotal = rootTotalSize > 0 ? 100.0 : 0.0;

        var stack = new Stack<FsItem>(1024);
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            var arr = dir.RawChildren;
            if (arr == null || arr.Length == 0) continue;

            long parentSize = dir.Size;
            for (int i = 0; i < arr.Length; i++)
            {
                var c = arr[i];
                c.PercentOfTotal = rootTotalSize > 0 ? (double)c.Size / rootTotalSize * 100.0 : 0.0;
                c.PercentOfParent = parentSize > 0 ? (double)c.Size / parentSize * 100.0 : 0.0;
                if (c.IsDirectory && c.HasChildren) stack.Push(c);
            }

            if (arr.Length > 1)
            {
                TreeMaintenance.SortArray(arr, dir.FullPath, sortColumn, sortAscending);
            }
            dir.SyncObservableChildrenIfCreated();
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
