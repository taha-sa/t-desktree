using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using AmberTree.Localization;
using TSuite.Link;

namespace AmberTree.Services;

internal static class BridgeInvitation
{
    internal static string? InstalledPath()
    {
        var registered = new AppRegistry().Read("t-bridge");
        if (registered != null && File.Exists(registered.ExePath)) return registered.ExePath;
        foreach (var folder in new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        foreach (var relative in new[] { @"T-Bridge\t-bridge.exe", @"Programs\T-Bridge\t-bridge.exe", @"T-Suite\T-Bridge\t-bridge.exe" })
        {
            var candidate = Path.Combine(Environment.GetFolderPath(folder), relative);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    internal static void Show(Window owner)
    {
        var installed = InstalledPath();
        if (installed != null) { Open(owner, installed); return; }
        var dialog = new Window { Owner = owner, Title = Strings.BridgeTitle, Width = 500, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false };
        dialog.SetResourceReference(Control.BackgroundProperty, "Brush.Bg.Elevated");
        dialog.SetResourceReference(Control.ForegroundProperty, "Brush.Text.Primary");
        var body = new StackPanel { Margin = new Thickness(24) };
        body.Children.Add(new Image { Source = new BitmapImage(new Uri("pack://application:,,,/T-DeskTree;component/Assets/Family/t-bridge.png")), Width = 64, Height = 64, HorizontalAlignment = HorizontalAlignment.Left });
        body.Children.Add(new TextBlock { Text = Strings.BridgeTitle, FontSize = 20, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,12,0,8) });
        body.Children.Add(new TextBlock { Text = Strings.BridgeLine, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,20) });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var get = new Button { Content = Strings.GetBridge, Margin = new Thickness(0,0,8,0), IsDefault = true };
        get.SetResourceReference(FrameworkElement.StyleProperty, "Button.Primary");
        get.Click += (_,_) => Open(dialog, "https://github.com/taha-sa/t-bridge-releases/releases/latest");
        var close = new Button { Content = "×", IsCancel = true };
        System.Windows.Automation.AutomationProperties.SetName(close, Strings.Close);
        close.SetResourceReference(FrameworkElement.StyleProperty, "Button.Secondary");
        actions.Children.Add(get); actions.Children.Add(close); body.Children.Add(actions);
        dialog.Content = body;
        dialog.ShowDialog();
    }

    static void Open(Window owner, string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose(); }
        catch { MessageBox.Show(owner, Strings.ActionFailed, "T-DeskTree", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
