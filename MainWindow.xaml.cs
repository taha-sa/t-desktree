using System.Windows;
using AmberTree.Services;
using AmberTree.ViewModels;

namespace AmberTree;

/// <summary>Yalnizca pencere kabugu: DataContext ve tam ekranda kenar tasmasini onleme.</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        DataContext = new MainWindowViewModel(new DialogService(), new WpfUiDispatcher(Dispatcher));
        InitializeComponent();
        WindowStateService.Apply(this);
        Closing += (_, _) => WindowStateService.Save(this);
        StateChanged += (_, _) => UpdateMaximizeInset();
        Loaded += (_, _) => UpdateMaximizeInset();
    }

    /// <summary>WindowChrome ile ekranı kaplayan pencere ekran dışına taşar; çerçeve kalınlığı kadar iç boşluk bırakır.</summary>
    private void UpdateMaximizeInset()
    {
        BorderThickness = WindowState == WindowState.Maximized
            ? SystemParameters.WindowResizeBorderThickness
            : new Thickness(0);
    }

    private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch { }
        e.Handled = true;
    }
}
