using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Windows.Data;
using AmberTree.Converters;
using AmberTree.Services;

namespace AmberTree.Models;

/// <summary>
/// Ultra-düşük bellekli dosya/klasör düğümü:
/// - Alt düğümler FullPath dizgesi saklamaz (Parent zincirinden anlık üretilir -> milyonlarca düğümde ~450 MB tasarruf).
/// - Çocuklar sıkı FsItem[] dizisinde tutulur; WPF ObservableCollection ve kilit kaydı yalnızca UI'da gerçekten
///   görüntülenen/genişletilen klasörler için tembel oluşturulur (~350 MB tasarruf).
/// </summary>
public class FsItem : INotifyPropertyChanged
{
    private static readonly BulkObservableCollection<FsItem> EmptyChildren = new();

    private const byte FlagDirectory = 1;
    private const byte FlagExpanded = 2;
    private const byte FlagSelected = 4;
    private const byte FlagFilteredOut = 8;

    private string? _explicitPath;
    private FsItem? _parent;
    private FsItem[]? _childArray;
    private BulkObservableCollection<FsItem>? _children;
    private object? _childrenLock;
    private long _size;
    private long _allocatedSize;
    private long _fileCount;
    private long _folderCount;
    private float _percentOfParent;
    private float _percentOfTotal;
    private byte _flags;

    public string Name { get; set; } = string.Empty;
    public DateTime LastModified { get; set; }
    public FileAttributes Attributes { get; set; }

    public string FullPath
    {
        get
        {
            if (_explicitPath != null) return _explicitPath;
            if (_parent == null) return Name;
            return BuildFullPath();
        }
        set
        {
            // Yalnızca kök veya bağımsız (Parent == null) düğümlerde yol saklanır; alt düğümler bellek yemez.
            _explicitPath = _parent == null ? value : null;
        }
    }

    private string BuildFullPath()
    {
        // Yinelemeli ve düşük tahsisli yol birleştirme (derinlik genelde 4-10)
        int depth = 0;
        for (var n = this; n != null; n = n._parent) depth++;
        var parts = new string[depth];
        int i = depth - 1;
        for (var n = this; n != null; n = n._parent)
        {
            parts[i--] = n._parent == null ? (n._explicitPath ?? n.Name) : n.Name;
        }
        var sb = new StringBuilder(128);
        for (int k = 0; k < parts.Length; k++)
        {
            string p = parts[k];
            if (string.IsNullOrEmpty(p)) continue;
            if (sb.Length > 0 && sb[^1] != '\\' && sb[^1] != '/') sb.Append('\\');
            sb.Append(p);
        }
        return sb.ToString();
    }

    public bool IsDirectory
    {
        get => (_flags & FlagDirectory) != 0;
        set { if (value) _flags |= FlagDirectory; else _flags &= unchecked((byte)~FlagDirectory); }
    }

    public long Size
    {
        get => Volatile.Read(ref _size);
        set => Volatile.Write(ref _size, value);
    }

    public long AllocatedSize
    {
        get => Volatile.Read(ref _allocatedSize);
        set => Volatile.Write(ref _allocatedSize, value);
    }

    public long FileCount
    {
        get => Volatile.Read(ref _fileCount);
        set => Volatile.Write(ref _fileCount, value);
    }

    public long FolderCount
    {
        get => Volatile.Read(ref _folderCount);
        set => Volatile.Write(ref _folderCount, value);
    }

    /// <summary>Paralel tarama sırasında kilitsiz canlı boyut/sayaç artışı.</summary>
    internal void AddLiveDelta(long sizeDelta, long allocDelta, long fileDelta, long folderDelta)
    {
        if (sizeDelta != 0) Interlocked.Add(ref _size, sizeDelta);
        if (allocDelta != 0) Interlocked.Add(ref _allocatedSize, allocDelta);
        if (fileDelta != 0) Interlocked.Add(ref _fileCount, fileDelta);
        if (folderDelta != 0) Interlocked.Add(ref _folderCount, folderDelta);
    }

