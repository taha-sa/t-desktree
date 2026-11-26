using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Markup;

namespace AmberTree.Localization;

public static class LanguagePreference
{
    static readonly CultureInfo SystemUi = CultureInfo.CurrentUICulture;
    static readonly CultureInfo SystemFormatting = CultureInfo.CurrentCulture;
    public static readonly string[] Languages = ["", "tr", "en", "de"];
    public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AmberTree", "settings.json");

    public static string Read()
    {
        try { return Normalize(JsonNode.Parse(File.ReadAllText(SettingsPath))?["language"]?.GetValue<string>()); }
        catch { return ""; }
    }

    public static string Normalize(string? language) => language is "tr" or "en" or "de" ? language : "";

    public static void Apply(string? language)
    {
        language = Normalize(language);
        var culture = language switch {
            "tr" => CultureInfo.GetCultureInfo("tr-TR"),
            "en" => CultureInfo.GetCultureInfo("en-US"),
            "de" => CultureInfo.GetCultureInfo("de-DE"),
            _ => SystemUi
        };
        CultureInfo.CurrentUICulture = CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = CultureInfo.DefaultThreadCurrentCulture = language == "" ? SystemFormatting : culture;
    }

    // Preserve theme and window preferences in the shared settings file.
    public static void Save(string language)
    {
        JsonObject settings = File.Exists(SettingsPath)
            ? JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject ?? new JsonObject()
            : new JsonObject();
        settings["language"] = Normalize(language);
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        string temporary = SettingsPath + ".language.tmp";
        File.WriteAllText(temporary, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, SettingsPath, true);
    }

    public static void InitializeWpf()
    {
        Apply(Read());
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag)));
    }
}
