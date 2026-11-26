using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Threading;
using System.Windows.Data;
using AmberTree.Converters;
using AmberTree.Services;

namespace AmberTree.Models;

public class FsItem : INotifyPropertyChanged
{
    private static readonly BulkObservableCollection<FsItem> EmptyChildren = new();

    private object? _childrenLock;
    private BulkObservableCollection<FsItem>? _children;
    private bool _isExpanded;
    private bool _isSelected;
    private bool _isFilteredOut;
    private double _percentOfParent;
    private double _percentOfTotal;

    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public long AllocatedSize { get; set; }
    public long FileCount { get; set; }
    public long FolderCount { get; set; }
    public DateTime LastModified { get; set; }
    public FileAttributes Attributes { get; set; }

    [JsonIgnore]
    public FsItem? Parent { get; set; }

    /// <summary>
    /// Dosyalar için paylaşılan boş koleksiyon döner (yüz binlerce gereksiz koleksiyon ve WPF kilit kaydını önler).
    /// Klasörler için yalnızca gerektiğinde tek seferde oluşturulur.
    /// </summary>
    public BulkObservableCollection<FsItem> Children
    {
        get
        {
            if (_children != null) return _children;
            if (!IsDirectory) return EmptyChildren;
            return EnsureChildren();
        }
    }

    [JsonIgnore]
    public bool HasChildren => _children != null && _children.Count > 0;

    [JsonIgnore]
    public bool IsSubfolder => IsDirectory && Parent != null;

    [JsonIgnore]
    public int Depth
    {
        get
        {
            int d = 0;
            for (var p = Parent; p != null; p = p.Parent) d++;
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
                var col = new BulkObservableCollection<FsItem>();
                BindingOperations.EnableCollectionSynchronization(col, lk);
                Volatile.Write(ref _children, col);
            }
            return _children;
        }
    }

    internal void SetChildrenBulk(List<FsItem> items)
    {
        if (items.Count == 0 && _children == null) return;
        var col = EnsureChildren();
        lock (SyncLock)
        {
            col.ReplaceAll(items);
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded != value)
            {
                _isExpanded = value;
                OnPropertyChanged();
                if (value && IsDirectory)
                {
                    EnsureDirectChildrenLoaded();
                }
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsFilteredOut
    {
        get => _isFilteredOut;
        set
        {
            if (_isFilteredOut != value)
            {
                _isFilteredOut = value;
                OnPropertyChanged();
            }
        }
    }

    public double PercentOfParent
    {
        get => _percentOfParent;
        set
        {
            if (Math.Abs(_percentOfParent - value) > 0.001)
            {
                _percentOfParent = value;
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
            if (Math.Abs(_percentOfTotal - value) > 0.001)
            {
                _percentOfTotal = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PercentOfTotalDisplay));
            }
        }
    }

    private FileCategory? _category;

    /// <summary>Dosya turu kategorisi (tembel hesaplanir). Klasorler icin anlamli degildir; IsDirectory'ye bakin.</summary>
    [JsonIgnore]
    public FileCategory Category
    {
        get
        {
            _category ??= IsDirectory ? default(FileCategory) : FileCategoryMap.FromExtension(Path.GetExtension(Name));
            return _category.Value;
        }
    }

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
        if (!IsDirectory || string.IsNullOrEmpty(FullPath)) return;
        lock (SyncLock)
        {
            if (HasChildren) return;
            try
            {
                string searchPattern = NativeFileSystem.ToSearchPattern(FullPath);
                IntPtr hFind = NativeFileSystem.FindFirstFileEx(
                    searchPattern,
                    1, // FindExInfoBasic
                    out var findData,
                    0,
                    IntPtr.Zero,
                    2); // FIND_FIRST_EX_LARGE_FETCH

                if (hFind == IntPtr.Zero || hFind.ToInt64() == -1) return;

                var items = new List<FsItem>();
                uint clusterSize = knownClusterSize ?? NativeFileSystem.GetClusterSize(FullPath);

                try
                {
                    do
                    {
                        string name = findData.cFileName;
                        if (name == "." || name == "..") continue;

                        var attrs = (FileAttributes)findData.dwFileAttributes;
                        bool isDir = (attrs & FileAttributes.Directory) != 0;
                        string childPath = Path.Combine(FullPath, name);
                        DateTime modified = NativeFileSystem.FileTimeToDateTime(findData.ftLastWriteTime);

                        if (isDir)
                        {
                            items.Add(new FsItem
                            {
                                Name = name,
                                FullPath = childPath,
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
                                FullPath = childPath,
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
                    TreeMaintenance.SortList(items, FullPath, TreeMaintenance.ActiveSortColumn, TreeMaintenance.ActiveSortAscending);
                    EnsureChildren().ReplaceAll(items);
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
