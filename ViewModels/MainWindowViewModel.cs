using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using AmberTree.Converters;
using AmberTree.Models;
using AmberTree.Services;
using Microsoft.Win32;

namespace AmberTree.ViewModels;

public class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly ScanEngine _scanEngine = new();
    private CancellationTokenSource? _cts;

    private string _selectedPath = "C:\\";
    private DriveInfoModel? _selectedDrive;
    private bool _isScanning;
    private string _statusText = "Ready. Select a drive or folder and click Scan.";
    private string _currentScanPath = string.Empty;
    private long _scannedFiles;
    private long _scannedFolders;
    private long _scannedBytes;
    private string _elapsedScanTime = "00:00:00";
    private string _scanSpeed = "0 files/s";
    private FsItem? _rootItem;
    private FsItem? _selectedItem;
    private SizeUnitMode _currentUnitMode = SizeUnitMode.Auto;
    private string _searchText = string.Empty;

    public ObservableCollection<DriveInfoModel> Drives { get; } = new();
    public ObservableCollection<FsItem> RootCollection { get; } = new();
    public ObservableCollection<ExtensionStat> Extensions { get; } = new();
    public ObservableCollection<TopFileItem> TopFiles { get; } = new();
    public ObservableCollection<AgeDistributionItem> AgeDistribution { get; } = new();

    public string SelectedPath
    {
        get => _selectedPath;
        set
        {
            if (_selectedPath != value)
            {
                _selectedPath = value;
                OnPropertyChanged();
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

    public bool CanScan => !IsScanning && !string.IsNullOrWhiteSpace(SelectedPath);
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
                OnPropertyChanged(nameof(ScannedBytesDisplay));
                RefreshBindings();
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText != value)
            {
                _searchText = value;
                OnPropertyChanged();
                ApplyFilter(value);
            }
        }
    }

    public ICommand StartScanCommand { get; }
    public ICommand StopScanCommand { get; }
    public ICommand BrowseFolderCommand { get; }
    public ICommand ExportCsvCommand { get; }
    public ICommand ExportJsonCommand { get; }
    public ICommand ExportTxtCommand { get; }
    public ICommand SetUnitModeCommand { get; }

    public MainWindowViewModel()
    {
        StartScanCommand = new RelayCommand(async _ => await StartScanAsync(), _ => CanScan);
        StopScanCommand = new RelayCommand(_ => StopScan(), _ => CanStop);
        BrowseFolderCommand = new RelayCommand(_ => BrowseFolder());
        ExportCsvCommand = new RelayCommand(_ => ExportCsv(), _ => RootItem != null);
        ExportJsonCommand = new RelayCommand(_ => ExportJson(), _ => RootItem != null);
        ExportTxtCommand = new RelayCommand(_ => ExportTxt(), _ => RootItem != null);
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

        _scanEngine.ProgressChanged += OnScanProgress;

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

    private void OnScanProgress(ScanProgress p)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            CurrentScanPath = p.CurrentPath;
            ScannedFiles = p.FilesScanned;
            ScannedFolders = p.FoldersScanned;
            ScannedBytes = p.BytesScanned;
            ElapsedScanTime = p.Elapsed.ToString(@"hh\:mm\:ss\.f");
            ScanSpeed = $"{p.FilesPerSecond:N0} files/s";
        });
    }

    public async Task StartScanAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedPath) || !Directory.Exists(SelectedPath))
        {
            MessageBox.Show("Please select a valid folder or drive path.", "AmberTree", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsScanning = true;
        StatusText = $"Scanning: {SelectedPath}...";
        _cts = new CancellationTokenSource();

        try
        {
            var result = await _scanEngine.ScanAsync(SelectedPath, _cts.Token);

            RootItem = result.Root;
            if (RootItem != null)
            {
                RootItem.IsExpanded = true;
            }

            Extensions.Clear();
            foreach (var ext in result.Extensions)
            {
                Extensions.Add(ext);
            }

            TopFiles.Clear();
            foreach (var top in result.TopFiles)
            {
                TopFiles.Add(top);
            }

            AgeDistribution.Clear();
            foreach (var age in result.AgeDistribution)
            {
                AgeDistribution.Add(age);
            }

            StatusText = result.WasCancelled
                ? $"Scan cancelled after {result.Elapsed.TotalSeconds:F2}s ({result.TotalFiles:N0} files, {SizeFormatter.FormatBytes(result.TotalSize, CurrentUnitMode)} scanned)"
                : $"Scan completed in {result.Elapsed.TotalSeconds:F2}s ({result.TotalFiles:N0} files, {result.TotalFolders:N0} folders, {SizeFormatter.FormatBytes(result.TotalSize, CurrentUnitMode)})";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan cancelled by user.";
        }
        catch (Exception ex)
        {
            StatusText = $"Scan failed: {ex.Message}";
            MessageBox.Show($"Error scanning: {ex.Message}", "AmberTree", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsScanning = false;
        }
    }

    public void StopScan()
    {
        if (_cts != null && !_cts.IsCancellationRequested)
        {
            _cts.Cancel();
            StatusText = "Cancelling scan...";
        }
    }

    public void BrowseFolder()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Select Drive or Folder to Analyze",
            InitialDirectory = Directory.Exists(SelectedPath) ? SelectedPath : "C:\\"
        };

        if (dlg.ShowDialog() == true)
        {
            SelectedPath = dlg.FolderName;
        }
    }

    private void ExportCsv()
    {
        if (RootItem == null) return;
        var sfd = new SaveFileDialog
        {
            Filter = "CSV File (*.csv)|*.csv",
            FileName = $"AmberTree_Report_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            DefaultDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };
        if (sfd.ShowDialog() == true)
        {
            ExportService.ExportToCsv(RootItem, sfd.FileName);
            MessageBox.Show($"CSV report successfully exported to:\n{sfd.FileName}", "AmberTree Export", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void ExportJson()
    {
        if (RootItem == null) return;
        var sfd = new SaveFileDialog
        {
            Filter = "JSON File (*.json)|*.json",
            FileName = $"AmberTree_Report_{DateTime.Now:yyyyMMdd_HHmmss}.json",
            DefaultDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };
        if (sfd.ShowDialog() == true)
        {
            var res = new ScanResult
            {
                Root = RootItem,
                Extensions = Extensions.ToList(),
                TopFiles = TopFiles.ToList(),
                AgeDistribution = AgeDistribution.ToList(),
                TotalFiles = ScannedFiles,
                TotalFolders = ScannedFolders,
                TotalSize = RootItem.Size,
                TotalAllocated = RootItem.AllocatedSize
            };
            ExportService.ExportToJson(res, sfd.FileName);
            MessageBox.Show($"JSON report successfully exported to:\n{sfd.FileName}", "AmberTree Export", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void ExportTxt()
    {
        if (RootItem == null) return;
        var sfd = new SaveFileDialog
        {
            Filter = "Text Report (*.txt)|*.txt",
            FileName = $"AmberTree_Report_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            DefaultDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };
        if (sfd.ShowDialog() == true)
        {
            ExportService.ExportToTxt(RootItem, sfd.FileName);
            MessageBox.Show($"Text report successfully exported to:\n{sfd.FileName}", "AmberTree Export", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void ApplyFilter(string query)
    {
        if (RootItem == null) return;
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
            if (anyChildMatches)
            {
                node.IsExpanded = true;
            }
        }

        FilterNode(RootItem);
    }

    private void RefreshBindings()
    {
        OnPropertyChanged(nameof(RootItem));
        OnPropertyChanged(nameof(Extensions));
        OnPropertyChanged(nameof(TopFiles));
        OnPropertyChanged(nameof(AgeDistribution));
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
