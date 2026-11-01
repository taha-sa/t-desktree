using System;
using System.IO;
using System.Text;
using System.Text.Json;
using AmberTree.Models;

namespace AmberTree.Services;

public static class ExportService
{
    public static void ExportToCsv(FsItem root, string destinationPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Level,Name,Path,Type,Size (Bytes),Allocated (Bytes),% of Parent,% of Total,Files,Folders,Last Modified,Attributes");

        void WriteNode(FsItem item, int level)
        {
            string type = item.IsDirectory ? "Directory" : "File";
            string name = EscapeCsv(item.Name);
            string path = EscapeCsv(item.FullPath);
            sb.AppendLine($"{level},{name},{path},{type},{item.Size},{item.AllocatedSize},{item.PercentOfParent:F2}%,{item.PercentOfTotal:F2}%,{item.FileCount},{item.FolderCount},{item.LastModified:yyyy-MM-dd HH:mm:ss},{item.AttributesDisplay}");

            foreach (var child in item.Children)
            {
                WriteNode(child, level + 1);
            }
        }

        WriteNode(root, 0);
        File.WriteAllText(destinationPath, sb.ToString(), Encoding.UTF8);
    }

    public static void ExportToJson(ScanResult result, string destinationPath)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles
        };
        string json = JsonSerializer.Serialize(result, options);
        File.WriteAllText(destinationPath, json, Encoding.UTF8);
    }

    public static void ExportToTxt(FsItem root, string destinationPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("===============================================================================");
        sb.AppendLine($" AmberTree Scan Report — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($" Root: {root.FullPath}");
        sb.AppendLine($" Total Size: {FormatSize(root.Size)} ({root.Size:N0} bytes)");
        sb.AppendLine($" Total Allocated: {FormatSize(root.AllocatedSize)} ({root.AllocatedSize:N0} bytes)");
        sb.AppendLine($" Total Files: {root.FileCount:N0} | Total Folders: {root.FolderCount:N0}");
        sb.AppendLine("===============================================================================");
        sb.AppendLine();

        void PrintTree(FsItem item, string indent, bool isLast)
        {
            string marker = isLast ? "└── " : "├── ";
            string sizeStr = FormatSize(item.Size).PadLeft(10);
            string pctStr = $"{item.PercentOfParent:F1}%".PadLeft(6);
            sb.AppendLine($"{indent}{marker}[{sizeStr} | {pctStr}] {item.Name}");

            string childIndent = indent + (isLast ? "    " : "│   ");
            for (int i = 0; i < item.Children.Count; i++)
            {
                PrintTree(item.Children[i], childIndent, i == item.Children.Count - 1);
            }
        }

        PrintTree(root, "", true);
        File.WriteAllText(destinationPath, sb.ToString(), Encoding.UTF8);
    }

    private static string EscapeCsv(string val)
    {
        if (val.Contains(',') || val.Contains('"') || val.Contains('\n'))
        {
            return $"\"{val.Replace("\"", "\"\"")}\"";
        }
        return val;
    }

    private static string FormatSize(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024) >= 1)
        {
            number /= 1024;
            counter++;
        }
        return $"{number:F2} {suffixes[counter]}";
    }
}
