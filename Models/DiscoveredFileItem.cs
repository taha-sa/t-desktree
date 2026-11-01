using System;
using AmberTree.Converters;
using AmberTree.Services;

namespace AmberTree.Models;

/// <summary>
/// Tarama esnasında canlı akışta (streaming) gösterilen dosya kaydı.
/// </summary>
public class DiscoveredFileItem
{
    public string Name { get; init; } = string.Empty;
    public string DirectoryPath { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public long Size { get; init; }
    public string SizeDisplay => SizeFormatter.FormatBytes(Size, SizeFormatter.CurrentMode);
    public string Extension { get; init; } = string.Empty;
    public DateTime LastModified { get; init; }
    public FileCategory Category => FileCategoryMap.FromExtension(Extension);
}
