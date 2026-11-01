using System.ComponentModel;

namespace AmberTree.Models;

/// <summary>Küçük INotifyPropertyChanged tabanı (model sınıfları için).</summary>
public abstract class ObservableBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
