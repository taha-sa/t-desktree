using System;
using System.Collections.Generic;
using System.Linq;
using AmberTree.Models;

namespace AmberTree.Services;

public class TreeStatistics
{
    public List<ExtensionStat> Extensions { get; set; } = new();
    public List<TopFileItem> TopFiles { get; set; } = new();
    public List<AgeDistributionItem> AgeDistribution { get; set; } = new();
}

/// <summary>
/// Tarama bittikten sonra agac uzerinde yapilan bakim isleri: silme/yeniden tarama sonrasi
/// ust zincir toplamlari, yuzdeler, siralama ve Uzanti/En buyuk/Yas istatistikleri.
/// (Tarama algoritmasindan bagimsizdir.)
/// </summary>
public static class TreeMaintenance
{
    public const string NoExtensionLabel = "[Uzantısız]";

    public static readonly string[] AgeRangeNames =
    {
        "1 aydan yeni", "1 - 3 ay", "3 - 6 ay", "6 - 12 ay", "1 yıldan eski"
    };

    /// <summary>Dugumu ebeveyninden cikarir ve ust zincirin toplamlarini dusurur. Kok cikarilamaz.</summary>
    public static bool RemoveFromTree(FsItem item)
    {
        var parent = item.Parent;
        if (parent == null) return false;
        if (!parent.Children.Remove(item)) return false;
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

    /// <summary>Tum agacin % degerlerini yeniden hesaplar (siralamaz).</summary>
    public static void RecalculatePercentages(FsItem root)
    {
        long total = root.Size;
        var stack = new Stack<FsItem>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var item = stack.Pop();
            item.PercentOfTotal = total > 0 ? (double)item.Size / total * 100.0 : 0.0;
            item.PercentOfParent = item.Parent != null && item.Parent.Size > 0
                ? (double)item.Size / item.Parent.Size * 100.0
                : 100.0;
            foreach (var c in item.Children) stack.Push(c);
        }
    }

    /// <summary>
    /// Çocukları boyuta göre azalan sıralar (Move ile; koleksiyonu sıfırlamaz).
    /// Kök dizinde (node.Parent == null) ana klasörler önce (büyükten küçüğe), ardından ana dizindeki dosyalar (büyükten küçüğe).
    /// Alt klasörlerde (node.Parent != null) dosya veya klasörler karma olarak büyükten küçüğe sıralanır.
    /// </summary>
    public static void SortChildren(FsItem node)
    {
        if (node.Children.Count <= 1) return;
        lock (node.SyncLock)
        {
            bool isRoot = node.Parent == null;
            List<FsItem> desired;
            if (isRoot)
            {
                desired = node.Children
                    .OrderByDescending(c => c.IsDirectory)
                    .ThenByDescending(c => c.Size)
                    .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            else
            {
                desired = node.Children
                    .OrderByDescending(c => c.Size)
                    .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            for (int i = 0; i < desired.Count; i++)
            {
                if (!ReferenceEquals(node.Children[i], desired[i]))
                {
                    int oldIndex = node.Children.IndexOf(desired[i]);
                    if (oldIndex >= 0 && oldIndex != i)
                    {
                        node.Children.Move(oldIndex, i);
                    }
                }
            }
        }
    }

    /// <summary>Geriye dönük uyumluluk: SortChildren metodunu çağırır.</summary>
    public static void SortChildrenBySize(FsItem node) => SortChildren(node);

    /// <summary>item'dan koke kadar her dugumun cocuk sirasini yeniler.</summary>
    public static void ResortChain(FsItem? item)
    {
        for (var n = item; n != null; n = n.Parent) SortChildrenBySize(n);
    }

    /// <summary>Verilen yolu kokten asagi inerek bulur.</summary>
    public static FsItem? FindByPath(FsItem? root, string fullPath)
    {
        if (root == null || string.IsNullOrEmpty(fullPath)) return null;
        string target = Normalize(fullPath);
        var cur = root;
        if (string.Equals(Normalize(cur.FullPath), target, StringComparison.OrdinalIgnoreCase)) return cur;

        while (true)
        {
            FsItem? next = null;
            foreach (var c in cur.Children)
            {
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

    /// <summary>Agactan Uzanti / En buyuk 100 / Yas dagilimi istatistiklerini yeniden uretir.</summary>
    public static TreeStatistics ComputeStatistics(FsItem? root, DateTime now)
    {
        var stats = new TreeStatistics();
        long totalSize = root?.Size ?? 0;

        var ext = new Dictionary<string, (long count, long size, long alloc)>(StringComparer.OrdinalIgnoreCase);
        var top = new List<TopFileItem>();
        var ageSize = new long[5];
        var ageCount = new long[5];

        if (root != null)
        {
            var stack = new Stack<FsItem>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (node.IsDirectory)
                {
                    foreach (var c in node.Children) stack.Push(c);
                    continue;
                }

                string e = System.IO.Path.GetExtension(node.Name).ToLowerInvariant();
                if (string.IsNullOrEmpty(e)) e = NoExtensionLabel;
                ext.TryGetValue(e, out var cur);
                ext[e] = (cur.count + 1, cur.size + node.Size, cur.alloc + node.AllocatedSize);

                if (node.Size > 1024 * 1024)
                {
                    top.Add(new TopFileItem
                    {
                        Name = node.Name,
                        DirectoryPath = node.Parent?.FullPath ?? string.Empty,
                        FullPath = node.FullPath,
                        Extension = e,
                        Size = node.Size,
                        AllocatedSize = node.AllocatedSize,
                        LastModified = node.LastModified
                    });
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

        stats.TopFiles = top.OrderByDescending(f => f.Size).Take(100).Select((f, i) =>
        {
            f.Rank = i + 1;
            f.PercentOfTotal = totalSize > 0 ? (double)f.Size / totalSize * 100.0 : 0.0;
            return f;
        }).ToList();

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
