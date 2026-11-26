using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AmberTree.Services;
using TSuite.Diagnostics;

namespace AmberTree;

public partial class App : Application
{
    // Tek örnek kilidi: App ömrü boyunca tutulmalı (GC/kapanma erken bırakmasın).
    private Mutex? _singleInstanceMutex;
    private IDisposable? _crashHook;
    internal static bool StartInBackground { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        Localization.LanguagePreference.InitializeWpf();
        // CLI-only modlar tek-örnek kilidinden ÖNCE: çalışan örnekle yan yana çalışabilmeli.
        if (e.Args.Length > 0 && e.Args[0] == "--test")
        {
            await RunDiagnosticsAndExitAsync();
            return;
        }

        // T-Bridge "Hepsini güncelle": `--update` ile başlatılır -> kontrol, indirme, yardımcıyı başlat, çık (arayüz yok).
        if (TSuite.Update.UpdateCli.IsRequested(e.Args))
        {
            int code = 1;
            try { code = await TSuite.Update.UpdateCli.RunAsync(UpdateBannerViewModel.MakeOptions()); }
            catch (Exception ex) { Log.Error("UpdateCli failed", ex); }
            Shutdown(code);
            return;
        }

        StartInBackground = Array.Exists(e.Args, arg => string.Equals(arg, "--background", StringComparison.OrdinalIgnoreCase));

        _singleInstanceMutex = new Mutex(true, @"Local\T-DeskTree.Single", out bool created);
        if (!created)
        {
            if (StartInBackground) { Shutdown(); return; }
            // Zaten çalışan örnek var: onu öne getirmesi için sinyal ver, kendini kapat.
            try
            {
                using var ev = EventWaitHandle.OpenExisting(@"Local\T-DeskTree.Activate");
                ev.Set();
            }
            catch { /* ilk örnek henüz hazır değil: sessizce çık */ }
            Shutdown();
            return;
        }

        CrashReporter.Install("t-desktree", "T-DeskTree", typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.1.0", "taha-sa/t-desktree-releases");
        _crashHook = CrashReporter.InstallWpf(Dispatcher);
        Log.Info("Application starting");
        StartActivationListener();

        ThemeService.Current.LoadSaved();
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        try { _activateEvent?.Set(); } catch { }
        try { _activateEvent?.Dispose(); } catch { }
        _activateEvent = null;
        try { _singleInstanceMutex?.Dispose(); } catch { }
        _singleInstanceMutex = null;
        _crashHook?.Dispose();
        base.OnExit(e);
    }

    private volatile bool _exiting;
    private EventWaitHandle? _activateEvent;

    private void StartActivationListener()
    {
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\T-DeskTree.Activate");
        var ev = _activateEvent;
        var t = new Thread(() =>
        {
            try { while (!_exiting && ev.WaitOne())
            {
                var d = Dispatcher;
                if (d == null || d.HasShutdownStarted) break;
                d.BeginInvoke(new Action(() =>
                {
                    var w = MainWindow;
                    if (w == null) return;
                    if (!w.IsVisible) w.Show();
                    if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                    w.Activate();
                    w.Topmost = true; w.Topmost = false;
                    w.Focus();
                }));
            }
            } catch (ObjectDisposedException) { }
        }) { IsBackground = true, Name = "ActivateListener" };
        t.Start();
    }

    private static async Task RunDiagnosticsAndExitAsync()
    {
        Console.WriteLine("==================================================");
        Console.WriteLine(" T-DeskTree Self-Diagnostic & Verification Suite");
        Console.WriteLine("==================================================");

        try
        {
            // Test 1: Native Filesystem Cluster Detection
            Console.Write("[TEST 1] Testing NativeFileSystem.GetClusterSize... ");
            uint clusterSize = NativeFileSystem.GetClusterSize("C:\\");
            if (clusterSize == 0 || clusterSize > 65536)
            {
                throw new Exception($"Invalid cluster size detected: {clusterSize}");
            }
            Console.WriteLine($"PASSED (Cluster: {clusterSize} bytes)");

            // Test 2: Cluster Alignment Math
            Console.Write("[TEST 2] Testing Cluster Alignment... ");
            long aligned0 = NativeFileSystem.AlignToCluster(0, clusterSize);
            long aligned1 = NativeFileSystem.AlignToCluster(1, clusterSize);
            long aligned4096 = NativeFileSystem.AlignToCluster(4096, clusterSize);
            long aligned4097 = NativeFileSystem.AlignToCluster(4097, clusterSize);
            if (aligned0 != 0 || aligned1 != clusterSize || aligned4096 != clusterSize || aligned4097 != clusterSize * 2)
            {
                throw new Exception($"Cluster alignment mismatch: a0={aligned0}, a1={aligned1}, a4096={aligned4096}, a4097={aligned4097}");
            }
            Console.WriteLine("PASSED");

            // Test 3: System Drives Enumeration
            Console.Write("[TEST 3] Testing ShellHelper.GetSystemDrives... ");
            var drives = ShellHelper.GetSystemDrives();
            if (drives.Count == 0)
            {
                throw new Exception("No drives detected on system!");
            }
            Console.WriteLine($"PASSED ({drives.Count} active drives found: {string.Join(", ", drives.ConvertAll(d => d.Name))})");

            // Test 6: T-Link disk uyarı kuralı (<%10 veya <10 GB, sürücü başına 6 saat bekleme)
            Console.Write("[TEST 3b] Testing DiskAlert rules... ");
            const long gb = 1024L * 1024 * 1024;
            var last = new System.Collections.Generic.Dictionary<string, DateTime>();
            var t0 = DateTime.UtcNow;
            if (DiskAlert.IsLow(500 * gb, 1000 * gb)) throw new Exception("%50 boş uyarı vermemeli");
            if (!DiskAlert.IsLow(5 * gb, 1000 * gb)) throw new Exception("5 GB boş uyarı vermeli");
            if (!DiskAlert.IsLow(80 * gb, 1000 * gb)) throw new Exception("%8 boş uyarı vermeli");
            if (!DiskAlert.ShouldRaise(last, "C:\\", 5 * gb, 1000 * gb, t0)) throw new Exception("ilk uyarı verilmeli");
            if (DiskAlert.ShouldRaise(last, "C:\\", 5 * gb, 1000 * gb, t0.AddHours(5))) throw new Exception("6 saat içinde tekrar verilmemeli");
            if (!DiskAlert.ShouldRaise(last, "C:\\", 5 * gb, 1000 * gb, t0.AddHours(7))) throw new Exception("6 saat sonra tekrar verilmeli");
            if (!DiskAlert.ShouldRaise(last, "D:\\", 5 * gb, 1000 * gb, t0)) throw new Exception("başka sürücü bağımsız olmalı");
            Console.WriteLine("PASSED");

            // Test 4 uses a controlled fixture, never the user's documents.
            string testDir = Path.Combine(Path.GetTempPath(), "TDeskTreeScan_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testDir);
            File.WriteAllText(Path.Combine(testDir, "sample.txt"), "T-DeskTree diagnostic fixture");

            Console.Write($"[TEST 4] Testing ScanEngine on '{testDir}'... ");
            var engine = new ScanEngine();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await engine.ScanAsync(testDir, cts.Token);
            var stats = TreeMaintenance.ComputeStatistics(result.Root, DateTime.Now);
            result.Extensions = stats.Extensions;
            result.TopFiles = stats.TopFiles;
            result.AgeDistribution = stats.AgeDistribution;

            if (result.Root == null) throw new Exception("Root item was null!");
            if (result.TotalFiles == 0) throw new Exception("No files scanned in test directory!");
            if (result.TotalSize == 0) throw new Exception("Total size was 0!");
            if (result.Extensions.Count == 0) throw new Exception("No extensions recorded!");
            if (result.AgeDistribution.Count != 5) throw new Exception("Age distribution buckets count != 5!");

            Console.WriteLine($"PASSED (Scanned {result.TotalFiles} files, {result.TotalFolders} folders, {result.TotalSize:N0} bytes in {result.Elapsed.TotalMilliseconds:F0}ms)");

            // Test 5: Export Service (CSV, JSON, TXT)
            Console.Write("[TEST 5] Testing ExportService (CSV, JSON, TXT)... ");
            string tempDir = Path.Combine(Path.GetTempPath(), "AmberTreeTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            string csvPath = Path.Combine(tempDir, "report.csv");
            string jsonPath = Path.Combine(tempDir, "report.json");
            string txtPath = Path.Combine(tempDir, "report.txt");

            ExportService.ExportToCsv(result.Root, csvPath);
            ExportService.ExportToJson(result, jsonPath);
            ExportService.ExportToTxt(result.Root, txtPath);

            if (!File.Exists(csvPath) || new FileInfo(csvPath).Length == 0) throw new Exception("CSV export empty!");
            if (!File.Exists(jsonPath) || new FileInfo(jsonPath).Length == 0) throw new Exception("JSON export empty!");
            if (!File.Exists(txtPath) || new FileInfo(txtPath).Length == 0) throw new Exception("TXT export empty!");

            foreach (var fixture in new[] { testDir, tempDir })
            {
                var resolved = Path.GetFullPath(fixture);
                var expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                if (Path.GetDirectoryName(resolved) != expectedParent) throw new InvalidOperationException("Unexpected diagnostic fixture path");
                Directory.Delete(resolved, true);
            }
            Console.WriteLine("PASSED");

            Console.WriteLine("==================================================");
            Console.WriteLine(" ALL T-DESKTREE TESTS PASSED SUCCESSFULLY! (6/6)");
            Console.WriteLine("==================================================");

            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"FAILED: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            Console.ResetColor();
            Environment.Exit(1);
        }
    }
}
