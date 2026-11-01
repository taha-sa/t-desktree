using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AmberTree.Services;

namespace AmberTree;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length > 0 && e.Args[0] == "--test")
        {
            await RunDiagnosticsAndExitAsync();
            return;
        }

        base.OnStartup(e);
    }

    private static async Task RunDiagnosticsAndExitAsync()
    {
        Console.WriteLine("==================================================");
        Console.WriteLine(" AmberTree Self-Diagnostic & Verification Suite");
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

            // Test 4: Live Directory Scan with ScanEngine
            string testDir = Directory.Exists(@"C:\Users\hp\Desktop\araçlar")
                ? @"C:\Users\hp\Desktop\araçlar"
                : AppDomain.CurrentDomain.BaseDirectory;

            Console.Write($"[TEST 4] Testing ScanEngine on '{testDir}'... ");
            var engine = new ScanEngine();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await engine.ScanAsync(testDir, cts.Token);

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

            Directory.Delete(tempDir, true);
            Console.WriteLine("PASSED");

            Console.WriteLine("==================================================");
            Console.WriteLine(" ALL AMBERTREE TESTS PASSED SUCCESSFULLY! (5/5)");
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
