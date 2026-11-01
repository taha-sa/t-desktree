using System.Windows.Controls;
using System.Windows.Input;

namespace AmberTree.Views;

public partial class ScanningState : UserControl
{
    public ScanningState() => InitializeComponent();

    private void Row_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGridRow row)
        {
            row.IsSelected = true;
            row.Focus();
        }
    }
}
