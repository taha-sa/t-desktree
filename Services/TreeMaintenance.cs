using AmberTree.Localization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AmberTree.Models;

namespace AmberTree.Services;

public enum TreeSortColumn
{
    Name,
    Size,
    Allocated,
    Percent,
    Files,
    Folders,
    Modified,
    Attributes
}

public class TreeStatistics
{
    public List<ExtensionStat> Extensions { get; set; } = new();
    public List<TopFileItem> TopFiles { get; set; } = new();
    public List<AgeDistributionItem> AgeDistribution { get; set; } = new();
}

/// <summary>
/// Tarama sirasi ve sonrasi agac bakim isleri: siralama (surucu kokunde klasor/dosya ayri, alt klasorlerde karma),
/// canli genisletilmis dugum yenileme, silme/yeniden tarama sonrasi ust zincir toplamlari ve istatistikler.
/// </summary>
public static class TreeMaintenance
{
    public static TreeSortColumn ActiveSortColumn { get; set; } = TreeSortColumn.Size;
    public static bool ActiveSortAscending { get; set; } = false;

    public static string NoExtensionLabel => Strings.NoExtension;

    public static string[] AgeRangeNames =>
    [
        Strings.AgeUnderMonth, Strings.Age1to3, Strings.Age3to6, Strings.Age6to12, Strings.AgeOverYear
    ];

