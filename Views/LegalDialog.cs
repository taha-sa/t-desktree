using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using AmberTree.Localization;

namespace AmberTree.Views;

internal sealed class LegalDialog : Window
{
    internal LegalDialog()
    {
        Title = Strings.Legal;
        Width = 800; Height = 600; MinWidth = 480; MinHeight = 360;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Brush.Bg.Elevated");
        SetResourceReference(ForegroundProperty, "Brush.Text.Primary");
        var body = new DockPanel { Margin = new Thickness(16) };
        var close = new Button { Content = Strings.Close, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 96, Margin = new Thickness(0,12,0,0) };
        close.SetResourceReference(StyleProperty, "Button.Primary");
        DockPanel.SetDock(close, Dock.Bottom); body.Children.Add(close);
        var tabs = new TabControl();
        foreach (var (title, file) in new[] { (Strings.Privacy, "PRIVACY.md"), (Strings.License, "LICENSE"), (Strings.Notices, "THIRD-PARTY-NOTICES.md") })
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Legal." + file)
                ?? throw new InvalidOperationException("Missing embedded legal document: " + file);
            using var reader = new StreamReader(stream);
            var text = new TextBox { Text = reader.ReadToEnd(), IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12) };
            text.SetResourceReference(Control.BackgroundProperty, "Brush.Bg.Surface");
            text.SetResourceReference(Control.ForegroundProperty, "Brush.Text.Primary");
            System.Windows.Automation.AutomationProperties.SetName(text, title);
            tabs.Items.Add(new TabItem { Header = title, Content = text });
        }
        body.Children.Add(tabs); Content = body;
    }
}
