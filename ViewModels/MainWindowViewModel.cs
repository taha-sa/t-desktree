using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using AmberTree.Converters;
using AmberTree.Models;
using AmberTree.Services;

namespace AmberTree.ViewModels;

/// <summary>Icerik alaninin ana durumu.</summary>
public enum ViewState
{
    Empty,
    Scanning,
    Results,
    Error
}

/// <summary>Sonuc gorunumlerinden hangisinin aktif oldugu (Ctrl+1..5 sirasiyla).</summary>
public enum ActiveView
{
    Tree,
    Treemap,
    Extensions,
    TopFiles,
    Age
}

public class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly ScanEngine _scanEngine = new();
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _ui;
    private CancellationTokenSource? _cts;
    private bool _isDeleting;

    private string _selectedPath = "C:\\";
    private DriveInfoModel? _selectedDrive;
    private bool _isScanning;
    private string _statusText = "Hazır. Bir sürücü ya da klasör seçip Tara'ya basın.";
    private string _currentScanPath = string.Empty;
    private long _scannedFiles;
    private long _scannedFolders;
    private long _scannedBytes;
    private string _elapsedScanTime = "00:00:00";
    private string _scanSpeed = "0 dosya/sn";
    private FsItem? _rootItem;
    private FsItem? _selectedItem;
    private SizeUnitMode _currentUnitMode = SizeUnitMode.Auto;
    private string _searchText = string.Empty;
    private ViewState _viewState = ViewState.Empty;
    private ActiveView _activeView = ActiveView.Tree;
    private string _errorMessage = string.Empty;
    private string _lastScannedPath = string.Empty;
    private FsItem? _treemapRoot;
    private TimeSpan _lastScanDuration;

    public ObservableCollection<DriveInfoModel> Drives { get; } = new();
    public ObservableCollection<FsItem> RootCollection { get; } = new();
    public ObservableCollection<ExtensionStat> Extensions { get; } = new();
    public ObservableCollection<TopFileItem> TopFiles { get; } = new();
    public ObservableCollection<AgeDistributionItem> AgeDistribution { get; } = new();

    /// <summary>Kokten TreemapRoot'a kadar olan klasorler (breadcrumb).</summary>
    public ObservableCollection<FsItem> TreemapBreadcrumb { get; } = new();

    // ---------------------------------------------------------------- durum

    public ViewState ViewState
    {
        get => _viewState;
        private set
        {
            if (_viewState != value)
            {
                _viewState = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsEmptyView));
                OnPropertyChanged(nameof(IsScanningView));
                OnPropertyChanged(nameof(IsResultsView));
                OnPropertyChanged(nameof(IsErrorView));
            }
        }
    }

    public bool IsEmptyView => ViewState == ViewState.Empty;
    public bool IsScanningView => ViewState == ViewState.Scanning;
    public bool IsResultsView => ViewState == ViewState.Results || ViewState == ViewState.Scanning;
    public bool IsErrorView => ViewState == ViewState.Error;

    public ActiveView ActiveView
    {
        get => _activeView;
        set
        {
            if (_activeView != value)
            {
                _activeView = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsTreeActive));
                OnPropertyChanged(nameof(IsTreemapActive));
                OnPropertyChanged(nameof(IsExtensionsActive));
                OnPropertyChanged(nameof(IsTopFilesActive));
                OnPropertyChanged(nameof(IsAgeActive));
            }
        }
    }

    public bool IsTreeActive => ActiveView == ActiveView.Tree;
    public bool IsTreemapActive => ActiveView == ActiveView.Treemap;
    public bool IsExtensionsActive => ActiveView == ActiveView.Extensions;
    public bool IsTopFilesActive => ActiveView == ActiveView.TopFiles;
    public bool IsAgeActive => ActiveView == ActiveView.Age;

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            value ??= string.Empty;
            if (_errorMessage != value)
            {
                _errorMessage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorMessage);

    public string LastScannedPath
    {
        get => _lastScannedPath;
        private set
        {
            if (_lastScannedPath != value)
            {
                _lastScannedPath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasLastScannedPath));
            }
        }
    }

    public bool HasLastScannedPath => !string.IsNullOrEmpty(_lastScannedPath);

    public string SelectedPath
    {
        get => _selectedPath;
        set
        {
            if (_selectedPath != value)
            {
                _selectedPath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanScan));
                RaiseDriveSummary();
            }
        }
    }

    public DriveInfoModel? SelectedDrive
    {
        get => _selectedDrive;
        set
        {
            if (_selectedDrive != value)
            {
                _selectedDrive = value;
                OnPropertyChanged();
                if (value != null && !string.IsNullOrEmpty(value.RootDirectory))
                {
                    SelectedPath = value.RootDirectory;
                }
                RaiseDriveSummary();
            }
        }
    }

    public bool IsScanning
    {
        get => _isScanning;
        set
        {
            if (_isScanning != value)
            {
                _isScanning = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanScan));
                OnPropertyChanged(nameof(CanStop));
            }
        }
    }

    public bool CanScan => !IsScanning && !_isDeleting && !string.IsNullOrWhiteSpace(SelectedPath);
    public bool CanStop => IsScanning;

    public string StatusText
    {
        get => _statusText;
        set
        {
            if (_statusText != value)
            {
                _statusText = value;
                OnPropertyChanged();
            }
        }
    }

    public string CurrentScanPath
    {
        get => _currentScanPath;
        set
        {
            if (_currentScanPath != value)
            {
                _currentScanPath = value;
                OnPropertyChanged();
            }
        }
    }

    public long ScannedFiles
    {
        get => _scannedFiles;
        set
        {
            if (_scannedFiles != value)
            {
                _scannedFiles = value;
                OnPropertyChanged();
            }
        }
    }

    public long ScannedFolders
    {
        get => _scannedFolders;
        set
        {
            if (_scannedFolders != value)
            {
                _scannedFolders = value;
                OnPropertyChanged();
            }
        }
    }

    public long ScannedBytes
    {
        get => _scannedBytes;
        set
        {
            if (_scannedBytes != value)
            {
                _scannedBytes = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ScannedBytesDisplay));
            }
        }
    }

    public string ScannedBytesDisplay => SizeFormatter.FormatBytes(ScannedBytes, CurrentUnitMode);

    public string ElapsedScanTime
    {
        get => _elapsedScanTime;
        set
        {
            if (_elapsedScanTime != value)
            {
                _elapsedScanTime = value;
                OnPropertyChanged();
            }
        }
    }

    public string ScanSpeed
    {
        get => _scanSpeed;
        set
        {
            if (_scanSpeed != value)
            {
                _scanSpeed = value;
                OnPropertyChanged();
            }
        }
    }

    public FsItem? RootItem
    {
        get => _rootItem;
        set
        {
            if (_rootItem != value)
            {
                _rootItem = value;
                OnPropertyChanged();
                RootCollection.Clear();
                if (value != null)
                {
                    RootCollection.Add(value);
                }
                TreemapRoot = value;
                RaiseSummary();
                OnPropertyChanged(nameof(HasSearchResults));
                OnPropertyChanged(nameof(ShowNoSearchResults));
            }
        }
    }

    public FsItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (_selectedItem != value)
            {
                _selectedItem = value;
                OnPropertyChanged();
            }
        }
    }

    public SizeUnitMode CurrentUnitMode
    {
        get => _currentUnitMode;
        set
        {
            if (_currentUnitMode != value)
            {
                _currentUnitMode = value;
                SizeFormatter.CurrentMode = value;
                OnPropertyChanged();
                RefreshUnitDisplays();
            }
        }
    }

    // ---------------------------------------------------------------- ozet seridi

    public string TotalSizeDisplay => SizeFormatter.FormatBytes(RootItem?.Size ?? 0, CurrentUnitMode);
    public string TotalAllocatedDisplay => SizeFormatter.FormatBytes(RootItem?.AllocatedSize ?? 0, CurrentUnitMode);
    public string TotalFilesDisplay => (RootItem?.FileCount ?? 0).ToString("N0", CultureInfo.CurrentCulture);
    public string TotalFoldersDisplay => (RootItem?.FolderCount ?? 0).ToString("N0", CultureInfo.CurrentCulture);
    public string ScanDurationDisplay => IsScanning ? ElapsedScanTime : $"{_lastScanDuration.TotalSeconds:F1} sn";

    /// <summary>Tarama sonrasi taranan yolun, tarama oncesi secili surucunun doluluk yuzdesi (0-100).</summary>
    public double DriveUsedPercent => ResolveSummaryDrive()?.UsedPercentage ?? 0.0;
    public string DriveTotalDisplay => SizeFormatter.FormatBytes(ResolveSummaryDrive()?.TotalSize ?? 0, CurrentUnitMode);
    public string DriveFreeDisplay => SizeFormatter.FormatBytes(ResolveSummaryDrive()?.AvailableFreeSpace ?? 0, CurrentUnitMode);
    public string DriveUsedDisplay => SizeFormatter.FormatBytes(ResolveSummaryDrive()?.UsedSpace ?? 0, CurrentUnitMode);

    // ---------------------------------------------------------------- arama

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText != value)
            {
                _searchText = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsSearchActive));
                ApplyFilter(value);
            }
        }
    }

    public bool IsSearchActive => !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>Arama yokken ya da en az bir eslesme varken true. false ise "sonuc yok" gosterilir.</summary>
    public bool HasSearchResults =>
        !IsSearchActive || RootItem == null || !RootItem.IsFilteredOut;

    public bool ShowNoSearchResults => IsSearchActive && RootItem != null && !HasSearchResults;

    /// <summary>Ctrl+F: arama kutusuna odaklanilmasini ister (View dinler).</summary>
    public event EventHandler? FocusSearchRequested;

    /// <summary>Birim modu degisti; DataGrid vb. yenilemek isteyenler dinleyebilir.</summary>
    public event EventHandler? UnitChanged;

    // ---------------------------------------------------------------- tema

    public bool IsDarkTheme => ThemeService.Current.IsDark;

    // ---------------------------------------------------------------- treemap

    public FsItem? TreemapRoot
    {
        get => _treemapRoot;
        set
        {
            if (!ReferenceEquals(_treemapRoot, value))
            {
                _treemapRoot = value;
                RebuildBreadcrumb();
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanTreemapGoUp));
            }
        }
    }

    public bool CanTreemapGoUp => TreemapRoot?.Parent != null;

    // ---------------------------------------------------------------- komutlar

    public ICommand StartScanCommand { get; }
    public ICommand StopScanCommand { get; }
    public ICommand BrowseFolderCommand { get; }
    public ICommand ExportCsvCommand { get; }
    public ICommand ExportJsonCommand { get; }
    public ICommand ExportTxtCommand { get; }
    public ICommand SetUnitModeCommand { get; }

    /// <summary>Parametre: ActiveView, ad ("Treemap") ya da 1..5 (Ctrl+1..5).</summary>
    public ICommand NavigateCommand { get; }
    public ICommand DismissErrorCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand FocusSearchCommand { get; }

    /// <summary>Esc: tarama varsa durdurur; yoksa aramayi temizler; o da yoksa hatayi kapatir.</summary>
    public ICommand EscapeCommand { get; }

    /// <summary>Parametre: FsItem | TopFileItem | null (null ise SelectedItem). Del tusu icin parametresiz de calisir.</summary>
    public ICommand RecycleCommand { get; }
    /// <summary>Parametre: FsItem | TopFileItem | null (null ise SelectedItem). Shift+Del.</summary>
    public ICommand DeletePermanentCommand { get; }
    public ICommand RescanFolderCommand { get; }
    public ICommand OpenInExplorerCommand { get; }
    public ICommand OpenTerminalCommand { get; }
    public ICommand CopyPathCommand { get; }

    public ICommand ToggleThemeCommand { get; }
    public ICommand TreemapUpCommand { get; }
    /// <summary>Parametre: FsItem (klasor; dosya gelirse ust klasore gider).</summary>
    public ICommand TreemapNavigateCommand { get; }

    public MainWindowViewModel() : this(new DialogService(), new WpfUiDispatcher())
    {
    }

    public MainWindowViewModel(IDialogService dialogs, IUiDispatcher ui)
    {
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));

        StartScanCommand = new RelayCommand(async _ => await StartScanAsync(), _ => CanScan);
        StopScanCommand = new RelayCommand(_ => StopScan(), _ => CanStop);
        BrowseFolderCommand = new RelayCommand(_ => BrowseFolder());
        ExportCsvCommand = new RelayCommand(async _ => await ExportAsync("csv"), _ => RootItem != null);
        ExportJsonCommand = new RelayCommand(async _ => await ExportAsync("json"), _ => RootItem != null);
        ExportTxtCommand = new RelayCommand(async _ => await ExportAsync("txt"), _ => RootItem != null);
        SetUnitModeCommand = new RelayCommand(param =>
        {
            if (param is SizeUnitMode mode)
            {
                CurrentUnitMode = mode;
            }
            else if (param is string str && Enum.TryParse<SizeUnitMode>(str, out var parsed))
            {
                CurrentUnitMode = parsed;
            }
        });

        NavigateCommand = new RelayCommand(Navigate);
        DismissErrorCommand = new RelayCommand(_ => DismissError());
        ClearSearchCommand = new RelayCommand(_ => SearchText = string.Empty, _ => !string.IsNullOrEmpty(SearchText));
        FocusSearchCommand = new RelayCommand(_ => FocusSearchRequested?.Invoke(this, EventArgs.Empty));
        EscapeCommand = new RelayCommand(_ =>
        {
            if (IsScanning) StopScan();
            else if (!string.IsNullOrEmpty(SearchText)) SearchText = string.Empty;
            else if (HasError) DismissError();
        });

        RecycleCommand = new RelayCommand(async p => await DeleteAsync(p, permanent: false), CanActOnItem);
        DeletePermanentCommand = new RelayCommand(async p => await DeleteAsync(p, permanent: true), CanActOnItem);
        RescanFolderCommand = new RelayCommand(async p => await RescanFolderAsync(p),
            p => !IsScanning && !_isDeleting && (p is FsItem { IsDirectory: true } || (p == null && SelectedItem is { IsDirectory: true })));
        OpenInExplorerCommand = new RelayCommand(p => ShellAction(p, ShellHelper.TryOpenInExplorer, null), HasTarget);
        OpenTerminalCommand = new RelayCommand(p => ShellAction(p, ShellHelper.TryOpenTerminal, null), HasTarget);
        CopyPathCommand = new RelayCommand(p => ShellAction(p, ShellHelper.TryCopyPath, "Yol panoya kopyalandı."), HasTarget);

        ToggleThemeCommand = new RelayCommand(_ =>
        {
            ThemeService.Current.Toggle();
            OnPropertyChanged(nameof(IsDarkTheme));
        });

        TreemapUpCommand = new RelayCommand(_ =>
        {
            if (TreemapRoot?.Parent != null) TreemapRoot = TreemapRoot.Parent;
        }, _ => CanTreemapGoUp);
        TreemapNavigateCommand = new RelayCommand(p =>
        {
            var item = Resolve(p);
            if (item != null && !item.IsDirectory) item = item.Parent;
            if (item != null) TreemapRoot = item;
        });

        ThemeService.Current.ThemeChanged += (_, _) => OnPropertyChanged(nameof(IsDarkTheme));
        _scanEngine.ProgressChanged += OnScanProgress;

        LoadLastScannedPath();
        LoadDrives();
    }

    private void LoadDrives()
    {
        Drives.Clear();
        var sysDrives = ShellHelper.GetSystemDrives();
        foreach (var d in sysDrives)
        {
            Drives.Add(d);
        }
        if (Drives.Count > 0)
        {
            SelectedDrive = Drives[0];
        }
    }

    // ---------------------------------------------------------------- gezinme / hata

    private void Navigate(object? param)
    {
        switch (param)
        {
            case ActiveView v:
                ActiveView = v;
                break;
            case int i when i >= 1 && i <= 5:
                ActiveView = (ActiveView)(i - 1);
                break;
            case string s:
                if (int.TryParse(s, out var n) && n >= 1 && n <= 5) ActiveView = (ActiveView)(n - 1);
                else if (Enum.TryParse<ActiveView>(s, true, out var parsed)) ActiveView = parsed;
                break;
        }
    }

    private void SetError(string message)
    {
        ErrorMessage = message;
    }

    private void DismissError()
    {
        ErrorMessage = string.Empty;
        if (ViewState == ViewState.Error)
        {
            ViewState = RootItem != null ? ViewState.Results : ViewState.Empty;
        }
    }

    // ---------------------------------------------------------------- tarama

    private void OnScanProgress(ScanProgress p)
    {
        _ui.Post(() =>
        {
            if (!IsScanning) return;
            CurrentScanPath = p.CurrentPath;
            ScannedFiles = p.FilesScanned;
            ScannedFolders = p.FoldersScanned;
            ScannedBytes = p.BytesScanned;
            ElapsedScanTime = p.Elapsed.ToString(@"hh\:mm\:ss\.f");
            ScanSpeed = $"{p.FilesPerSecond:N0} dosya/sn";

            if (RootItem != null)
            {
                RootItem.Size = p.BytesScanned;
                RootItem.AllocatedSize = p.BytesScanned;
                RootItem.FileCount = p.FilesScanned;
                RootItem.FolderCount = p.FoldersScanned;
                RootItem.NotifySizesChanged();
            }

            RaiseSummary();
        });
    }

    private void ResetCounters()
    {
        CurrentScanPath = string.Empty;
        ScannedFiles = 0;
        ScannedFolders = 0;
        ScannedBytes = 0;
        ElapsedScanTime = "00:00:00";
        ScanSpeed = "0 dosya/sn";
    }

    public async Task StartScanAsync()
    {
        if (IsScanning || _isDeleting) return;

        if (string.IsNullOrWhiteSpace(SelectedPath) || !Directory.Exists(SelectedPath))
        {
            SetError("Geçerli bir klasör ya da sürücü yolu seçin.");
            return;
        }

        ErrorMessage = string.Empty;
        string scanPath = SelectedPath;
        SearchText = string.Empty;
        ResetCounters();
        ClearResults();

        var dirInfo = new DirectoryInfo(scanPath);
        RootItem = new FsItem
        {
            Name = string.IsNullOrEmpty(dirInfo.Name) ? scanPath : dirInfo.Name,
            FullPath = scanPath,
            IsDirectory = true,
            LastModified = dirInfo.Exists ? dirInfo.LastWriteTime : DateTime.Now,
            Attributes = dirInfo.Exists ? dirInfo.Attributes : FileAttributes.Directory,
            IsExpanded = true
        };
        RootCollection.Clear();
        RootCollection.Add(RootItem);

        ViewState = ViewState.Results;
        IsScanning = true;
        StatusText = $"Taranıyor: {scanPath}...";
        _cts = new CancellationTokenSource();

        try
        {
            var result = await _scanEngine.ScanAsync(scanPath, _cts.Token);

            ScannedFiles = result.TotalFiles;
            ScannedFolders = result.TotalFolders;
            ScannedBytes = result.TotalSize;
            _lastScanDuration = result.Elapsed;

            RootItem = result.Root;
            if (RootItem != null)
            {
                RootItem.IsExpanded = true;
            }

            ReplaceCollection(Extensions, result.Extensions);
            ReplaceCollection(TopFiles, result.TopFiles);
            ReplaceCollection(AgeDistribution, result.AgeDistribution);

            ViewState = ViewState.Results;
            RaiseSummary();

            if (result.WasCancelled)
            {
                StatusText = $"Tarama iptal edildi: {result.Elapsed.TotalSeconds:F2} sn ({result.TotalFiles:N0} dosya, {SizeFormatter.FormatBytes(result.TotalSize, CurrentUnitMode)} tarandı)";
            }
            else
            {
                StatusText = $"Tarama tamamlandı: {result.Elapsed.TotalSeconds:F2} sn ({result.TotalFiles:N0} dosya, {result.TotalFolders:N0} klasör, {SizeFormatter.FormatBytes(result.TotalSize, CurrentUnitMode)})";
                LastScannedPath = scanPath;
                SaveLastScannedPath(scanPath);
            }
            RefreshDriveInfo();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Tarama kullanıcı tarafından iptal edildi.";
            ViewState = RootItem != null ? ViewState.Results : ViewState.Empty;
        }
        catch (Exception ex)
        {
            StatusText = $"Tarama başarısız: {ex.Message}";
            SetError($"Tarama sırasında hata oluştu: {ex.Message}");
            ViewState = RootItem != null ? ViewState.Results : ViewState.Error;
        }
        finally
        {
            IsScanning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    public void StopScan()
    {
        if (_cts != null && !_cts.IsCancellationRequested)
        {
            _cts.Cancel();
            StatusText = "Tarama durduruluyor...";
        }
    }

    public void BrowseFolder()
    {
        var picked = _dialogs.PickFolder(SelectedPath);
        if (!string.IsNullOrEmpty(picked))
        {
            SelectedPath = picked;
        }
    }

    private void ClearResults()
    {
        SelectedItem = null;
        RootItem = null;
        Extensions.Clear();
        TopFiles.Clear();
        AgeDistribution.Clear();
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }

    // ---------------------------------------------------------------- silme

    private FsItem? Resolve(object? param) => param switch
    {
        FsItem f => f,
        TopFileItem t => TreeMaintenance.FindByPath(RootItem, t.FullPath),
        null => SelectedItem,
        _ => null
    };

    private bool HasTarget(object? p) => p is FsItem or TopFileItem || (p == null && SelectedItem != null);

    private bool CanActOnItem(object? p) => !IsScanning && !_isDeleting && HasTarget(p);

    private async Task DeleteAsync(object? param, bool permanent)
    {
        if (IsScanning || _isDeleting) return;
        var item = Resolve(param);
        if (item == null) return;

        string title = permanent ? "Kalıcı silme" : "Geri dönüşüm kutusuna taşı";
        string message = permanent
            ? $"'{item.Name}' kalıcı olarak silinecek.\n\nBu işlem geri alınamaz. Devam edilsin mi?"
            : $"'{item.Name}' geri dönüşüm kutusuna taşınsın mı?";
        if (!_dialogs.Confirm(title, message, permanent)) return;

        _isDeleting = true;
        OnPropertyChanged(nameof(CanScan));
        string path = item.FullPath;
        string name = item.Name;
        long freed = item.Size;
        bool ok;
        string? error;
        try
        {
            (ok, error) = await Task.Run<(bool, string?)>(() =>
            {
                if (permanent)
                {
                    bool r = ShellHelper.TryPermanentDelete(path, out var e);
                    return (r, e);
                }
                bool moved = ShellHelper.MoveToRecycleBin(path);
                return (moved, moved ? null : "Geri dönüşüm kutusuna taşınamadı (dosya kullanımda ya da izin yok olabilir).");
            });
        }
        catch (Exception ex)
        {
            ok = false;
            error = ex.Message;
        }
        finally
        {
            _isDeleting = false;
            OnPropertyChanged(nameof(CanScan));
        }

        if (!ok)
        {
            SetError($"'{name}' silinemedi: {error}");
            return;
        }

        ApplyRemoval(item);
        StatusText = permanent
            ? $"'{name}' kalıcı olarak silindi ({SizeFormatter.FormatBytes(freed, CurrentUnitMode)} boşaldı)."
            : $"'{name}' geri dönüşüm kutusuna taşındı ({SizeFormatter.FormatBytes(freed, CurrentUnitMode)} boşaldı).";
    }

    /// <summary>Diskte silinmis dugumu agactan cikarir ve tum turev verileri gunceller.</summary>
    private void ApplyRemoval(FsItem item)
    {
        var parent = item.Parent;
        if (parent == null)
        {
            // Kok silindi: sonuc kalmadi.
            ClearResults();
            ViewState = ViewState.Empty;
            RefreshDriveInfo();
            return;
        }

        bool selectionInside = TreeMaintenance.IsSelfOrDescendant(SelectedItem, item);
        bool treemapInside = TreeMaintenance.IsSelfOrDescendant(TreemapRoot, item);

        TreeMaintenance.RemoveFromTree(item);
        if (selectionInside)
        {
            SelectedItem = parent;
            parent.IsSelected = true;
        }
        if (treemapInside) TreemapRoot = parent;

        AfterTreeChanged(parent);
    }

    /// <summary>Yapi degisti: yuzdeler, siralama, istatistikler, ozet, filtre.</summary>
    private void AfterTreeChanged(FsItem changedParent)
    {
        if (RootItem != null)
        {
            TreeMaintenance.RecalculatePercentages(RootItem);
            TreeMaintenance.ResortChain(changedParent);
        }
        RefreshStatistics();
        RaiseSummary();
        RefreshDriveInfo();
        OnPropertyChanged(nameof(CanTreemapGoUp));
        if (IsSearchActive) ApplyFilter(SearchText);
    }

    private void RefreshStatistics()
    {
        var stats = TreeMaintenance.ComputeStatistics(RootItem, DateTime.Now);
        ReplaceCollection(Extensions, stats.Extensions);
        ReplaceCollection(TopFiles, stats.TopFiles);
        ReplaceCollection(AgeDistribution, stats.AgeDistribution);
    }

    // ---------------------------------------------------------------- klasoru yeniden tara

    private async Task RescanFolderAsync(object? param)
    {
        if (IsScanning || _isDeleting) return;
        var item = Resolve(param);
        if (item == null || !item.IsDirectory) return;

        if (!Directory.Exists(item.FullPath))
        {
            SetError($"Klasör artık yok: {item.FullPath}");
            return;
        }

        // Kok: tum agaci yeniden tarar.
        if (item.Parent == null)
        {
            SelectedPath = item.FullPath;
            await StartScanAsync();
            return;
        }

        ErrorMessage = string.Empty;
        ResetCounters();
        IsScanning = true;
        StatusText = $"Klasör yeniden taranıyor: {item.FullPath}...";
        _cts = new CancellationTokenSource();

        try
        {
            var result = await _scanEngine.ScanAsync(item.FullPath, _cts.Token);

            if (result.WasCancelled || result.Root == null)
            {
                StatusText = "Klasör yeniden tarama iptal edildi; mevcut sonuçlar korundu.";
                return;
            }

            var parent = item.Parent;
            var fresh = result.Root;
            int index = parent == null ? -1 : parent.Children.IndexOf(item);
            if (parent == null || index < 0)
            {
                StatusText = "Klasör ağaçta bulunamadı; yeniden tarama uygulanmadı.";
                return;
            }

            bool selectionInside = TreeMaintenance.IsSelfOrDescendant(SelectedItem, item);
            bool treemapInside = TreeMaintenance.IsSelfOrDescendant(TreemapRoot, item);

            fresh.Parent = parent;
            fresh.IsExpanded = item.IsExpanded;
            parent.Children[index] = fresh;

            TreeMaintenance.ApplyDeltaToAncestors(parent,
                fresh.Size - item.Size,
                fresh.AllocatedSize - item.AllocatedSize,
                fresh.FileCount - item.FileCount,
                fresh.FolderCount - item.FolderCount);
            item.Parent = null;

            if (selectionInside)
            {
                SelectedItem = fresh;
                fresh.IsSelected = true;
            }
            if (treemapInside) TreemapRoot = fresh;

            AfterTreeChanged(parent);
            StatusText = $"Klasör yeniden tarandı: {fresh.FullPath} ({fresh.FileCount:N0} dosya, {SizeFormatter.FormatBytes(fresh.Size, CurrentUnitMode)}, {result.Elapsed.TotalSeconds:F2} sn)";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Klasör yeniden tarama iptal edildi; mevcut sonuçlar korundu.";
        }
        catch (Exception ex)
        {
            StatusText = $"Yeniden tarama başarısız: {ex.Message}";
            SetError($"Klasör yeniden taranırken hata oluştu: {ex.Message}");
        }
        finally
        {
            IsScanning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    // ---------------------------------------------------------------- kabuk komutlari

    private delegate bool ShellOp(string path, out string? error);

    private void ShellAction(object? param, ShellOp op, string? successMessage)
    {
        string? path = param switch
        {
            FsItem f => f.FullPath,
            TopFileItem t => t.FullPath,
            null => SelectedItem?.FullPath,
            _ => null
        };
        if (string.IsNullOrEmpty(path)) return;

        if (!op(path, out var error))
        {
            SetError(error ?? "İşlem gerçekleştirilemedi.");
        }
        else if (successMessage != null)
        {
            StatusText = successMessage;
        }
    }

    // ---------------------------------------------------------------- disa aktarma

    private async Task ExportAsync(string kind)
    {
        var root = RootItem;
        if (root == null) return;

        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string filter = kind switch
        {
            "csv" => "CSV dosyası (*.csv)|*.csv",
            "json" => "JSON dosyası (*.json)|*.json",
            _ => "Metin raporu (*.txt)|*.txt"
        };
        string? file = _dialogs.PickSaveFile(filter, $"AmberTree_Rapor_{stamp}.{kind}");
        if (string.IsNullOrEmpty(file)) return;

        try
        {
            ScanResult? res = null;
            if (kind == "json")
            {
                res = new ScanResult
                {
                    Root = root,
                    Extensions = Extensions.ToList(),
                    TopFiles = TopFiles.ToList(),
                    AgeDistribution = AgeDistribution.ToList(),
                    TotalFiles = root.FileCount,
                    TotalFolders = root.FolderCount + 1,
                    TotalSize = root.Size,
                    TotalAllocated = root.AllocatedSize
                };
            }

            await Task.Run(() =>
            {
                switch (kind)
                {
                    case "csv": ExportService.ExportToCsv(root, file); break;
                    case "json": ExportService.ExportToJson(res!, file); break;
                    default: ExportService.ExportToTxt(root, file); break;
                }
            });

            string label = kind switch { "csv" => "CSV", "json" => "JSON", _ => "Metin" };
            StatusText = $"{label} raporu dışa aktarıldı: {file}";
        }
        catch (Exception ex)
        {
            SetError($"Rapor dışa aktarılamadı: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- filtre

    private void ApplyFilter(string query)
    {
        if (RootItem == null)
        {
            OnPropertyChanged(nameof(HasSearchResults));
            OnPropertyChanged(nameof(ShowNoSearchResults));
            return;
        }
        bool isEmpty = string.IsNullOrWhiteSpace(query);

        void FilterNode(FsItem node)
        {
            bool matches = isEmpty || node.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
            bool anyChildMatches = false;

            foreach (var child in node.Children)
            {
                FilterNode(child);
                if (!child.IsFilteredOut)
                {
                    anyChildMatches = true;
                }
            }

            node.IsFilteredOut = !matches && !anyChildMatches;
            if (anyChildMatches && !isEmpty)
            {
                node.IsExpanded = true;
            }
        }

        FilterNode(RootItem);
        OnPropertyChanged(nameof(HasSearchResults));
        OnPropertyChanged(nameof(ShowNoSearchResults));
    }

    // ---------------------------------------------------------------- birim / ozet yenileme

    private void RefreshUnitDisplays()
    {
        OnPropertyChanged(nameof(ScannedBytesDisplay));
        RaiseSummary();

        if (RootItem != null)
        {
            var stack = new Stack<FsItem>();
            stack.Push(RootItem);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                n.NotifyUnitChanged();
                foreach (var c in n.Children) stack.Push(c);
            }
        }
        foreach (var e in Extensions) e.NotifyUnitChanged();
        foreach (var t in TopFiles) t.NotifyUnitChanged();
        foreach (var a in AgeDistribution) a.NotifyUnitChanged();

        UnitChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseSummary()
    {
        OnPropertyChanged(nameof(TotalSizeDisplay));
        OnPropertyChanged(nameof(TotalAllocatedDisplay));
        OnPropertyChanged(nameof(TotalFilesDisplay));
        OnPropertyChanged(nameof(TotalFoldersDisplay));
        OnPropertyChanged(nameof(ScanDurationDisplay));
        RaiseDriveSummary();
    }

    private void RaiseDriveSummary()
    {
        OnPropertyChanged(nameof(DriveUsedPercent));
        OnPropertyChanged(nameof(DriveTotalDisplay));
        OnPropertyChanged(nameof(DriveFreeDisplay));
        OnPropertyChanged(nameof(DriveUsedDisplay));
    }

    private DriveInfoModel? ResolveSummaryDrive()
    {
        string basis = RootItem?.FullPath ?? SelectedPath;
        string? root = null;
        try { root = Path.GetPathRoot(basis); } catch { }
        if (!string.IsNullOrEmpty(root))
        {
            var match = Drives.FirstOrDefault(d => string.Equals(d.RootDirectory, root, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }
        return SelectedDrive;
    }

    /// <summary>Tarama/silme sonrasi surucu doluluk bilgisini diskten tazeler.</summary>
    private void RefreshDriveInfo()
    {
        try
        {
            var drive = ResolveSummaryDrive();
            if (drive != null)
            {
                var di = new DriveInfo(drive.RootDirectory);
                if (di.IsReady)
                {
                    drive.TotalSize = di.TotalSize;
                    drive.AvailableFreeSpace = di.AvailableFreeSpace;
                }
            }
        }
        catch { }
        RaiseDriveSummary();
    }

    private void RebuildBreadcrumb()
    {
        TreemapBreadcrumb.Clear();
        var chain = new List<FsItem>();
        for (var n = _treemapRoot; n != null; n = n.Parent) chain.Add(n);
        chain.Reverse();
        foreach (var n in chain) TreemapBreadcrumb.Add(n);
    }

    // ---------------------------------------------------------------- son taranan yol

    private static string LastPathFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AmberTree", "last_scan.txt");

    private void LoadLastScannedPath()
    {
        try
        {
            if (File.Exists(LastPathFile))
            {
                LastScannedPath = File.ReadAllText(LastPathFile).Trim();
            }
        }
        catch { }
    }

    private static void SaveLastScannedPath(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LastPathFile)!);
            File.WriteAllText(LastPathFile, path);
        }
        catch { }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _canExecute;

    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
