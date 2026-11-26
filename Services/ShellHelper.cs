using AmberTree.Localization;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using AmberTree.Models;

namespace AmberTree.Services;

public static class ShellHelper
{
    // --- Hata dondurenler (VM kullanir; MessageBox yok) ---

    public static bool TryOpenInExplorer(string path, out string? error)
    {
        error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(['"', '\0']) >= 0)
            {
                error = Strings.PathMissing + path;
                return false;
            }
            if (File.Exists(path))
            {
                Process.Start("explorer.exe", $"/select,\"{path.TrimEnd('\\')}\"")?.Dispose();
            }
            else if (Directory.Exists(path))
            {
                var psi = new ProcessStartInfo("explorer.exe");
                psi.ArgumentList.Add(path);
                Process.Start(psi)?.Dispose();
            }
            else
            {
                error = Strings.PathMissing + path;
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = Strings.ExplorerFailed + ex.Message;
            return false;
        }
    }

    public static bool TryOpenTerminal(string path, out string? error)
    {
        error = null;
        try
        {
            string dir = Directory.Exists(path) ? path : (Path.GetDirectoryName(path) ?? "C:\\");
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                WorkingDirectory = dir,
                UseShellExecute = true
            };
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            error = Strings.TerminalFailed + ex.Message;
            return false;
        }
    }

    public static bool TryCopyPath(string path, out string? error)
    {
        error = null;
        try
        {
            Clipboard.SetText(path);
            return true;
        }
        catch (Exception ex)
        {
            error = Strings.ClipboardFailed + ex.Message;
            return false;
        }
    }

    public static bool TryPermanentDelete(string path, out string? error)
    {
        error = null;
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
                return true;
            }
            error = Strings.PathMissing + path;
        }
        catch (Exception ex)
        {
            error = Strings.DeleteError + ex.Message;
        }
        return false;
    }

    // --- Eski imzalar (geriye uyumluluk; artik iletisim kutusu gostermez) ---

    public static void OpenInExplorer(string path) => TryOpenInExplorer(path, out _);

    public static void OpenTerminal(string path) => TryOpenTerminal(path, out _);

    public static void CopyPath(string path) => TryCopyPath(path, out _);

    public static bool MoveToRecycleBin(string path)
    {
        return NativeFileSystem.MoveToRecycleBin(path);
    }

    public static bool PermanentDelete(string path) => TryPermanentDelete(path, out _);

    public static bool IsRunningAsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static bool RestartAsAdministrator()
    {
        try
        {
            var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath)) return false;

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas"
            };
            Process.Start(psi);
            Application.Current.Shutdown();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static List<DriveInfoModel> GetSystemDrives()
    {
        var result = new List<DriveInfoModel>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (!d.IsReady) continue;

                result.Add(new DriveInfoModel
                {
                    Name = d.Name,
                    RootDirectory = d.RootDirectory.FullName,
                    VolumeLabel = d.VolumeLabel,
                    DriveFormat = d.DriveFormat,
                    DriveType = d.DriveType,
                    TotalSize = d.TotalSize,
                    AvailableFreeSpace = d.AvailableFreeSpace
                });
            }
        }
        catch { }
        return result;
    }
}
