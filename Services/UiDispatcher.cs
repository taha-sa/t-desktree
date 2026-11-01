using System;
using System.Windows.Threading;

namespace AmberTree.Services;

/// <summary>UI iş parçacığına geçişi soyutlar (VM, Application.Current'a bağımlı olmasın).</summary>
public interface IUiDispatcher
{
    /// <summary>Eylemi UI iş parçacığında sırayla çalıştırır, beklemez.</summary>
    void Post(Action action);
}

public class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    /// <summary>Oluşturulduğu iş parçacığının dispatcher'ını yakalar (UI iş parçacığında oluşturun).</summary>
    public WpfUiDispatcher() : this(Dispatcher.CurrentDispatcher) { }

    public WpfUiDispatcher(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public void Post(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action);
    }
}
