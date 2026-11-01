using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace AmberTree.Views;

/// <summary>
/// Agac gorunumu kolon genislikleri. Baslik ve tum satirlar ayni nesneye baglanir, bu yuzden hizali kalir.
/// "Ad" kolonu kalan alani doldurur (star); digerleri piksel genisligindedir ve baslikta surukleyerek degisir.
/// </summary>
public sealed class TreeColumns : INotifyPropertyChanged
{
    public const double MinWidth = 44;

    private double _size = 84, _allocated = 84, _percent = 132, _files = 64, _folders = 64, _modified = 124, _attr = 64;

    public double Size { get => _size; set => Set(ref _size, value); }
    public double Allocated { get => _allocated; set => Set(ref _allocated, value); }
    public double Percent { get => _percent; set => Set(ref _percent, value); }
    public double Files { get => _files; set => Set(ref _files, value); }
    public double Folders { get => _folders; set => Set(ref _folders, value); }
    public double Modified { get => _modified; set => Set(ref _modified, value); }
    public double Attr { get => _attr; set => Set(ref _attr, value); }

    private static readonly string[] Order = { "Name", "Size", "Allocated", "Percent", "Files", "Folders", "Modified", "Attr" };

    /// <summary>Soldan index'inci kolon ile sagindaki sinir surukleninceki degisim (delta &gt; 0: sinir saga).</summary>
    public void DragBoundary(string rightColumn, double delta)
    {
        int idx = System.Array.IndexOf(Order, rightColumn);
        if (idx < 1) return;
        string left = Order[idx - 1];

        double rightW = Get(rightColumn);
        double newRight = rightW - delta;
        if (newRight < MinWidth) { delta = rightW - MinWidth; newRight = MinWidth; }

        if (left == "Name")
        {
            // Ad kolonu star: yalnizca sagdaki kolon degisir, Ad kalan alani alir.
            Put(rightColumn, newRight);
            return;
        }

        double leftW = Get(left);
        double newLeft = leftW + delta;
        if (newLeft < MinWidth) { delta = MinWidth - leftW; newLeft = MinWidth; newRight = rightW - delta; }
        Put(left, newLeft);
        Put(rightColumn, newRight);
    }

    private double Get(string name) => name switch
    {
        "Size" => Size, "Allocated" => Allocated, "Percent" => Percent, "Files" => Files,
        "Folders" => Folders, "Modified" => Modified, _ => Attr
    };

    private void Put(string name, double v)
    {
        switch (name)
        {
            case "Size": Size = v; break;
            case "Allocated": Allocated = v; break;
            case "Percent": Percent = v; break;
            case "Files": Files = v; break;
            case "Folders": Folders = v; break;
            case "Modified": Modified = v; break;
            default: Attr = v; break;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set(ref double field, double value, [CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