    /// <summary>
    /// Yolun bir sürücü kök dizini (ör. C:\, D:\) veya ağ paylaşım kökü olup olmadığını döndürür.
    /// Ana sürücü dizininde klasörler üstte (kendi içinde sıralı), dosyalar altta (kendi içinde sıralı);
    /// alt klasörlerde (ör. AppData) klasörler ve dosyalar birlikte sıralanır.
    /// </summary>
    public static bool IsDriveRoot(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return false;
        try
        {
            string trimmed = fullPath.TrimEnd('\\', '/');
            string? root = Path.GetPathRoot(fullPath)?.TrimEnd('\\', '/');
            return !string.IsNullOrEmpty(root) && string.Equals(trimmed, root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static int CompareItems(FsItem a, FsItem b, TreeSortColumn column, bool ascending, bool splitDirsAndFiles)
    {
        if (splitDirsAndFiles && a.IsDirectory != b.IsDirectory)
        {
            return a.IsDirectory ? -1 : 1;
        }

        int cmp = column switch
        {
            TreeSortColumn.Name => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name),
            TreeSortColumn.Size => a.Size.CompareTo(b.Size),
            TreeSortColumn.Allocated => a.AllocatedSize.CompareTo(b.AllocatedSize),
            TreeSortColumn.Percent => a.PercentOfParent.CompareTo(b.PercentOfParent),
            TreeSortColumn.Files => a.FileCount.CompareTo(b.FileCount),
            TreeSortColumn.Folders => a.FolderCount.CompareTo(b.FolderCount),
            TreeSortColumn.Modified => a.LastModified.CompareTo(b.LastModified),
            TreeSortColumn.Attributes => string.Compare(a.AttributesDisplay, b.AttributesDisplay, StringComparison.Ordinal),
            _ => a.Size.CompareTo(b.Size)
        };

        if (!ascending) cmp = -cmp;
        if (cmp != 0) return cmp;

        int sizeTie = b.Size.CompareTo(a.Size);
        return sizeTie != 0 ? sizeTie : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
    }

    /// <summary>Çocuk dizisini yerinde, ek bellek ayırmadan O(N log N) sıralar.</summary>
    public static void SortArray(FsItem[] items, string? parentFullPath, TreeSortColumn column = TreeSortColumn.Size, bool ascending = false)
    {
        if (items.Length <= 1) return;
        bool splitDirsAndFiles = IsDriveRoot(parentFullPath);
        Array.Sort(items, (a, b) => CompareItems(a, b, column, ascending, splitDirsAndFiles));
    }

    public static void SortList(List<FsItem> items, string? parentFullPath, TreeSortColumn column = TreeSortColumn.Size, bool ascending = false)
    {
        if (items.Count <= 1) return;
        bool splitDirsAndFiles = IsDriveRoot(parentFullPath);
        items.Sort((a, b) => CompareItems(a, b, column, ascending, splitDirsAndFiles));
    }

    /// <summary>Dugumu ebeveyninden cikarir ve ust zincirin toplamlarini dusurur. Kok cikarilamaz.</summary>
    public static bool RemoveFromTree(FsItem item)
    {
        var parent = item.Parent;
        if (parent == null) return false;
        if (!parent.RemoveChild(item)) return false;
        ApplyDeltaToAncestors(parent, -item.Size, -item.AllocatedSize, -item.FileCount,
            -(item.IsDirectory ? item.FolderCount + 1 : 0));
        item.Parent = null;
        return true;
    }

    /// <summary>start dahil, kokten start'a kadar olan zincire fark uygular ve bildirim gonderir.</summary>
    public static void ApplyDeltaToAncestors(FsItem? start, long size, long allocated, long files, long folders)
    {
        for (var n = start; n != null; n = n.Parent)
        {
            n.Size = Math.Max(0, n.Size + size);
            n.AllocatedSize = Math.Max(0, n.AllocatedSize + allocated);
            n.FileCount = Math.Max(0, n.FileCount + files);
            n.FolderCount = Math.Max(0, n.FolderCount + folders);
            n.NotifySizesChanged();
        }
    }

    /// <summary>Tum agacin % degerlerini yeniden hesaplar (siralamaz, UI koleksiyonu uretmez).</summary>
    public static void RecalculatePercentages(FsItem root)
    {
        long total = root.Size;
        var stack = new Stack<FsItem>(256);
        stack.Push(root);
        while (stack.Count > 0)
        {
            var item = stack.Pop();
            item.PercentOfTotal = total > 0 ? (double)item.Size / total * 100.0 : 0.0;
            item.PercentOfParent = item.Parent != null && item.Parent.Size > 0
                ? (double)item.Size / item.Parent.Size * 100.0
                : 100.0;
            var arr = item.RawChildren;
            if (arr != null)
            {
                for (int i = 0; i < arr.Length; i++) stack.Push(arr[i]);
            }
        }
    }

    /// <summary>
    /// Tarama devam ederken yalnızca açık (genişletilmiş) klasörlerin görünen alt öğelerini
    /// canlı olarak günceller (boyut/dosya/klasör/yüzde) ve aktif sıralama kuralına göre sıralar.
    /// </summary>
    public static void RefreshLiveExpandedTree(FsItem? root, TreeSortColumn column, bool ascending)
    {
        if (root == null) return;
        long totalSize = root.Size;
        root.PercentOfParent = 100.0;
        root.PercentOfTotal = totalSize > 0 ? 100.0 : 0.0;
        root.NotifySizesChanged();

        var stack = new Stack<FsItem>(16);
        if (root.IsExpanded) stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            var arr = dir.RawChildren;
            if (arr == null || arr.Length == 0) continue;

            long parentSize = dir.Size;
            for (int i = 0; i < arr.Length; i++)
            {
                var c = arr[i];
                c.PercentOfParent = parentSize > 0 ? (double)c.Size / parentSize * 100.0 : 0.0;
                c.PercentOfTotal = totalSize > 0 ? (double)c.Size / totalSize * 100.0 : 0.0;
                c.NotifySizesChanged();
                if (c.IsDirectory && c.IsExpanded) stack.Push(c);
            }

            SortChildren(dir, column, ascending);
        }
    }

    /// <summary>
    /// Çocukları belirtilen sütun ve yöne göre yerinde O(N log N) sıralar;
    /// UI koleksiyonu yalnızca bağlıysa tek Reset ile eşitler (kapalı 1M klasörde 0 WPF maliyeti).
    /// </summary>
    public static void SortChildren(FsItem node, TreeSortColumn column, bool ascending)
    {
        var arr = node.RawChildren;
        if (arr == null || arr.Length <= 1) return;
        bool splitDirsAndFiles = IsDriveRoot(node.FullPath);
        Array.Sort(arr, (a, b) => CompareItems(a, b, column, ascending, splitDirsAndFiles));
        node.SyncObservableChildrenIfCreated();
    }

    public static void SortChildren(FsItem node) => SortChildren(node, ActiveSortColumn, ActiveSortAscending);

    /// <summary>Tüm ağacı yinelemeli olarak belirtilen sütun ve yöne göre sıralar.</summary>
    public static void SortTree(FsItem? root, TreeSortColumn column, bool ascending)
    {
        if (root == null) return;
        ActiveSortColumn = column;
        ActiveSortAscending = ascending;

        var stack = new Stack<FsItem>(256);
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            var arr = node.RawChildren;
            if (arr == null || arr.Length == 0) continue;
            if (arr.Length > 1) SortChildren(node, column, ascending);
            for (int i = 0; i < arr.Length; i++)
            {
                var c = arr[i];
                if (c.IsDirectory && c.HasChildren) stack.Push(c);
            }
        }
    }

    public static void SortChildrenBySize(FsItem node) => SortChildren(node, TreeSortColumn.Size, false);

    public static void ResortChain(FsItem? item)
    {
        for (var n = item; n != null; n = n.Parent) SortChildren(n, ActiveSortColumn, ActiveSortAscending);
    }

