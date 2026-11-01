using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AmberTree.Converters;

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
    {
        return value is Visibility.Visible;
    }
}

public class BoolToVisibilityCollapseConverter : IValueConverter
{
    // For IsFilteredOut: true means filtered out -> Collapsed; false means keep -> Visible
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isFilteredOut = value is true;
        return isFilteredOut ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class DirToFileIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return (value is true) ? "📁" : "📄";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class PercentToWidthConverter : IValueConverter
{
    public double MaxWidth { get; set; } = 100.0;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double pct = 0;
        if (value is double d) pct = d;
        else if (value is float f) pct = f;
        else if (value is int i) pct = i;

        pct = Math.Clamp(pct, 0.0, 100.0);
        return Math.Max(2.0, (pct / 100.0) * MaxWidth);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}