    [JsonIgnore]
    public FsItem? Parent
    {
        get => _parent;
        set
        {
            _parent = value;
            if (value != null) _explicitPath = null; // Alt düğüm olduysa açık yolu bırak (RAM tasarrufu)
        }
    }

    /// <summary>Ham, sıkı çocuk dizisi (UI koleksiyonu oluşturmadan tarama/sıralama/istatistik için).</summary>
    [JsonIgnore]
    internal FsItem[]? RawChildren => Volatile.Read(ref _childArray);

    /// <summary>
    /// WPF ağaç bağlantısı için tembel ObservableCollection.
    /// Dosyalar için paylaşılan boş koleksiyon döner; klasörler için yalnızca UI bağlandığında oluşturulur.
    /// </summary>
    public BulkObservableCollection<FsItem> Children
    {
        get
        {
            var col = Volatile.Read(ref _children);
            if (col != null) return col;
            if (!IsDirectory) return EmptyChildren;
            return EnsureChildren();
        }
    }

    [JsonIgnore]
    public bool HasChildren
    {
        get
        {
            var arr = Volatile.Read(ref _childArray);
            if (arr != null && arr.Length > 0) return true;
            var col = Volatile.Read(ref _children);
            return col != null && col.Count > 0;
        }
    }

    [JsonIgnore]
    public bool IsSubfolder => IsDirectory && _parent != null;

    [JsonIgnore]
    public int Depth
    {
        get
        {
            int d = 0;
            for (var p = _parent; p != null; p = p._parent) d++;
            return d;
        }
    }

    [JsonIgnore]
    public int DepthColorLevel => Depth <= 5 ? Depth : 1 + ((Depth - 1) % 5);

    [JsonIgnore]
    public object SyncLock
    {
        get
        {
            var lk = Volatile.Read(ref _childrenLock);
            if (lk != null) return lk;
            var created = new object();
            return Interlocked.CompareExchange(ref _childrenLock, created, null) ?? created;
        }
    }

    private BulkObservableCollection<FsItem> EnsureChildren()
    {
        var existing = Volatile.Read(ref _children);
        if (existing != null) return existing;

        var lk = SyncLock;
        lock (lk)
        {
            if (_children == null)
            {
                var arr = Volatile.Read(ref _childArray);
                var col = arr != null && arr.Length > 0
                    ? new BulkObservableCollection<FsItem>(arr)
                    : new BulkObservableCollection<FsItem>();
                BindingOperations.EnableCollectionSynchronization(col, lk);
                Volatile.Write(ref _children, col);
            }
            return _children;
        }
    }

    /// <summary>Çocuk dizisini sıkı dizi olarak atar; UI koleksiyonu zaten bağlıysa tek Reset ile eşitler.</summary>
    internal void SetChildArray(FsItem[]? items)
    {
        var clean = items == null || items.Length == 0 ? null : items;
        Volatile.Write(ref _childArray, clean);
        var col = Volatile.Read(ref _children);
        if (col != null)
        {
            lock (SyncLock)
            {
                col.ReplaceAll(clean ?? Array.Empty<FsItem>());
            }
        }
    }

    internal void SetChildrenBulk(List<FsItem> items)
        => SetChildArray(items.Count == 0 ? null : items.ToArray());

