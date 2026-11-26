using AmberTree.Localization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AmberTree.Models;
using AmberTree.Services;
using AmberTree.ViewModels;

namespace AmberTree.Controls;

/// <summary>
/// Treemap cizimi. Renkler tema kaynaklarindan (Brush/Color.Cat.*, Bg.*) okunur; tema ya da birim degisince yeniden cizilir.
/// Secim degisiminde tum tuvali yeniden kurmadan O(1) kenarlik gunceller; tum fircalar dondurulmus (Frozen) olarak uretilir.
/// </summary>
public class TreemapCanvas : Canvas
{
    private const int MaxRenderedTiles = 600;

    private FsItem? _currentItem;
    private FsItem? _selected;
    private Border? _selectedBorder;
    private Brush? _edgeBrushCache;
    private Brush? _accentBrushCache;
    private MainWindowViewModel? _vm;
    private bool _resizeQueued;
    private readonly Dictionary<FsItem, (long size, FileCategory cat)> _dominant = new();

    public event Action<FsItem>? ItemSelected;

    public static readonly DependencyProperty SourceItemProperty =
        DependencyProperty.Register(
            nameof(SourceItem), typeof(FsItem), typeof(TreemapCanvas),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSourceItemChanged));

    /// <summary>Cift tiklanan klasor icin calisir (parametre: FsItem).</summary>
    public static readonly DependencyProperty ItemActivatedCommandProperty =
        DependencyProperty.Register(nameof(ItemActivatedCommand), typeof(ICommand), typeof(TreemapCanvas));

    /// <summary>Karolara atanan sag tik menusu. Menu ogeleri PlacementTarget.DataContext (FsItem) ve PlacementTarget.Tag (VM) kullanir.</summary>
    public static readonly DependencyProperty TileContextMenuProperty =
        DependencyProperty.Register(nameof(TileContextMenu), typeof(ContextMenu), typeof(TreemapCanvas),
            new PropertyMetadata(null, (d, _) => ((TreemapCanvas)d).RebuildTreemap()));

    public FsItem? SourceItem
    {
        get => (FsItem?)GetValue(SourceItemProperty);
        set => SetValue(SourceItemProperty, value);
    }

    public ICommand? ItemActivatedCommand
    {
        get => (ICommand?)GetValue(ItemActivatedCommandProperty);
        set => SetValue(ItemActivatedCommandProperty, value);
    }

    public ContextMenu? TileContextMenu
    {
        get => (ContextMenu?)GetValue(TileContextMenuProperty);
        set => SetValue(TileContextMenuProperty, value);
    }

    private static void OnSourceItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TreemapCanvas canvas)
        {
            canvas._currentItem = e.NewValue as FsItem;
            canvas._selected = null;
            canvas._selectedBorder = null;
            canvas._dominant.Clear();
            canvas.RebuildTreemap();
        }
    }

    public TreemapCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        FocusVisualStyle = null;
        SetResourceReference(BackgroundProperty, "Brush.Bg.Base");
        SizeChanged += (_, _) => ScheduleRebuild();
        Loaded += (_, _) =>
        {
            ThemeService.Current.ThemeChanged -= OnThemeChanged;
            ThemeService.Current.ThemeChanged += OnThemeChanged;
        };
        Unloaded += (_, _) =>
        {
            ThemeService.Current.ThemeChanged -= OnThemeChanged;
            Detach();
        };
        DataContextChanged += (_, e) =>
        {
            Detach();
            _vm = e.NewValue as MainWindowViewModel;
            if (_vm != null) _vm.UnitChanged += OnUnitChanged;
        };
    }

    private void ScheduleRebuild()
    {
        if (_resizeQueued) return;
        _resizeQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _resizeQueued = false;
            RebuildTreemap();
        }));
    }

    private void Detach()
    {
        if (_vm != null) _vm.UnitChanged -= OnUnitChanged;
        _vm = null;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ScheduleRebuild();
    private void OnUnitChanged(object? sender, EventArgs e) => ScheduleRebuild();

    public FsItem? CurrentItem => _currentItem;

    // ---------------------------------------------------------------- renk yardimcilari

    private Color ResColor(string key, Color fallback)
        => TryFindResource(key) is Color c ? c : fallback;

    private static SolidColorBrush FrozenBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Color Mix(Color a, Color b, double t)
        => Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));

    private static double ExactLuminance(Color c)
    {
        static double L(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * L(c.R) + 0.7152 * L(c.G) + 0.0722 * L(c.B);
    }

    /// <summary>Klasor icin altindaki baskin (en cok bayt) dosya kategorisi; sonuc onbelleklenir.</summary>
    private FileCategory DominantCategory(FsItem item)
    {
        if (!item.IsDirectory) return item.Category;
        if (_dominant.TryGetValue(item, out var hit) && hit.size == item.Size) return hit.cat;

        var sums = new long[Enum.GetValues<FileCategory>().Length];
        var stack = new Stack<FsItem>();
        stack.Push(item);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            var arr = n.RawChildren;
            if (arr == null) continue;
            for (int i = 0; i < arr.Length; i++)
            {
                var c = arr[i];
                if (c.IsDirectory) stack.Push(c);
                else sums[(int)c.Category] += c.Size;
            }
        }
        int best = 0;
        for (int i = 1; i < sums.Length; i++) if (sums[i] > sums[best]) best = i;
        var cat = sums[best] == 0 ? FileCategory.Other : (FileCategory)best;
        _dominant[item] = (item.Size, cat);
        return cat;
    }

    private sealed class PaletteCache
    {
        public required Color Surface { get; init; }
        public required Color TextPrimary { get; init; }
        public required Brush EdgeBrush { get; init; }
        public required Brush AccentBrush { get; init; }
        public required Brush DarkText { get; init; }
        public required Brush LightText { get; init; }
        public required string FolderGlyph { get; init; }
        public required FontFamily? IconFont { get; init; }
        public required FontFamily? UiFont { get; init; }
        public required FontFamily? MonoFont { get; init; }
        public Dictionary<FileCategory, Color> CatColors { get; } = new();
        public Dictionary<(FileCategory, bool), (Brush fill, Brush hover, Brush fg)> TileBrushes { get; } = new();
    }

    private PaletteCache BuildPalette()
    {
        var baseColor = ResColor("Color.Bg.Base", Colors.Black);
        var surface = ResColor("Color.Bg.Surface", Colors.Gray);
        var textPrimary = ResColor("Color.Text.Primary", Colors.White);
        var edge = FrozenBrush(baseColor);
        var accent = FrozenBrush(ResColor("Color.Accent", Colors.Teal));
        _edgeBrushCache = edge;
        _accentBrushCache = accent;

        var pal = new PaletteCache
        {
            Surface = surface,
            TextPrimary = textPrimary,
            EdgeBrush = edge,
            AccentBrush = accent,
            DarkText = FrozenBrush(Colors.Black),
            LightText = FrozenBrush(Colors.White),
            FolderGlyph = (string)(TryFindResource("Icon.Folder") ?? string.Empty),
            IconFont = TryFindResource("Font.Icons") as FontFamily,
            UiFont = TryFindResource("Font.UI") as FontFamily,
            MonoFont = TryFindResource("Font.Mono") as FontFamily,
        };
        foreach (FileCategory cat in Enum.GetValues<FileCategory>())
        {
            pal.CatColors[cat] = ResColor(FileCategoryMap.ColorKey(cat), Colors.Gray);
        }
        return pal;
    }

    private (Brush fill, Brush hover, Brush fg) GetTileBrushes(PaletteCache pal, FileCategory cat, bool isDir)
    {
        var key = (cat, isDir);
        if (pal.TileBrushes.TryGetValue(key, out var cached)) return cached;

        var catColor = pal.CatColors[cat];
        var fill = isDir ? Mix(pal.Surface, catColor, 0.55) : catColor;
        var hover = Mix(fill, pal.TextPrimary, 0.18);
        var fg = ExactLuminance(fill) > 0.32 ? pal.DarkText : pal.LightText;
        var entry = ((Brush)FrozenBrush(fill), (Brush)FrozenBrush(hover), fg);
        pal.TileBrushes[key] = entry;
        return entry;
    }

    private void SelectTile(FsItem item, Border border)
    {
        if (_selectedBorder != null && !ReferenceEquals(_selectedBorder, border))
        {
            _selectedBorder.BorderThickness = new Thickness(1);
            _selectedBorder.BorderBrush = _edgeBrushCache ?? Brushes.Black;
        }
        _selected = item;
        _selectedBorder = border;
        border.BorderThickness = new Thickness(2);
        border.BorderBrush = _accentBrushCache ?? Brushes.Teal;
    }

    // ---------------------------------------------------------------- cizim

    public void RebuildTreemap()
    {
        Children.Clear();
        _selectedBorder = null;

        if (_currentItem == null || ActualWidth < 10 || ActualHeight < 10) return;

        var pal = BuildPalette();

        var raw = _currentItem.RawChildren;
        var children = raw != null && raw.Length > 0
            ? raw.Where(c => c.Size > 0).OrderByDescending(c => c.Size).ToList()
            : new List<FsItem>();

        if (children.Count == 0)
        {
            DrawTile(_currentItem, new Rect(4, 4, ActualWidth - 8, ActualHeight - 8), pal);
            return;
        }

        long totalSize = 0;
        for (int i = 0; i < children.Count; i++) totalSize += children[i].Size;
        if (totalSize <= 0) return;

        var rects = Squarify(children, new Rect(2, 2, ActualWidth - 4, ActualHeight - 4), totalSize);
        int drawn = 0;
        for (int i = 0; i < rects.Count && drawn < MaxRenderedTiles; i++)
        {
            var (item, rect) = rects[i];
            if (rect.Width < 3 || rect.Height < 3) continue;
            DrawTile(item, rect, pal);
            drawn++;
        }
    }

    private void DrawTile(FsItem item, Rect rect, PaletteCache pal)
    {
        if (rect.Width < 3 || rect.Height < 3) return;

        var cat = DominantCategory(item);
        var (fillBrush, hoverBrush, textBrush) = GetTileBrushes(pal, cat, item.IsDirectory);
        bool selected = ReferenceEquals(item, _selected);

        var border = new Border
        {
            Width = Math.Max(1, rect.Width - 2),
            Height = Math.Max(1, rect.Height - 2),
            Margin = new Thickness(1),
            BorderThickness = new Thickness(selected ? 2 : 1),
            BorderBrush = selected ? pal.AccentBrush : pal.EdgeBrush,
            Background = fillBrush,
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            DataContext = item,
            Tag = DataContext,
            ContextMenu = TileContextMenu,
            ToolTip = Strings.Format(Strings.TreemapTooltip, item.Name, item.SizeDisplay, item.AllocatedSizeDisplay, item.FileCount, item.FolderCount, item.FullPath)
        };
        if (selected) _selectedBorder = border;

        border.MouseEnter += (_, _) =>
        {
            border.Background = hoverBrush;
            if (!ReferenceEquals(item, _selected)) border.BorderBrush = textBrush;
        };
        border.MouseLeave += (_, _) =>
        {
            border.Background = fillBrush;
            border.BorderBrush = ReferenceEquals(item, _selected) ? pal.AccentBrush : pal.EdgeBrush;
        };

        border.MouseLeftButtonDown += (_, e) =>
        {
            Focus();
            if (e.ClickCount == 1)
            {
                SelectTile(item, border);
                ItemSelected?.Invoke(item);
            }
            else if (e.ClickCount == 2 && item.IsDirectory && item.HasChildren)
            {
                var cmd = ItemActivatedCommand;
                if (cmd != null && cmd.CanExecute(item)) cmd.Execute(item);
            }
            e.Handled = true;
        };
        border.MouseRightButtonDown += (_, _) =>
        {
            if (!ReferenceEquals(item, _selected))
            {
                SelectTile(item, border);
                ItemSelected?.Invoke(item);
            }
        };

        if (rect.Width > 56 && rect.Height > 34)
        {
            var stack = new StackPanel
            {
                Margin = new Thickness(6, 4, 6, 4),
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left,
                IsHitTestVisible = false,
                Width = Math.Max(10, rect.Width - 14)
            };

            var nameRow = new DockPanel { LastChildFill = true };
            if (item.IsDirectory)
            {
                var glyph = new TextBlock
                {
                    Text = pal.FolderGlyph,
                    FontSize = 12,
                    Foreground = textBrush,
                    Margin = new Thickness(0, 1, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                if (pal.IconFont != null) glyph.FontFamily = pal.IconFont;
                DockPanel.SetDock(glyph, Dock.Left);
                nameRow.Children.Add(glyph);
            }
            var nameText = new TextBlock
            {
                Text = item.Name,
                Foreground = textBrush,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            if (pal.UiFont != null) nameText.FontFamily = pal.UiFont;
            nameRow.Children.Add(nameText);
            stack.Children.Add(nameRow);

            var sizeText = new TextBlock { Text = item.SizeDisplay, Foreground = textBrush, Opacity = 0.85, FontSize = 11 };
            if (pal.MonoFont != null) sizeText.FontFamily = pal.MonoFont;
            stack.Children.Add(sizeText);

            if (rect.Height > 62)
            {
                var pctText = new TextBlock { Text = $"{item.PercentOfParent:F1}%", Foreground = textBrush, Opacity = 0.7, FontSize = 11 };
                if (pal.MonoFont != null) pctText.FontFamily = pal.MonoFont;
                stack.Children.Add(pctText);
            }

            border.Child = stack;
        }

        SetLeft(border, rect.X);
        SetTop(border, rect.Y);
        Children.Add(border);
    }

    private List<(FsItem item, Rect rect)> Squarify(List<FsItem> items, Rect bounds, long totalSize)
    {
        var result = new List<(FsItem, Rect)>(Math.Min(items.Count, MaxRenderedTiles));
        if (items.Count == 0 || bounds.Width <= 0 || bounds.Height <= 0) return result;

        double totalArea = bounds.Width * bounds.Height;
        var areas = new double[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            areas[i] = Math.Max(0.001, (double)items[i].Size / totalSize * totalArea);
        }

        int idx = 0;
        while (idx < items.Count && bounds.Width > 0 && bounds.Height > 0 && result.Count < MaxRenderedTiles)
        {
            int remaining = items.Count - idx;
            if (remaining == 1)
            {
                result.Add((items[idx], bounds));
                break;
            }

            bool horizontal = bounds.Width >= bounds.Height;
            double sideLength = horizontal ? bounds.Height : bounds.Width;
            if (sideLength <= 0) break;

            int rowCount = 1;
            double currentWorst = WorstAspect(areas, idx, rowCount, sideLength);

            while (rowCount < remaining)
            {
                double nextWorst = WorstAspect(areas, idx, rowCount + 1, sideLength);
                if (nextWorst > currentWorst) break;
                currentWorst = nextWorst;
                rowCount++;
            }

            double rowAreaSum = 0;
            for (int i = 0; i < rowCount; i++) rowAreaSum += areas[idx + i];

            double rowBreadth = rowAreaSum / sideLength;
            if (rowBreadth <= 0 || double.IsNaN(rowBreadth) || double.IsInfinity(rowBreadth)) break;

            double offset = 0;
            for (int i = 0; i < rowCount && result.Count < MaxRenderedTiles; i++)
            {
                double itemLength = areas[idx + i] / rowBreadth;
                if (itemLength < 0 || double.IsNaN(itemLength) || double.IsInfinity(itemLength)) continue;
                Rect itemRect = horizontal
                    ? new Rect(bounds.X, bounds.Y + offset, rowBreadth, itemLength)
                    : new Rect(bounds.X + offset, bounds.Y, itemLength, rowBreadth);

                result.Add((items[idx + i], itemRect));
                offset += itemLength;
            }

            bounds = horizontal
                ? new Rect(bounds.X + rowBreadth, bounds.Y, Math.Max(0, bounds.Width - rowBreadth), bounds.Height)
                : new Rect(bounds.X, bounds.Y + rowBreadth, bounds.Width, Math.Max(0, bounds.Height - rowBreadth));

            idx += rowCount;
        }
        return result;
    }

    private static double WorstAspect(double[] areas, int start, int count, double length)
    {
        if (count <= 0 || length <= 0) return double.MaxValue;
        double sum = 0;
        double max = double.MinValue;
        double min = double.MaxValue;
        for (int i = 0; i < count; i++)
        {
            double a = areas[start + i];
            sum += a;
            if (a > max) max = a;
            if (a < min) min = a;
        }
        if (sum <= 0 || min <= 0) return double.MaxValue;
        double lengthSq = length * length;
        double sumSq = sum * sum;
        return Math.Max((lengthSq * max) / sumSq, sumSq / (lengthSq * min));
    }
}
