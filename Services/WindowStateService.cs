using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;

namespace AmberTree.Services;

/// <summary>
/// Pencere konum/boyut/durumunu %APPDATA%\AmberTree\settings.json icinde "window" anahtari altinda saklar
/// (diger anahtarlar, ornegin "theme", korunur). Degerler WPF DIP biriminde.
/// </summary>
public static class WindowStateService
{
    private const string Key = "window";
    private const double DefaultWidth = 1280, DefaultHeight = 800;

    private static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AmberTree", "settings.json");

    /// <summary>Pencere gosterilmeden once cagrilir. Kayit gecerliyse uygular, degilse ortada varsayilan boyut.</summary>
    public static void Apply(Window w)
    {
        double scale = SystemScale();
        var work = GetWorkAreas(scale);
        if (work.Count == 0)
        {
            var wa = SystemParameters.WorkArea;
            work.Add(new Rect(wa.Left, wa.Top, wa.Width, wa.Height));
        }

        if (TryRead(out var r, out bool maximized) && IsVisibleEnough(r, work))
        {
            r.Width = Math.Max(r.Width, w.MinWidth);
            r.Height = Math.Max(r.Height, w.MinHeight);
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = r.Left; w.Top = r.Top; w.Width = r.Width; w.Height = r.Height;
            if (maximized) w.WindowState = WindowState.Maximized;
            return;
        }

        // Varsayilan: birincil calisma alanina sigan, ortali, Normal.
        var area = work[0];
        var primary = SystemParameters.WorkArea;
        area = new Rect(primary.Left, primary.Top, primary.Width, primary.Height);
        double width = Math.Max(w.MinWidth, Math.Min(DefaultWidth, area.Width));
        double height = Math.Max(w.MinHeight, Math.Min(DefaultHeight, area.Height));
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Width = width; w.Height = height;
        w.Left = area.Left + (area.Width - width) / 2;
        w.Top = area.Top + (area.Height - height) / 2;
        w.WindowState = WindowState.Normal;
    }

    public static void Save(Window w)
    {
        try
        {
            var b = w.RestoreBounds;
            if (b.IsEmpty || double.IsNaN(b.Left) || b.Width <= 0 || b.Height <= 0) return;
            JsonObject obj = new();
            if (File.Exists(SettingsPath))
            {
                try { obj = JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject ?? new JsonObject(); }
                catch { obj = new JsonObject(); }
            }
            obj[Key] = new JsonObject
            {
                ["left"] = Math.Round(b.Left, 1),
                ["top"] = Math.Round(b.Top, 1),
                ["width"] = Math.Round(b.Width, 1),
                ["height"] = Math.Round(b.Height, 1),
                ["state"] = w.WindowState == WindowState.Maximized ? "Maximized" : "Normal",
            };
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* yazilamazsa sessizce gec */ }
    }

    private static bool TryRead(out Rect r, out bool maximized)
    {
        r = default; maximized = false;
        try
        {
            if (!File.Exists(SettingsPath)) return false;
            var o = (JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject)?[Key] as JsonObject;
            if (o == null) return false;
            double l = o["left"]!.GetValue<double>(), t = o["top"]!.GetValue<double>();
            double wd = o["width"]!.GetValue<double>(), ht = o["height"]!.GetValue<double>();
            if (double.IsNaN(l + t + wd + ht) || double.IsInfinity(l + t + wd + ht) || wd < 100 || ht < 100 || wd > 20000 || ht > 20000)
                return false;
            r = new Rect(l, t, wd, ht);
            maximized = string.Equals(o["state"]?.GetValue<string>(), "Maximized", StringComparison.OrdinalIgnoreCase);
            return true;
        }
        catch { return false; }
    }

    private static bool IsVisibleEnough(Rect r, List<Rect> work)
    {
        foreach (var wa in work)
        {
            var i = Rect.Intersect(r, wa);
            if (!i.IsEmpty && i.Width >= 120 && i.Height >= 60) return true;
        }
        return false;
    }

    // --- Win32 ---
    private static double SystemScale()
    {
        try { return GetDpiForSystem() / 96.0; } catch { return 1.0; }
    }

    private static List<Rect> GetWorkAreas(double scale)
    {
        var list = new List<Rect>();
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref RECT __, IntPtr ___) =>
            {
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(hMon, ref mi))
                {
                    var a = mi.rcWork;
                    list.Add(new Rect(a.Left / scale, a.Top / scale, (a.Right - a.Left) / scale, (a.Bottom - a.Top) / scale));
                }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return list;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }
    private delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
}
