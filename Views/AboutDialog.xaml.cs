using AmberTree.Localization;
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
        LanguageBox.ItemsSource = new[] { Strings.SystemLanguage, "Türkçe", "English", "Deutsch" };
        LanguageBox.SelectedIndex = Array.IndexOf(LanguagePreference.Languages, LanguagePreference.Read());
        BridgeButton.Content = Services.BridgeInvitation.InstalledPath() == null ? Strings.GetBridge : Strings.OpenBridge;
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
        public string DownloadUrl => $"https://github.com/taha-sa/{(Id == "t-desk-viewer" ? "tdesk-viewer" : Id)}-releases/releases/latest";
        public string DownloadLabel => Strings.Format(Strings.GetNamedApp, Name);
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
            string status = current ? Strings.CurrentApp : installed ? Strings.Installed : Strings.NotInstalled;
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

    private void Language_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded || LanguageBox.SelectedIndex < 0) return;
        try
        {
            LanguagePreference.Save(LanguagePreference.Languages[LanguageBox.SelectedIndex]);
            MessageBox.Show(this, Strings.RestartLanguage, Strings.Language, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Strings.SettingSaveFailed + ex.Message, "T-DeskTree", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Bridge_Click(object sender, RoutedEventArgs e)
    {
        Services.BridgeInvitation.Show(this);
        BridgeButton.Content = Services.BridgeInvitation.InstalledPath() == null ? Strings.GetBridge : Strings.OpenBridge;
    }

    private void StartWithWindows_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (StartWithWindowsCheckBox.IsChecked == true)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exe))
                {
                    StartWithWindowsCheckBox.IsChecked = false;
                    return;
                }
                key.SetValue("T-DeskTree", $"\"{exe}\" --background");
            }
            else
                key.DeleteValue("T-DeskTree", false);
        }
        catch (Exception ex)
        {
            StartWithWindowsCheckBox.IsChecked = StartWithWindowsCheckBox.IsChecked != true;
            MessageBox.Show(Strings.Format(Strings.StartupSaveFailed, ex.Message), "T-DeskTree", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AutoReports_Click(object sender, RoutedEventArgs e)
    {
        try { TSuite.Diagnostics.CrashReporter.AutomaticallyPrepareReports = AutoReportsCheckBox.IsChecked == true; }
        catch (Exception ex)
        {
            TSuite.Diagnostics.Log.Error("Could not save crash reporting preference", ex);
            AutoReportsCheckBox.IsChecked = TSuite.Diagnostics.CrashReporter.AutomaticallyPrepareReports;
            MessageBox.Show(Strings.SettingSaveFailed + ex.Message, "T-DeskTree", MessageBoxButton.OK, MessageBoxImage.Warning);
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

    private void Legal_Click(object sender, RoutedEventArgs e) => new LegalDialog { Owner = this }.ShowDialog();

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            if (e.Uri is { IsAbsoluteUri: true } &&
                (e.Uri.Scheme == Uri.UriSchemeHttps || e.Uri.Scheme == Uri.UriSchemeHttp))
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            }
        }
        catch { }
        e.Handled = true;
    }
}
