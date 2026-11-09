using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AmberTree.ViewModels;
using TSuite.Link;

namespace AmberTree.Services;

/// <summary>Disk doluluk uyarısı kuralı (saf, test edilebilir): boş alan %10'un VEYA 10 GB'ın altındaysa uyar; sürücü başına 6 saat bekleme.</summary>
public static class DiskAlert
{
    public const long LowBytes = 10L * 1024 * 1024 * 1024;
    public const double LowPct = 10.0;
    public static readonly TimeSpan Debounce = TimeSpan.FromHours(6);

    public static double FreePct(long free, long total) => total > 0 ? free * 100.0 / total : 100.0;

    public static bool IsLow(long free, long total) => total > 0 && (FreePct(free, total) < LowPct || free < LowBytes);

    /// <summary>Uyarı verilmeli mi? Verilirse zamanı kaydeder.</summary>
    public static bool ShouldRaise(IDictionary<string, DateTime> last, string drive, long free, long total, DateTime now)
    {
        if (!IsLow(free, total)) return false;
        if (last.TryGetValue(drive, out var t) && now - t < Debounce) return false;
        last[drive] = now;
        return true;
    }
}

/// <summary>
/// T-Link (T-Suite): sunucu kimliği "t-desktree". Olaylar: scan.finished, disk.low, app.update.available.
/// Komutlar: disk.status, scan.start, app.show, app.version. Manifest açılışta yazılır, çıkışta silinir.
/// </summary>
public sealed class AppLink : IAsyncDisposable
{
    public const string AppId = "t-desktree";
    private static readonly string[] Events = ["scan.finished", "disk.low", "app.update.available"];
    private static readonly string[] Commands = ["disk.status", "scan.start", "app.show", "app.version", .. TSuite.Update.UpdateLink.Commands];

    private readonly Window _window;
    private readonly MainWindowViewModel _vm;
    private readonly AppRegistry _registry = new();
    private readonly Dictionary<string, DateTime> _lastAlert = new(StringComparer.OrdinalIgnoreCase);
    private LinkServer? _server;
    private Timer? _timer;

    public AppLink(Window window, MainWindowViewModel vm)
    {
        _window = window;
        _vm = vm;
    }

    public void Start()
    {
        try
        {
            var v = UpdateBannerViewModel.CurrentVersion;
            var srv = new LinkServer(AppId, v, Events, Commands);
            srv.OnCommand("disk.status", (d, _) => Task.FromResult<JsonElement?>(DiskStatus(Str(d, "drive"))));
            srv.OnCommand("scan.start", (d, _) => ScanStartAsync(Str(d, "path")));
            srv.OnCommand("app.show", (_, _) =>
            {
                Ui(ShowWindow);
                return Task.FromResult<JsonElement?>(LinkJson.Obj());
            });
            srv.OnCommand("app.version", (_, _) =>
                Task.FromResult<JsonElement?>(LinkJson.Obj(("version", v), ("protocol", LinkProtocol.Version))));
            TSuite.Update.UpdateLink.Register(srv, UpdateBannerViewModel.Instance.Controller);   // app.update.check / app.update.install
            srv.Start();
            _server = srv;

            _vm.ScanFinished += OnScanFinished;
            // Hafif sürücü izleyicisi: ilk kontrol 30 sn sonra, sonra 5 dakikada bir (yalnızca uygulama çalışırken).
            _timer = new Timer(_ => CheckDisks(), null, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5));

            _registry.CleanupStale();
            _registry.Write(new AppManifest
            {
                Id = AppId, Name = "T-DeskTree", Version = v, ExePath = Environment.ProcessPath ?? "", Pid = Environment.ProcessId,
                Events = Events, Commands = Commands, ReleasesRepo = "taha-sa/t-desktree-releases", Icon = Environment.ProcessPath,
            });
        }
        catch { /* T-Link isteğe bağlı: hata uygulamayı etkilemez */ }
    }

    public void PublishUpdateAvailable(TSuite.Update.UpdateInfo info) =>
        _server?.Publish("app.update.available", LinkJson.Obj(("version", info.NewVersion), ("notes", info.Notes)));

    private void OnScanFinished(object? sender, ScanFinishedEventArgs e) =>
        _server?.Publish("scan.finished", LinkJson.Obj(("root", e.Root), ("totalBytes", e.TotalBytes), ("durationMs", (long)e.Duration.TotalMilliseconds)));

    private void CheckDisks()
    {
        try
        {
            if (_server == null) return;
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                long free = d.AvailableFreeSpace, total = d.TotalSize;
                if (!DiskAlert.ShouldRaise(_lastAlert, d.Name, free, total, DateTime.UtcNow)) continue;
                _server.Publish("disk.low", LinkJson.Obj(("drive", d.Name), ("freeBytes", free), ("totalBytes", total), ("freePct", DiskAlert.FreePct(free, total))));
            }
        }
        catch { /* sürücü okunamazsa bir sonraki turda */ }
    }

    private static JsonElement DiskStatus(string? only)
    {
        var rows = new List<object>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                if (!string.IsNullOrEmpty(only) && !d.Name.StartsWith(only.TrimEnd('\\', '/', ':') , StringComparison.OrdinalIgnoreCase)) continue;
                rows.Add(new { drive = d.Name, freeBytes = d.AvailableFreeSpace, totalBytes = d.TotalSize, freePct = DiskAlert.FreePct(d.AvailableFreeSpace, d.TotalSize) });
            }
            catch { }
        }
        return JsonSerializer.SerializeToElement(new { drives = rows });
    }

    private async Task<JsonElement?> ScanStartAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) throw new ArgumentException("path geçerli bir klasör olmalı");
        var tcs = new TaskCompletionSource<bool>();
        Ui(() =>
        {
            ShowWindow();
            if (_vm.IsScanning) { tcs.SetResult(false); return; }
            _vm.SelectedPath = path; // mevcut tarama komutu: ViewModel kendi iş parçacığı kurallarıyla başlatır
            _ = _vm.StartScanAsync();
            tcs.SetResult(true);
        });
        return LinkJson.Obj(("started", await tcs.Task.ConfigureAwait(false)));
    }

    private void ShowWindow()
    {
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private static void Ui(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

    private static string? Str(JsonElement? d, string prop) =>
        d is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public async ValueTask DisposeAsync()
    {
        _vm.ScanFinished -= OnScanFinished;
        _timer?.Dispose();
        try { _registry.Remove(AppId); } catch { }
        if (_server != null) { try { await _server.DisposeAsync().ConfigureAwait(false); } catch { } }
    }
}
