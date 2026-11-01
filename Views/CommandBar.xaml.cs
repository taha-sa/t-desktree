using System;
using System.Windows;
using System.Windows.Controls;
using AmberTree.ViewModels;

namespace AmberTree.Views;

/// <summary>Komut cubugu: Disa aktar menusunu acar, Ctrl+F istegini arama kutusuna odak olarak cevirir.</summary>
public partial class CommandBar : UserControl
{
    private MainWindowViewModel? _vm;

    public CommandBar()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm != null) _vm.FocusSearchRequested -= OnFocusSearch;
            _vm = e.NewValue as MainWindowViewModel;
            if (_vm != null) _vm.FocusSearchRequested += OnFocusSearch;
        };
    }

    private void OnFocusSearch(object? sender, EventArgs e)
    {
        if (!SearchBox.IsEnabled) return;
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (ExportButton.ContextMenu is not { } menu) return;
        menu.PlacementTarget = ExportButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
