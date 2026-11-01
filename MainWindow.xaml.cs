using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AmberTree.Models;
using AmberTree.Services;
using AmberTree.ViewModels;

namespace AmberTree;

public partial class MainWindow : Window
{
    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();

        TreemapView.ItemSelected += (item) =>
        {
            TreemapCurrentPath.Text = item.FullPath;
        };

        TreemapView.ItemDoubleClicked += (item) =>
        {
            TreemapCurrentPath.Text = item.FullPath;
        };
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SearchText = string.Empty;
    }

    private FsItem? GetSelectedFsItem(object sender)
    {
        if (sender is MenuItem mi && mi.DataContext is FsItem item)
        {
            return item;
        }
        return TreeList.SelectedItem as FsItem;
    }

    private void MenuOpenExplorer_Click(object sender, RoutedEventArgs e)
    {
        var item = GetSelectedFsItem(sender);
        if (item != null)
        {
            ShellHelper.OpenInExplorer(item.FullPath);
        }
    }

    private void MenuOpenTerminal_Click(object sender, RoutedEventArgs e)
    {
        var item = GetSelectedFsItem(sender);
        if (item != null)
        {
            ShellHelper.OpenTerminal(item.FullPath);
        }
    }

    private void MenuCopyPath_Click(object sender, RoutedEventArgs e)
    {
        var item = GetSelectedFsItem(sender);
        if (item != null)
        {
            ShellHelper.CopyPath(item.FullPath);
        }
    }

    private void MenuRecycle_Click(object sender, RoutedEventArgs e)
    {
        var item = GetSelectedFsItem(sender);
        if (item == null) return;

        var confirm = MessageBox.Show($"Move '{item.Name}' to Recycle Bin?", "Confirm Recycle", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm == MessageBoxResult.Yes)
        {
            if (ShellHelper.MoveToRecycleBin(item.FullPath))
            {
                item.Parent?.Children.Remove(item);
                MessageBox.Show($"'{item.Name}' moved to Recycle Bin.", "AmberTree", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }

    private void MenuDeletePermanent_Click(object sender, RoutedEventArgs e)
    {
        var item = GetSelectedFsItem(sender);
        if (item == null) return;

        var confirm = MessageBox.Show($"PERMANENTLY DELETE '{item.Name}'?\n\nThis action cannot be undone!", "Caution: Permanent Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm == MessageBoxResult.Yes)
        {
            if (ShellHelper.PermanentDelete(item.FullPath))
            {
                item.Parent?.Children.Remove(item);
                MessageBox.Show($"'{item.Name}' permanently deleted.", "AmberTree", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }

    private async void MenuRescan_Click(object sender, RoutedEventArgs e)
    {
        var item = GetSelectedFsItem(sender);
        if (item != null && item.IsDirectory)
        {
            ViewModel.SelectedPath = item.FullPath;
            await ViewModel.StartScanAsync();
        }
    }

    private void TreemapRoot_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.RootItem != null)
        {
            TreemapView.NavigateTo(ViewModel.RootItem);
            TreemapCurrentPath.Text = ViewModel.RootItem.FullPath;
        }
    }

    private void TreemapUp_Click(object sender, RoutedEventArgs e)
    {
        TreemapView.NavigateUp();
        if (TreemapView.CurrentItem != null)
        {
            TreemapCurrentPath.Text = TreemapView.CurrentItem.FullPath;
        }
    }

    private void TopFileOpenExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (TopFilesGrid.SelectedItem is TopFileItem top)
        {
            ShellHelper.OpenInExplorer(top.FullPath);
        }
    }

    private void TopFileCopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (TopFilesGrid.SelectedItem is TopFileItem top)
        {
            ShellHelper.CopyPath(top.FullPath);
        }
    }

    private void TopFileRecycle_Click(object sender, RoutedEventArgs e)
    {
        if (TopFilesGrid.SelectedItem is TopFileItem top)
        {
            var confirm = MessageBox.Show($"Move '{top.Name}' to Recycle Bin?", "Confirm Recycle", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm == MessageBoxResult.Yes)
            {
                if (ShellHelper.MoveToRecycleBin(top.FullPath))
                {
                    ViewModel.TopFiles.Remove(top);
                }
            }
        }
    }
}
