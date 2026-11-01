using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AmberTree.Models;
using AmberTree.ViewModels;

namespace AmberTree.Views;

/// <summary>Agac gorunumu: kolon surukleme, secimi VM'e aktarma ve sag tikta satiri secme.</summary>
public partial class TreeDetailsView : UserControl
{
    public TreeDetailsView() => InitializeComponent();

    private void Splitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is Thumb { Tag: string col } && Resources["Cols"] is TreeColumns cols)
        {
            cols.DragBoundary(col, e.HorizontalChange);
        }
    }

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is MainWindowViewModel vm && e.NewValue is FsItem item)
        {
            vm.SelectedItem = item;
        }
    }

    private void Tree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var d = e.OriginalSource as DependencyObject;
        while (d != null && d is not TreeViewItem)
        {
            d = VisualTreeHelper.GetParent(d);
        }
        if (d is TreeViewItem tvi)
        {
            tvi.IsSelected = true;
            tvi.Focus();
        }
    }
}
