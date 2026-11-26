using System;
using System.IO;
using System.Text;
using System.Text.Json;
using AmberTree.Models;
using AmberTree.Localization;

namespace AmberTree.Services;

public static class ExportService
{
    public static void ExportToCsv(FsItem root, string destinationPath)
    {
        using var writer = new StreamWriter(destinationPath, false, Encoding.UTF8);
        writer.WriteLine("Level,Name,Path,Type,Size (Bytes),Allocated (Bytes),% of Parent,% of Total,Files,Folders,Last Modified,Attributes");

        var stack = new Stack<(FsItem item, int level)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (item, level) = stack.Pop();
            string type = item.IsDirectory ? "Directory" : "File";
            string name = EscapeCsv(item.Name);
            string path = EscapeCsv(item.FullPath);
            writer.WriteLine(FormattableString.Invariant($"{level},{name},{path},{type},{item.Size},{item.AllocatedSize},{item.PercentOfParent:F2}%,{item.PercentOfTotal:F2}%,{item.FileCount},{item.FolderCount},{item.LastModified:yyyy-MM-dd HH:mm:ss},{item.AttributesDisplay}"));

            var arr = item.RawChildren;
            if (arr != null)
            {
                for (int i = arr.Length - 1; i >= 0; i--)
                {
                    stack.Push((arr[i], level + 1));
                }
            }
        }
    }

    public static void ExportToJson(ScanResult result, string destinationPath)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles
        };
        using var stream = File.Create(destinationPath);
        JsonSerializer.Serialize(stream, result, options);
    }

    public static void ExportToTxt(FsItem root, string destinationPath)
    {
        using var writer = new StreamWriter(destinationPath, false, Encoding.UTF8);
        writer.WriteLine("===============================================================================");
        writer.WriteLine(Strings.Format(Strings.ScanReport, DateTime.Now));
        writer.WriteLine(Strings.Format(Strings.ReportRoot, root.FullPath));
        writer.WriteLine(Strings.Format(Strings.ReportSize, FormatSize(root.Size), root.Size));
        writer.WriteLine(Strings.Format(Strings.ReportAllocated, FormatSize(root.AllocatedSize), root.AllocatedSize));
        writer.WriteLine(Strings.Format(Strings.ReportCounts, root.FileCount, root.FolderCount));
        writer.WriteLine("===============================================================================");
        writer.WriteLine();

        var stack = new Stack<(FsItem item, string indent, bool isLast)>();
        stack.Push((root, "", true));
        while (stack.Count > 0)
        {
            var (item, indent, isLast) = stack.Pop();
            string marker = isLast ? "└── " : "├── ";
            string sizeStr = FormatSize(item.Size).PadLeft(10);
            string pctStr = $"{item.PercentOfParent:F1}%".PadLeft(6);
            writer.WriteLine($"{indent}{marker}[{sizeStr} | {pctStr}] {item.Name}");

            string childIndent = indent + (isLast ? "    " : "│   ");
            var arr = item.RawChildren;
            if (arr != null)
            {
                for (int i = arr.Length - 1; i >= 0; i--)
                {
                    stack.Push((arr[i], childIndent, i == arr.Length - 1));
                }
            }
        }
    }

    private static string EscapeCsv(string val)
    {
        if (string.IsNullOrEmpty(val)) return string.Empty;
        // Neutralize spreadsheet formula injection on leading =, +, -, @, tab, CR.
        string safe = val[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + val : val;
        if (safe.IndexOfAny([',', '"', '\n', '\r']) >= 0)
        {
            return $"\"{safe.Replace("\"", "\"\"")}\"";
        }
        return safe;
    }

    private static string FormatSize(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int counter = 0;
        decimal number = bytes;
        while (counter < suffixes.Length - 1 && Math.Round(number / 1024) >= 1)
        {
            number /= 1024;
            counter++;
        }
        return $"{number:F2} {suffixes[counter]}";
    }
}
