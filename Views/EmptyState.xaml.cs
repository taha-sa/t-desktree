using System.Windows;
using System.Windows.Controls;
using AmberTree.ViewModels;

namespace AmberTree.Views;

public partial class EmptyState : UserControl
{
    public EmptyState() => InitializeComponent();

    /// <summary>Son taranan yolu yola yazip taramayi baslatir (yalnizca mevcut VM yuzeyini cagirir).</summary>
    private void RescanLast_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        vm.SelectedPath = vm.LastScannedPath;
        if (vm.StartScanCommand.CanExecute(null)) vm.StartScanCommand.Execute(null);
    }
}
