using System.Windows;
using AmberTree.Services;
using AmberTree.ViewModels;

namespace AmberTree;

/// <summary>Yalnizca pencere kabugu: DataContext ve tam ekranda kenar tasmasini onleme.</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        var vm = new MainWindowViewModel(new DialogService(), new WpfUiDispatcher(Dispatcher));
        DataContext = vm;
        InitializeComponent();
        WindowStateService.Apply(this);
        if (App.StartInBackground)
        {
            ShowActivated = false;
            WindowState = WindowState.Minimized;
        }
        Closing += (_, _) => WindowStateService.Save(this);

        // T-Suite: T-Link sunucusu + güncelleme afişi (30 sn sonra arka planda kontrol)
        var link = new AppLink(this, vm);
        link.Start();
        UpdateBannerViewModel.Instance.ScheduleCheck(null, link.PublishUpdateAvailable);
        Closed += async (_, _) => await link.DisposeAsync();
        StateChanged += (_, _) => UpdateMaximizeInset();
        Loaded += (_, _) =>
        {
            UpdateMaximizeInset();
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new System.Action(() =>
            {
                if (TSuite.Diagnostics.CrashReporter.GetPendingReport() is { } report)
                    new Views.CrashPreviewDialog(report, true) { Owner = this }.ShowDialog();
            }));
        };
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
            if (e.Uri is { IsAbsoluteUri: true } &&
                (e.Uri.Scheme == System.Uri.UriSchemeHttps || e.Uri.Scheme == System.Uri.UriSchemeHttp))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            }
        }
        catch { }
        e.Handled = true;
    }
}