    /// <summary>UI koleksiyonu bağlıysa ve sıra/sayı değiştiyse tek Reset ile günceller.</summary>
    internal bool SyncObservableChildrenIfCreated()
    {
        var col = Volatile.Read(ref _children);
        if (col == null) return false;
        var arr = Volatile.Read(ref _childArray) ?? Array.Empty<FsItem>();
        lock (SyncLock)
        {
            bool changed = col.Count != arr.Length;
            if (!changed)
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    if (!ReferenceEquals(col[i], arr[i])) { changed = true; break; }
                }
            }
            if (changed) col.ReplaceAll(arr);
            return changed;
        }
    }

    internal bool RemoveChild(FsItem child)
    {
        var arr = Volatile.Read(ref _childArray);
        bool removed = false;
        if (arr != null)
        {
            int idx = Array.IndexOf(arr, child);
            if (idx >= 0)
            {
                if (arr.Length == 1)
                {
                    Volatile.Write(ref _childArray, null);
                }
                else
                {
                    var next = new FsItem[arr.Length - 1];
                    if (idx > 0) Array.Copy(arr, 0, next, 0, idx);
                    if (idx < arr.Length - 1) Array.Copy(arr, idx + 1, next, idx, arr.Length - idx - 1);
                    Volatile.Write(ref _childArray, next);
                }
                removed = true;
            }
        }
        var col = Volatile.Read(ref _children);
        if (col != null)
        {
            lock (SyncLock) { removed |= col.Remove(child); }
        }
        return removed;
    }

    internal bool ReplaceChild(FsItem oldItem, FsItem newItem)
    {
        var arr = Volatile.Read(ref _childArray);
        bool replaced = false;
        if (arr != null)
        {
            int idx = Array.IndexOf(arr, oldItem);
            if (idx >= 0)
            {
                arr[idx] = newItem;
                replaced = true;
            }
        }
        var col = Volatile.Read(ref _children);
        if (col != null)
        {
            lock (SyncLock)
            {
                int idx = col.IndexOf(oldItem);
                if (idx >= 0) { col[idx] = newItem; replaced = true; }
            }
        }
        return replaced;
    }

    public bool IsExpanded
    {
        get => (_flags & FlagExpanded) != 0;
        set
        {
            bool cur = (_flags & FlagExpanded) != 0;
            if (cur != value)
            {
                if (value) _flags |= FlagExpanded; else _flags &= unchecked((byte)~FlagExpanded);
                OnPropertyChanged();
                if (value && IsDirectory && !HasChildren)
                {
                    EnsureDirectChildrenLoaded();
                }
            }
        }
    }

    public bool IsSelected
    {
        get => (_flags & FlagSelected) != 0;
        set
        {
            bool cur = (_flags & FlagSelected) != 0;
            if (cur != value)
            {
                if (value) _flags |= FlagSelected; else _flags &= unchecked((byte)~FlagSelected);
                OnPropertyChanged();
            }
        }
    }

    public bool IsFilteredOut
    {
        get => (_flags & FlagFilteredOut) != 0;
        set
        {
            bool cur = (_flags & FlagFilteredOut) != 0;
            if (cur != value)
            {
                if (value) _flags |= FlagFilteredOut; else _flags &= unchecked((byte)~FlagFilteredOut);
                OnPropertyChanged();
            }
        }
    }

    public double PercentOfParent
    {
        get => _percentOfParent;
        set
        {
            float v = (float)value;
            if (Math.Abs(_percentOfParent - v) > 0.01f)
            {
                _percentOfParent = v;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PercentOfParentDisplay));
            }
        }
    }

    public double PercentOfTotal
    {
        get => _percentOfTotal;
        set
        {
            float v = (float)value;
            if (Math.Abs(_percentOfTotal - v) > 0.01f)
            {
                _percentOfTotal = v;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PercentOfTotalDisplay));
            }
        }
    }

    /// <summary>Dosya turu kategorisi (alan saklamaz, yalnizca Treemap ciziminde hesaplanir).</summary>
    [JsonIgnore]
    public FileCategory Category => IsDirectory ? default : FileCategoryMap.FromExtension(Path.GetExtension(Name));

    [JsonIgnore]
    public string SizeDisplay => SizeFormatter.FormatBytes(Size, SizeFormatter.CurrentMode);

    [JsonIgnore]
    public string AllocatedSizeDisplay => SizeFormatter.FormatBytes(AllocatedSize, SizeFormatter.CurrentMode);

    /// <summary>Size/AllocatedSize/FileCount/FolderCount degistikten sonra cagrilir.</summary>
    public void NotifySizesChanged()
    {
        OnPropertyChanged(nameof(Size));
        OnPropertyChanged(nameof(AllocatedSize));
        OnPropertyChanged(nameof(FileCount));
        OnPropertyChanged(nameof(FolderCount));
        OnPropertyChanged(nameof(SizeDisplay));
        OnPropertyChanged(nameof(AllocatedSizeDisplay));
    }

    /// <summary>Birim modu degisince yalnizca goruntu metinlerini yeniler.</summary>
    public void NotifyUnitChanged()
    {
        OnPropertyChanged(nameof(SizeDisplay));
        OnPropertyChanged(nameof(AllocatedSizeDisplay));
    }

    public string PercentOfParentDisplay => $"{PercentOfParent:F1}%";
    public string PercentOfTotalDisplay => $"{PercentOfTotal:F1}%";

    public string AttributesDisplay
    {
        get
        {
            Span<char> buf = stackalloc char[7];
            int len = 0;
            var a = Attributes;
            if ((a & FileAttributes.ReadOnly) != 0) buf[len++] = 'R';
            if ((a & FileAttributes.Hidden) != 0) buf[len++] = 'H';
            if ((a & FileAttributes.System) != 0) buf[len++] = 'S';
            if ((a & FileAttributes.Archive) != 0) buf[len++] = 'A';
            if ((a & FileAttributes.Compressed) != 0) buf[len++] = 'C';
            if ((a & FileAttributes.Encrypted) != 0) buf[len++] = 'E';
            if ((a & FileAttributes.ReparsePoint) != 0) buf[len++] = 'L';
            return len == 0 ? "-" : new string(buf[..len]);
        }
    }

    public void EnsureDirectChildrenLoaded(uint? knownClusterSize = null)
    {
        if (!IsDirectory) return;
        string path = FullPath;
        if (string.IsNullOrEmpty(path)) return;
        lock (SyncLock)
        {
            if (HasChildren) return;
            try
            {
                string searchPattern = NativeFileSystem.ToSearchPattern(path);
                IntPtr hFind = NativeFileSystem.FindFirstFileEx(
                    searchPattern,
                    1, // FindExInfoBasic
                    out var findData,
                    0,
                    IntPtr.Zero,
                    2); // FIND_FIRST_EX_LARGE_FETCH

                if (hFind == IntPtr.Zero || hFind.ToInt64() == -1) return;

                var items = new List<FsItem>();
                uint clusterSize = knownClusterSize ?? NativeFileSystem.GetClusterSize(path);

                try
                {
                    do
                    {
                        string name = findData.cFileName;
                        if (name == "." || name == "..") continue;

                        var attrs = (FileAttributes)findData.dwFileAttributes;
                        bool isDir = (attrs & FileAttributes.Directory) != 0;
                        DateTime modified = NativeFileSystem.FileTimeToDateTime(findData.ftLastWriteTime);

                        if (isDir)
                        {
                            items.Add(new FsItem
                            {
                                Name = name,
                                IsDirectory = true,
                                Attributes = attrs,
                                LastModified = modified,
                                Parent = this
                            });
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
                                Parent = this,
                                FileCount = 1,
                                FolderCount = 0
                            });
                        }
                    } while (NativeFileSystem.FindNextFile(hFind, out findData));
                }
                finally
                {
                    NativeFileSystem.FindClose(hFind);
                }

                if (items.Count > 0)
                {
                    var arr = items.ToArray();
                    TreeMaintenance.SortArray(arr, path, TreeMaintenance.ActiveSortColumn, TreeMaintenance.ActiveSortAscending);
                    SetChildArray(arr);
                }
            }
            catch
            {
                // Access denied or unreadable directory
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
