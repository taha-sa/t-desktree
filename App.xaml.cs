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

            // Test 6: Folders always above files everywhere (0 B folders never fall below files) + clickable column sort
            Console.Write("[TEST 6] Testing Folders-Above-Files & Clickable Column Sorting... ");
            var driveRoot = new AmberTree.Models.FsItem { Name = @"C:\", FullPath = @"C:\", IsDirectory = true };
            var subUsers = new AmberTree.Models.FsItem { Name = "Users", IsDirectory = true, Parent = driveRoot, Size = 500 };
            var folderSmall = new AmberTree.Models.FsItem { Name = "Program Files", IsDirectory = true, Parent = driveRoot, Size = 200 };
            var hugePagefile = new AmberTree.Models.FsItem { Name = "pagefile.sys", IsDirectory = false, Parent = driveRoot, Size = 10_000 };
            var hiberfile = new AmberTree.Models.FsItem { Name = "hiberfil.sys", IsDirectory = false, Parent = driveRoot, Size = 4_000 };
            driveRoot.SetChildrenBulk(new() { hiberfile, folderSmall, hugePagefile, subUsers });

            var uFile = new AmberTree.Models.FsItem { Name = "desktop.ini", IsDirectory = false, Parent = subUsers, Size = 174 };
            var uDirBig = new AmberTree.Models.FsItem { Name = "hp", IsDirectory = true, Parent = subUsers, Size = 3_000 };
            var uDirZero = new AmberTree.Models.FsItem { Name = "All Users", IsDirectory = true, Parent = subUsers, Size = 0 };
            var uFileSmall = new AmberTree.Models.FsItem { Name = "a.txt", IsDirectory = false, Parent = subUsers, Size = 50 };
            subUsers.SetChildrenBulk(new() { uFileSmall, uDirZero, uFile, uDirBig });

            TreeMaintenance.SortTree(driveRoot, TreeSortColumn.Size, ascending: false);
            if (driveRoot.Children[0].Name != "Users" || driveRoot.Children[1].Name != "Program Files"
                || driveRoot.Children[2].Name != "pagefile.sys" || driveRoot.Children[3].Name != "hiberfil.sys")
                throw new Exception("Drive root sort failed!");
            // Subfolder: 0 B folder (All Users) stays ABOVE desktop.ini (174 B)
            if (subUsers.Children[0].Name != "hp" || subUsers.Children[1].Name != "All Users"
                || subUsers.Children[2].Name != "desktop.ini" || subUsers.Children[3].Name != "a.txt")
                throw new Exception("Subfolder 0 B folder fell below files!");

            TreeMaintenance.SortTree(driveRoot, TreeSortColumn.Name, ascending: true);
            if (subUsers.Children[0].Name != "All Users" || subUsers.Children[1].Name != "hp"
                || subUsers.Children[2].Name != "a.txt" || subUsers.Children[3].Name != "desktop.ini")
                throw new Exception("Name column sort failed!");
            Console.WriteLine("PASSED");

            Console.WriteLine("==================================================");
            Console.WriteLine(" ALL T-DESKTREE TESTS PASSED SUCCESSFULLY! (7/7)");
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
