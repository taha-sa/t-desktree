using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AmberTree.Views;

public partial class TopFilesView : UserControl
{
    public TopFilesView() => InitializeComponent();

    /// <summary>Sag tik satiri secer; menu komutlari secili satiri (PlacementTarget.DataContext) kullanir.</summary>
    private void Row_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGridRow row)
        {
            row.IsSelected = true;
            row.Focus();
        }
    }
}
