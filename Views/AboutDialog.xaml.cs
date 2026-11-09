using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using Microsoft.Win32;

namespace AmberTree.Views;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        DataContext = AmberTree.Services.UpdateBannerViewModel.Instance;
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            StartWithWindowsCheckBox.IsChecked = key?.GetValue("T-DeskTree") is string;

        if (Application.Current?.MainWindow != null && Application.Current.MainWindow.IsVisible)
        {
            Owner = Application.Current.MainWindow;
        }
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
