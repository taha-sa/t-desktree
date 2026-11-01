using System;
using System.Globalization;
using System.Windows.Data;

namespace AmberTree.Converters;

public enum SizeUnitMode
{
    Auto,
    Bytes,
    KB,
    MB,
    GB,
    TB
}

public class SizeFormatter : IValueConverter
{
    public static SizeUnitMode CurrentMode { get; set; } = SizeUnitMode.Auto;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not long bytes)
        {
            if (value is int intVal) bytes = intVal;
            else return "0 B";
        }

        return FormatBytes(bytes, CurrentMode);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }

    public static string FormatBytes(long bytes, SizeUnitMode mode = SizeUnitMode.Auto)
    {
        if (bytes <= 0) return "0 B";

        switch (mode)
        {
            case SizeUnitMode.Bytes:
                return $"{bytes:N0} B";
            case SizeUnitMode.KB:
                return $"{(double)bytes / 1024:F2} KB";
            case SizeUnitMode.MB:
                return $"{(double)bytes / (1024 * 1024):F2} MB";
            case SizeUnitMode.GB:
                return $"{(double)bytes / (1024 * 1024 * 1024):F2} GB";
            case SizeUnitMode.TB:
                return $"{(double)bytes / (1024L * 1024 * 1024 * 1024):F2} TB";
            case SizeUnitMode.Auto:
            default:
                string[] sizes = { "B", "KB", "MB", "GB", "TB" };
                double len = bytes;
                int order = 0;
                while (len >= 1024 && order < sizes.Length - 1)
                {
                    order++;
                    len /= 1024;
                }
                return $"{len:0.##} {sizes[order]}";
        }
    }
}
