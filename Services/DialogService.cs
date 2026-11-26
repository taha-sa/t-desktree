using AmberTree.Localization;
using System;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace AmberTree.Services;

public interface IDialogService
{
    /// <summary>Evet/Hayır onayı. isDanger=true ise uyarı simgesi kullanılır ve varsayılan seçenek "Hayır"dır.</summary>
    bool Confirm(string title, string message, bool isDanger = false);

    /// <summary>Klasör seçtirir; iptalde null.</summary>
    string? PickFolder(string? initialDirectory = null);

    /// <summary>Kaydetme yeri seçtirir (filter: "CSV (*.csv)|*.csv" biçiminde); iptalde null.</summary>
    string? PickSaveFile(string filter, string defaultName);

    /// <summary>Hakkında kartını gösterir.</summary>
    void ShowAbout();
}

public class DialogService : IDialogService
{
    public bool Confirm(string title, string message, bool isDanger = false)
    {
        try
        {
            var dlg = new Views.ConfirmDialog(title, message, isDanger);
            return dlg.ShowDialog() == true;
        }
        catch
        {
            var result = MessageBox.Show(
                message,
                title,
                MessageBoxButton.YesNo,
                isDanger ? MessageBoxImage.Warning : MessageBoxImage.Question,
                MessageBoxResult.No);
            return result == MessageBoxResult.Yes;
        }
    }

    public string? PickFolder(string? initialDirectory = null)
    {
        var dlg = new OpenFolderDialog
        {
            Title = Strings.ChooseAnalysisPath,
            InitialDirectory = !string.IsNullOrEmpty(initialDirectory) && Directory.Exists(initialDirectory)
                ? initialDirectory
                : "C:\\"
        };
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    public string? PickSaveFile(string filter, string defaultName)
    {
        var sfd = new SaveFileDialog
        {
            Filter = filter,
            FileName = defaultName,
            DefaultDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };
        return sfd.ShowDialog() == true ? sfd.FileName : null;
    }

    public void ShowAbout()
    {
        try
        {
            var dlg = new Views.AboutDialog();
            dlg.ShowDialog();
        }
        catch { }
    }
}
