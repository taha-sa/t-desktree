using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Xml.Linq;
using AmberTree;
using AmberTree.Localization;
using AmberTree.Services;
using AmberTree.Views;
using TSuite.Diagnostics;
using TSuite.Update;

internal static class Checks
{
    [STAThread]
    static void Main()
    {
        var app = new App();
        app.InitializeComponent(); // Resources only: do not run startup, scans or T-Link.
        LanguagePreference.InitializeWpf(); // Same resource/metadata order as normal application startup.
        var session = Activator.CreateInstance(typeof(CrashReporter).Assembly.GetType("TSuite.Diagnostics.ReporterSession")!,
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { "desktree-checks", "Checks", "1.0.0", "taha-sa/t-desktree-releases", Path.Combine(AppContext.BaseDirectory, "test-data") }, null);
        typeof(CrashReporter).GetProperty("Current", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, session);
        Dictionary<string,string> Read(string suffix) => XDocument.Load(Path.Combine(AppContext.BaseDirectory, "SourceResources", $"Strings{suffix}.resx"))
            .Root!.Elements("data").ToDictionary(e => (string)e.Attribute("name")!, e => e.Element("value")!.Value);
        var baseline = Read("");
        foreach (var (language, suffix) in new[] { ("en", ""), ("tr", ".tr"), ("de", ".de") })
        {
            LanguagePreference.Apply(language);
            var translated = Read(suffix);
            if (!baseline.Keys.Order().SequenceEqual(translated.Keys.Order())) throw new Exception("Resource key mismatch: " + language);
            foreach (var (key, value) in translated)
            {
                static string NormalizeLines(string text) => text.Replace("\r\n", "\n");
                if (string.IsNullOrWhiteSpace(value) || NormalizeLines(Strings.Get(key)) != NormalizeLines(value)) throw new Exception("Resource loading failed: " + key);
                if (CompositeFormat.Parse(value).MinimumArgumentCount != CompositeFormat.Parse(baseline[key]).MinimumArgumentCount)
                    throw new Exception("Format arguments differ: " + key);
                if (value.Contains('!') || Regex.IsMatch(value, "Ã|Ä[±°]|Å[Ÿž]|â€")) throw new Exception("Invalid resource text: " + key);
                if (NormalizeLines((string)new TextExtension(key).ProvideValue(null!)) != NormalizeLines(value)) throw new Exception("Markup extension: " + key);
            }
            foreach (var view in new object[] { new EmptyState(), new CommandBar(), new TitleBar(), new TreeDetailsView(), new TopFilesView(), new TreemapView(), new ScanningState(), new ExtensionsView(), new AgeView(), new SummaryStrip() })
                if (view is not FrameworkElement) throw new Exception("View construction failed.");
            foreach (var window in new Window[] { new AboutDialog(), new ConfirmDialog("Check", "Check"), new ConfirmDialog("Check", "Check", true) }) window.Close();
            var sample = new AmberTree.Models.FsItem { Name = "sample.txt", FullPath = @"C:\fixture\sample.txt", Size = 1536,
                AllocatedSize = 4096, PercentOfParent = 12.5, PercentOfTotal = 25.5, LastModified = new DateTime(2026,11,10) };
            var output = Path.Combine(AppContext.BaseDirectory, "test-data");
            Directory.CreateDirectory(output);
            string csv = Path.Combine(output, "report-" + language + ".csv");
            ExportService.ExportToCsv(sample, csv);
            var row = File.ReadAllLines(csv)[1];
            if (row.Split(',').Length != 12 || !row.Contains("12.50%")) throw new Exception("CSV depends on UI number formatting: " + language);
            sample.Size = long.MaxValue;
            ExportService.ExportToTxt(sample, Path.Combine(output, "report-" + language + ".txt"));
            Console.WriteLine($"PASS {language}: {baseline.Count} resources and WPF views");
        }
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
        if (Strings.Close != "Close") throw new Exception("English fallback failed.");
        foreach (var name in new[] { "LICENSE", "PRIVACY.md", "THIRD-PARTY-NOTICES.md" })
        {
            using var stream = typeof(App).Assembly.GetManifestResourceStream("Legal." + name) ?? throw new Exception("Missing legal document: " + name);
            using var reader = new StreamReader(stream);
            if (reader.ReadToEnd().Length < 100) throw new Exception("Empty legal document: " + name);
        }
        var vm = new UpdateBannerViewModel();
        ((Window)Activator.CreateInstance(typeof(App).Assembly.GetType("AmberTree.Views.LegalDialog")!, true)!).Close();
        var controller = vm.Controller;
        typeof(UpdateController).GetProperty("Info")!.SetValue(controller, new UpdateInfo("1.0.0", "2.0.0", "", null,
            new LatestManifest { Signature = "test", MinVersion = "2.0.0" }));
        typeof(UpdateBannerViewModel).GetField("_ctl", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, controller);
        typeof(UpdateBannerViewModel).GetProperty("IsBannerVisible")!.SetValue(vm, true);
        vm.LaterCommand.Execute(null); vm.SkipCommand.Execute(null);
        if (!vm.IsBannerVisible || vm.LaterCommand.CanExecute(null) || vm.SkipCommand.CanExecute(null)) throw new Exception("Required update can be dismissed.");
        Console.WriteLine("PASS English fallback, embedded legal documents and required update commands");
        app.Shutdown();
    }
}
