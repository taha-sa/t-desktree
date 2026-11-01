using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Win32;

namespace AmberTree.Services;

/// <summary>
/// Koyu/acik tema gecisi. Application.Resources.MergedDictionaries icindeki
/// Tokens.*.xaml sozlugunu ayni siraya yerlestirerek degistirir (DynamicResource guncellenir).
/// Tercih: %APPDATA%\AmberTree\settings.json  ({"theme":"dark"|"light"}; diger anahtarlar korunur).
/// </summary>
public sealed class ThemeService
{
    private const string SettingsKey = "theme";

    public static ThemeService Current { get; } = new();

    private ThemeService() { }

    public bool IsDark { get; private set; } = true;

    public event EventHandler? ThemeChanged;

    private static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AmberTree", "settings.json");

    /// <summary>Kayitli tercihi uygular; yoksa sistem tercihini (AppsUseLightTheme) okur. Kaydetmez.</summary>
    public void LoadSaved()
    {
        bool dark = ReadSavedPreference() ?? !SystemPrefersLight();
        ApplyCore(dark);
    }

    public void Apply(bool dark)
    {
        ApplyCore(dark);
        SavePreference(dark);
    }

    public void Toggle() => Apply(!IsDark);

    private void ApplyCore(bool dark)
    {
        var app = Application.Current;
        if (app != null)
        {
            var uri = new Uri(
                dark ? "pack://application:,,,/Themes/Tokens.Dark.xaml" : "pack://application:,,,/Themes/Tokens.Light.xaml",
                UriKind.Absolute);
            var merged = app.Resources.MergedDictionaries;
            int idx = -1;
            for (int i = 0; i < merged.Count; i++)
            {
                var src = merged[i].Source?.OriginalString;
                if (src != null && src.Contains("Tokens.", StringComparison.OrdinalIgnoreCase))
                {
                    idx = i;
                    break;
                }
            }

            var fresh = new ResourceDictionary { Source = uri };
            if (idx >= 0) merged[idx] = fresh;
            else merged.Insert(0, fresh);
        }

        bool changed = IsDark != dark;
        IsDark = dark;
        if (changed) ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool? ReadSavedPreference()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;
            var node = JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject;
            var value = node?[SettingsKey]?.GetValue<string>();
            if (string.Equals(value, "dark", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(value, "light", StringComparison.OrdinalIgnoreCase)) return false;
        }
        catch { /* bozuk ayar dosyasi: yok say */ }
        return null;
    }

    private static void SavePreference(bool dark)
    {
        try
        {
            JsonObject obj = new();
            if (File.Exists(SettingsPath))
            {
                try { obj = JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject ?? new JsonObject(); }
                catch { obj = new JsonObject(); }
            }
            obj[SettingsKey] = dark ? "dark" : "light";
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* yazilamazsa sessizce gec */ }
    }

    private static bool SystemPrefersLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }
}
