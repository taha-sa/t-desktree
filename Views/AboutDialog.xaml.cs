using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using Microsoft.Win32;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using TSuite.Link;

namespace AmberTree.Views;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height;
        MaxWidth = SystemParameters.WorkArea.Width;
        VersionText.Text = $"v{Services.UpdateBannerViewModel.CurrentVersion} · {RuntimeInformation.ProcessArchitecture}";
        FamilyList.ItemsSource = LoadFamily();
        AutoReportsCheckBox.IsChecked = TSuite.Diagnostics.CrashReporter.AutomaticallyPrepareReports;
        DataContext = AmberTree.Services.UpdateBannerViewModel.Instance;
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            StartWithWindowsCheckBox.IsChecked = key?.GetValue("T-DeskTree") is string;

        if (Application.Current?.MainWindow != null && Application.Current.MainWindow.IsVisible)
        {
            Owner = Application.Current.MainWindow;
        }
    }

    private sealed record FamilyApp(string Id, string Name, string Status, bool Installed)
    {
        public string Icon => $"/T-DeskTree;component/Assets/Family/{Id}.png";
        public string DownloadUrl => $"https://github.com/taha-sa/{Id}-releases/releases/latest";
        public string DownloadLabel => $"{Name} edinin";
        public Visibility DownloadVisibility => Installed ? Visibility.Collapsed : Visibility.Visible;
    }

    private static FamilyApp[] LoadFamily()
    {
        var registry = new AppRegistry();
        return new[] {
            ("t-bridge", "T-Bridge"), ("t-downloader", "T-Downloader"),
            ("t-ramcash", "T-RamCash"), ("t-desktree", "T-DeskTree"),
            ("t-desk-viewer", "T-Desk Viewer")
        }.Select(app =>
        {
            bool current = app.Item1 == Services.AppLink.AppId;
            var manifest = registry.Read(app.Item1);
            bool installed = current || (manifest != null && File.Exists(manifest.ExePath));
            string version = current ? Services.UpdateBannerViewModel.CurrentVersion : manifest?.Version ?? "";
            string status = current ? "Bu uygulama" : installed ? "Yüklü" : "Yüklü değil";
            if (installed && !string.IsNullOrWhiteSpace(version)) status += " · " + version;
            return new FamilyApp(app.Item1, app.Item2, status, installed);
        }).ToArray();
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText($"T-DeskTree {Services.UpdateBannerViewModel.CurrentVersion}\n" +
                $"{RuntimeInformation.OSDescription}\nOS: {RuntimeInformation.OSArchitecture}; App: {RuntimeInformation.ProcessArchitecture}");
        }
        catch (Exception ex) { TSuite.Diagnostics.Log.Error("Could not copy diagnostics", ex); }
    }

    private void StartWithWindows_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (StartWithWindowsCheckBox.IsChecked == true)
                key.SetValue("T-DeskTree", $"\"{Environment.ProcessPath}\" --background");
            else
                key.DeleteValue("T-DeskTree", false);
        }
        catch (Exception ex)
        {
            StartWithWindowsCheckBox.IsChecked = StartWithWindowsCheckBox.IsChecked != true;
            MessageBox.Show($"Ba?lang?? ayar? kaydedilemedi: {ex.Message}", "T-DeskTree", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AutoReports_Click(object sender, RoutedEventArgs e)
    {
        try { TSuite.Diagnostics.CrashReporter.AutomaticallyPrepareReports = AutoReportsCheckBox.IsChecked == true; }
        catch (Exception ex)
        {
            TSuite.Diagnostics.Log.Error("Could not save crash reporting preference", ex);
            AutoReportsCheckBox.IsChecked = TSuite.Diagnostics.CrashReporter.AutomaticallyPrepareReports;
            MessageBox.Show("Ayar kaydedilemedi: " + ex.Message, "T-DeskTree", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Report_Click(object sender, RoutedEventArgs e)
    {
        new CrashPreviewDialog(CrashPreviewDialog.CreateManualReport(), false) { Owner = this }.ShowDialog();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch { }
        e.Handled = true;
    }
}
