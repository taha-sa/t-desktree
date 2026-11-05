using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Data;
using AmberTree.Converters;
using AmberTree.Services;

namespace AmberTree.Models;

public class FsItem : INotifyPropertyChanged
{
    private readonly object _childrenLock = new();
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
    public ObservableCollection<FsItem> Children { get; } = new();

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
    public object SyncLock => _childrenLock;

    public FsItem()
    {
        BindingOperations.EnableCollectionSynchronization(Children, _childrenLock);
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
            var res = "";
            if ((Attributes & FileAttributes.ReadOnly) != 0) res += "R";
            if ((Attributes & FileAttributes.Hidden) != 0) res += "H";
            if ((Attributes & FileAttributes.System) != 0) res += "S";
            if ((Attributes & FileAttributes.Archive) != 0) res += "A";
            if ((Attributes & FileAttributes.Compressed) != 0) res += "C";
            if ((Attributes & FileAttributes.Encrypted) != 0) res += "E";
            if ((Attributes & FileAttributes.ReparsePoint) != 0) res += "L";
            return string.IsNullOrEmpty(res) ? "-" : res;
        }
    }

    public void EnsureDirectChildrenLoaded()
    {
        if (!IsDirectory || string.IsNullOrEmpty(FullPath)) return;
        lock (_childrenLock)
        {
            if (Children.Count > 0) return;
            try
            {
                string searchPattern = Path.Combine(FullPath, "*");
                IntPtr hFind = NativeFileSystem.FindFirstFileEx(
                    searchPattern,
                    1, // FindExInfoBasic
                    out var findData,
                    0,
                    IntPtr.Zero,
                    2); // FIND_FIRST_EX_LARGE_FETCH

                if (hFind == IntPtr.Zero || hFind.ToInt64() == -1) return;

                var subFolders = new List<FsItem>();
                var directFiles = new List<FsItem>();
                uint clusterSize = NativeFileSystem.GetClusterSize(FullPath);

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
                            subFolders.Add(new FsItem
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
                            directFiles.Add(new FsItem
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

                foreach (var sub in subFolders) Children.Add(sub);
                foreach (var f in directFiles) Children.Add(f);
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
