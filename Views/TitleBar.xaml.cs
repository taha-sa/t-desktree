using AmberTree.Localization;
using System;
using System.Windows;
using System.Windows.Controls;
using AmberTree.Services;

namespace AmberTree.Views;

/// <summary>Baslik cubugu: yalnizca pencere dugmeleri ve glif guncellemesi. Surukleme/cift tik WindowChrome'a aittir.</summary>
public partial class TitleBar : UserControl
{
    public TitleBar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private Window? Host => Window.GetWindow(this);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Host is { } w) w.StateChanged += OnStateChanged;
        ThemeService.Current.ThemeChanged += OnThemeChanged;
        UpdateGlyphs();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (Host is { } w) w.StateChanged -= OnStateChanged;
        ThemeService.Current.ThemeChanged -= OnThemeChanged;
    }

    private void OnStateChanged(object? sender, EventArgs e) => UpdateGlyphs();
    private void OnThemeChanged(object? sender, EventArgs e) => UpdateGlyphs();

    private void UpdateGlyphs()
    {
        bool max = Host?.WindowState == WindowState.Maximized;
        MaxButton.SetResourceReference(ContentControl.ContentProperty, max ? "Icon.Restore" : "Icon.Maximize");
        MaxButton.ToolTip = max ? Strings.RestoreSize : Strings.MaximizeWindow;
        ThemeGlyph.SetResourceReference(TextBlock.TextProperty, ThemeService.Current.IsDark ? "Icon.Sun" : "Icon.Moon");
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        if (Host is { } w) SystemCommands.MinimizeWindow(w);
    }

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (Host is not { } w) return;
        if (w.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(w);
        else SystemCommands.MaximizeWindow(w);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (Host is { } w) SystemCommands.CloseWindow(w);
    }
}
