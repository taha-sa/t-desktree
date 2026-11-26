using AmberTree.Localization;
using System;
using System.Collections.Generic;

namespace AmberTree.Services;

public enum FileCategory
{
    Video,
    Image,
    Audio,
    Archive,
    Code,
    Executable,
    Document,
    Other
}

public static class FileCategoryMap
{
    private static readonly Dictionary<string, FileCategory> Map = Build();

    private static Dictionary<string, FileCategory> Build()
    {
        var d = new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase);
        void Add(FileCategory c, string list)
        {
            foreach (var e in list.Split(' ', StringSplitOptions.RemoveEmptyEntries)) d[e] = c;
        }

        Add(FileCategory.Video, "mp4 mkv avi mov wmv flv webm m4v mpg mpeg 3gp ts m2ts vob ogv mts divx rm rmvb");
        Add(FileCategory.Image, "jpg jpeg png gif bmp tif tiff webp svg ico heic heif raw cr2 nef arw dng psd ai xcf eps tga dds avif jfif");
        Add(FileCategory.Audio, "mp3 wav flac aac ogg wma m4a opus aiff aif mid midi amr ape wv");
        Add(FileCategory.Archive, "zip rar 7z tar gz bz2 xz zst tgz cab iso img dmg wim lz lzma jar apk msi vhd vhdx vmdk");
        Add(FileCategory.Code, "cs vb fs xaml cpp c h hpp cc cxx rs go java kt kts swift py pyw rb php pl lua r js jsx mjs cjs tsx html htm css scss sass less json xml yaml yml toml ini cfg conf config sql sh bash ps1 psm1 bat cmd csproj sln vcxproj props targets gradle cmake makefile txt md markdown log csv tsv rst tex diff patch gitignore lock proj resx razor cshtml vue svelte dart");
        Add(FileCategory.Executable, "exe dll sys drv scr com ocx cpl efi msc mui lnk bin dat cat inf pdb etl tmp bak msp mum manifest ax");
        Add(FileCategory.Document, "pdf doc docx xls xlsx ppt pptx odt ods odp rtf epub mobi djvu pages numbers key xps one vsd vsdx pub");
        return d;
    }

    /// <summary>Uzantidan kategori. Nokta ile ya da noktasiz, buyuk/kucuk harf fark etmez.</summary>
    public static FileCategory FromExtension(string? ext)
    {
        if (string.IsNullOrWhiteSpace(ext)) return FileCategory.Other;
        var e = ext.Trim();
        if (e.StartsWith('.')) e = e.Substring(1);
        if (e.Length == 0) return FileCategory.Other;
        return Map.TryGetValue(e, out var c) ? c : FileCategory.Other;
    }

    /// <summary>Kategori icin Brush kaynak anahtari, ornek "Brush.Cat.Video".</summary>
    public static string BrushKey(FileCategory c) => "Brush.Cat." + c;

    /// <summary>Kategori icin Color kaynak anahtari, ornek "Color.Cat.Video".</summary>
    public static string ColorKey(FileCategory c) => "Color.Cat." + c;

    /// <summary>Turkce gorunen ad.</summary>
    public static string DisplayName(FileCategory c) => c switch
    {
        FileCategory.Video => "Video",
        FileCategory.Image => Strings.ImageCategory,
        FileCategory.Audio => Strings.AudioCategory,
        FileCategory.Archive => Strings.ArchiveCategory,
        FileCategory.Code => Strings.CodeCategory,
        FileCategory.Executable => Strings.ExecutableCategory,
        FileCategory.Document => Strings.DocumentCategory,
        _ => Strings.OtherCategory
    };
}
