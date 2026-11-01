using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using AmberTree.Models;

namespace AmberTree.Services;

public static class ShellHelper
{
    public static void OpenInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            else if (Directory.Exists(path))
            {
                Process.Start("explorer.exe", $"\"{path}\"");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open in Explorer: {ex.Message}", "AmberTree", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public static void OpenTerminal(string path)
    {
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
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open Command Prompt: {ex.Message}", "AmberTree", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public static void CopyPath(string path)
    {
        try
        {
            Clipboard.SetText(path);
        }
        catch { }
    }

    public static bool MoveToRecycleBin(string path)
    {
        return NativeFileSystem.MoveToRecycleBin(path);
    }

    public static bool PermanentDelete(string path)
    {
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
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Delete failed: {ex.Message}", "AmberTree", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        return false;
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
