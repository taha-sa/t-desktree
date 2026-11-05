using System.Windows;

namespace AmberTree.Views;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, bool isDanger = false)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;

        if (isDanger)
        {
            ConfirmButton.Style = (Style)FindResource("Button.Danger");
            ConfirmButton.Content = "Sil";
            IconGlyph.Text = (string)FindResource("Icon.Delete");
            IconGlyph.Foreground = (System.Windows.Media.Brush)FindResource("Brush.Danger");
            IconBorder.Background = (System.Windows.Media.Brush)FindResource("Brush.Danger.Soft");
        }
        else
        {
            ConfirmButton.Style = (Style)FindResource("Button.Primary");
            ConfirmButton.Content = "Evet";
            IconGlyph.Text = (string)FindResource("Icon.Info");
            IconGlyph.Foreground = (System.Windows.Media.Brush)FindResource("Brush.Accent");
            IconBorder.Background = (System.Windows.Media.Brush)FindResource("Brush.Accent.Soft");
        }

        // Parent window varsa CenterOwner ile ortalayalim
        if (Application.Current?.MainWindow != null && Application.Current.MainWindow.IsVisible)
        {
            Owner = Application.Current.MainWindow;
        }
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