    public static FsItem? FindByPath(FsItem? root, string fullPath)
    {
        if (root == null || string.IsNullOrEmpty(fullPath)) return null;
        string target = Normalize(fullPath);
        var cur = root;
        if (string.Equals(Normalize(cur.FullPath), target, StringComparison.OrdinalIgnoreCase)) return cur;

        while (true)
        {
            var arr = cur.RawChildren;
            if (arr == null || arr.Length == 0) return null;
            FsItem? next = null;
            for (int i = 0; i < arr.Length; i++)
            {
                var c = arr[i];
                string cp = Normalize(c.FullPath);
                if (string.Equals(cp, target, StringComparison.OrdinalIgnoreCase)) return c;
                if (c.IsDirectory && target.StartsWith(cp + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    next = c;
                    break;
                }
            }
            if (next == null) return null;
            cur = next;
        }
    }

    private static string Normalize(string p) => p.Length > 3 ? p.TrimEnd('\\', '/') : p;

    public static bool IsSelfOrDescendant(FsItem? candidate, FsItem ancestor)
    {
        for (var n = candidate; n != null; n = n.Parent)
            if (ReferenceEquals(n, ancestor)) return true;
        return false;
    }

    /// <summary>Agactan Uzanti / En buyuk 100 / Yas dagilimi istatistiklerini UI koleksiyonu olusturmadan uretir.</summary>
    public static TreeStatistics ComputeStatistics(FsItem? root, DateTime now)
    {
        var stats = new TreeStatistics();
        long totalSize = root?.Size ?? 0;

        var ext = new Dictionary<string, (long count, long size, long alloc)>(StringComparer.OrdinalIgnoreCase);
        var topHeap = new PriorityQueue<FsItem, long>(101);
        var ageSize = new long[5];
        var ageCount = new long[5];

        if (root != null)
        {
            var stack = new Stack<FsItem>(1024);
            stack.Push(root);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (node.IsDirectory)
                {
                    var arr = node.RawChildren;
                    if (arr != null)
                    {
                        for (int i = 0; i < arr.Length; i++) stack.Push(arr[i]);
                    }
                    continue;
                }

                string e = Path.GetExtension(node.Name);
                e = string.IsNullOrEmpty(e) ? NoExtensionLabel : e.ToLowerInvariant();
                ext.TryGetValue(e, out var cur);
                ext[e] = (cur.count + 1, cur.size + node.Size, cur.alloc + node.AllocatedSize);

                if (node.Size > 0)
                {
                    if (topHeap.Count < 100)
                    {
                        topHeap.Enqueue(node, node.Size);
                    }
                    else if (node.Size > topHeap.Peek().Size)
                    {
                        topHeap.Dequeue();
                        topHeap.Enqueue(node, node.Size);
                    }
                }

                double days = (now - node.LastModified).TotalDays;
                int b = days switch { < 30 => 0, < 90 => 1, < 180 => 2, < 365 => 3, _ => 4 };
                ageSize[b] += node.Size;
                ageCount[b]++;
            }
        }

        stats.Extensions = ext.Select(kv => new ExtensionStat
        {
            Extension = kv.Key,
            Description = ScanEngine.GetExtensionDescription(kv.Key),
            FileCount = kv.Value.count,
            TotalSize = kv.Value.size,
            AllocatedSize = kv.Value.alloc,
            PercentOfTotal = totalSize > 0 ? (double)kv.Value.size / totalSize * 100.0 : 0.0
        }).OrderByDescending(x => x.TotalSize).ToList();

        var topNodes = new List<FsItem>(topHeap.Count);
        while (topHeap.Count > 0) topNodes.Add(topHeap.Dequeue());
        topNodes.Reverse();
        var topList = new List<TopFileItem>(topNodes.Count);
        for (int i = 0; i < topNodes.Count; i++)
        {
            var f = topNodes[i];
            string e = Path.GetExtension(f.Name);
            topList.Add(new TopFileItem
            {
                Rank = i + 1,
                Name = f.Name,
                DirectoryPath = f.Parent?.FullPath ?? string.Empty,
                FullPath = f.FullPath,
                Extension = string.IsNullOrEmpty(e) ? NoExtensionLabel : e.ToLowerInvariant(),
                Size = f.Size,
                AllocatedSize = f.AllocatedSize,
                LastModified = f.LastModified,
                PercentOfTotal = totalSize > 0 ? (double)f.Size / totalSize * 100.0 : 0.0
            });
        }
        stats.TopFiles = topList;

        for (int i = 0; i < 5; i++)
        {
            stats.AgeDistribution.Add(new AgeDistributionItem
            {
                RangeName = AgeRangeNames[i],
                FileCount = ageCount[i],
                TotalSize = ageSize[i],
                PercentOfTotal = totalSize > 0 ? (double)ageSize[i] / totalSize * 100.0 : 0.0
            });
        }
        return stats;
    }
}
