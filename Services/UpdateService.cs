using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using AmberTree.Models;
using AmberTree.ViewModels;
using TSuite.Update;

namespace AmberTree.Services;

/// <summary>
/// T-Update (başsız): UpdateController durumunu XAML afişine (Views/UpdateBanner) bağlar. Açılıştan 30 sn sonra arka planda kontrol eder.
/// Genel anahtar: t-desktree.pub (özel anahtar repo dışında: C:\Users\hp\.t-suite\keys\).
/// </summary>
public sealed class UpdateBannerViewModel : ObservableBase
{
    public const string AppId = "t-desktree";
    private const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEsirqrdVnwpwXrsj2OoSvkPIgI22JUy3scQZotzyPudfa7hqnZ0xcN9ElK2zZoFjsIfwFi97doJ1cMpe9jic4wQ==";

    public static string CurrentVersion { get; } =
        typeof(UpdateBannerViewModel).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.1.0";

    public static UpdateBannerViewModel Instance { get; } = new();

    private UpdateController? _ctl;
    private bool _visible, _downloading, _notesOpen, _failed;
    private string _title = "", _notes = "", _error = "";
    private double _progress;

    public UpdateBannerViewModel()
    {
        UpdateCommand = new RelayCommand(_ => _ = StartAsync(), _ => !Downloading && _ctl?.Info != null);
        LaterCommand = new RelayCommand(_ => { _ctl?.Later(); Hide(); });
        SkipCommand = new RelayCommand(_ => { _ctl?.Skip(); Hide(); });
        ToggleNotesCommand = new RelayCommand(_ => NotesOpen = !NotesOpen);
    }

    public ICommand UpdateCommand { get; }
    public ICommand LaterCommand { get; }
    public ICommand SkipCommand { get; }
    public ICommand ToggleNotesCommand { get; }

    public bool IsBannerVisible { get => _visible; private set { if (_visible != value) { _visible = value; Raise(nameof(IsBannerVisible)); } } }
    public bool Downloading { get => _downloading; private set { if (_downloading != value) { _downloading = value; Raise(nameof(Downloading)); Raise(nameof(NotDownloading)); } } }
    public bool NotDownloading => !_downloading;
    public bool NotesOpen { get => _notesOpen; set { if (_notesOpen != value) { _notesOpen = value; Raise(nameof(NotesOpen)); } } }
    public bool Failed { get => _failed; private set { if (_failed != value) { _failed = value; Raise(nameof(Failed)); } } }
    public string Title { get => _title; private set { _title = value; Raise(nameof(Title)); } }
    public string Notes { get => _notes; private set { _notes = value; Raise(nameof(Notes)); Raise(nameof(HasNotes)); } }
    public bool HasNotes => !string.IsNullOrWhiteSpace(_notes);
    public string Error { get => _error; private set { _error = value; Raise(nameof(Error)); } }
    public double Progress { get => _progress; private set { _progress = value; Raise(nameof(Progress)); Raise(nameof(ProgressText)); } }
    public string ProgressText => $"%{(int)Math.Round(_progress)}";

    /// <summary>Açılıştan <paramref name="delay"/> sonra (varsayılan 30 sn) arka planda sürüm kontrolü.</summary>
    public void ScheduleCheck(TimeSpan? delay = null, Action<UpdateInfo>? onAvailable = null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay ?? TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                var checker = new UpdateChecker(new UpdateOptions { AppId = AppId, CurrentVersion = CurrentVersion, PublicKeyBase64 = PublicKey });
                var ctl = new UpdateController(checker);
                if (!await ctl.CheckAsync().ConfigureAwait(false) || ctl.Info == null) return;
                onAvailable?.Invoke(ctl.Info);
                ctl.ProgressChanged += p => OnUi(() => Progress = p * 100.0);
                ctl.StateChanged += () => OnUi(() => OnState(ctl));
                ctl.ExitRequested += () => OnUi(() => Application.Current.Shutdown());
                OnUi(() => Show(ctl));
            }
            catch { /* ağ yoksa sessizce geç */ }
        });
    }

    private void Show(UpdateController ctl)
    {
        _ctl = ctl;
        Title = $"Yeni sürüm {ctl.Info!.NewVersion} hazır";
        Notes = ctl.Info.Notes ?? "";
        Error = "";
        Failed = false;
        Downloading = false;
        Progress = 0;
        NotesOpen = false;
        IsBannerVisible = true;
        CommandManager.InvalidateRequerySuggested();
    }

    private void Hide() => IsBannerVisible = false;

    private async Task StartAsync()
    {
        if (_ctl == null) return;
        Failed = false;
        Error = "";
        await _ctl.StartUpdateAsync().ConfigureAwait(false);
    }

    private void OnState(UpdateController ctl)
    {
        Downloading = ctl.State is UpdateState.Downloading or UpdateState.Launching;
        if (ctl.State == UpdateState.Failed)
        {
            Failed = true;
            Error = ctl.Error ?? "Güncelleme indirilemedi";
        }
        CommandManager.InvalidateRequerySuggested();
    }

    private static void OnUi(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null) return;
        if (d.CheckAccess()) a(); else d.BeginInvoke(a);
    }
}
