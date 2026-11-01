using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using AmberTree.Models;

namespace AmberTree.Converters;

/// <summary>true -> Visible, false -> Collapsed (Invert ile tersi).</summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value is true;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>IsFilteredOut icin: true (elendi) -> Collapsed, false -> Visible.</summary>
public class BoolToVisibilityCollapseConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>bool -> !bool (ornegin IsEnabled="{Binding IsScanning, Converter=...}").</summary>
public class NotConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Sayi &gt; 0 ise Visible, degilse Collapsed (ornegin Children.Count).</summary>
public class PositiveToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double d = value switch { int i => i, long l => l, double x => x, _ => 0 };
        return d > 0 ? Visibility.Visible : Visibility.Hidden;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Yuzde (0-100) -> Grid star uzunlugu. ConverterParameter "fill" = dolu kisim, "rest" = bos kisim.
/// Oransal bar icin iki star kolon kullanilir; sabit piksel genisligi yoktur.
/// </summary>
public class PercentToGridLengthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double pct = value switch { double d => d, float f => f, int i => i, long l => l, _ => 0 };
        if (double.IsNaN(pct)) pct = 0;
        pct = Math.Clamp(pct, 0.0, 100.0);
        bool rest = string.Equals(parameter as string, "rest", StringComparison.OrdinalIgnoreCase);
        return new GridLength(rest ? 100.0 - pct : pct, GridUnitType.Star);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>FsItem -> agac derinligine gore sol bosluk (Thickness). Satirlar tam genislikte kalir, kolonlar hizali olur.</summary>
public class DepthToIndentConverter : IValueConverter
{
    public double Step { get; set; } = 16;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int depth = 0;
        if (value is FsItem item)
        {
            for (var p = item.Parent; p != null; p = p.Parent) depth++;
        }
        return new Thickness(depth * Step, 0, 0, 0);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>Enum degeri ConverterParameter ile ayni adli mi? (segmentli secici icin, tek yonlu).</summary>
public class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value != null && string.Equals(value.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
