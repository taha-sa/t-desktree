using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using TSuite.Diagnostics;

namespace AmberTree.Views;

public partial class CrashPreviewDialog : Window
{
    private readonly CrashReport _report;
    private readonly bool _pending;
    private bool _sent;

    public CrashPreviewDialog(CrashReport report, bool pending)
    {
        InitializeComponent();
        _report = report;
        _pending = pending;
        Heading.Text = pending ? "Uygulama beklenmedik şekilde kapandı" : "Hata raporu";
        Preview.Text = CrashReporter.GetReportText(report);
        Closed += (_, _) => { if (_pending && !_sent) CrashReporter.MarkDismissed(_report); };
    }

    public static CrashReport CreateManualReport()
    {
        var lines = new Queue<string>();
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "T-Suite", "logs", "t-desktree", "app.log");
        try
        {
            foreach (var file in new[] { path + ".2", path + ".1", path })
                if (File.Exists(file)) foreach (var line in File.ReadLines(file))
                {
                    lines.Enqueue(line);
                    if (lines.Count > 200) lines.Dequeue();
                }
        }
        catch (Exception ex) { Log.Error("Could not read recent log", ex); }
        using var process = Process.GetCurrentProcess();
        return new CrashReport("manual", DateTime.UtcNow, "t-desktree", "T-DeskTree",
            typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.1.0", "taha-sa/t-desktree-releases",
            RuntimeInformation.OSDescription + " (" + Environment.OSVersion.Version + ")",
            RuntimeInformation.FrameworkDescription, CultureInfo.CurrentCulture.Name,
            DateTime.UtcNow - process.StartTime.ToUniversalTime(),
            new ExceptionDetails("Manual report", "Kullanıcı tarafından oluşturuldu", null, []), lines.ToArray());
    }

    private void Send_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(CrashReporter.BuildIssueUrl(_report)) { UseShellExecute = true });
            if (_pending) CrashReporter.MarkReported(_report);
            _sent = true;
            Close();
        }
        catch (Exception ex)
        {
            Log.Error("Could not open report", ex);
            MessageBox.Show("Rapor açılamadı: " + ex.Message, "T-DeskTree", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(Preview.Text); }
        catch (Exception ex) { MessageBox.Show("Rapor kopyalanamadı: " + ex.Message, "T-DeskTree", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
