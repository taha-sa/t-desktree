using AmberTree.Localization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AmberTree.Models;
using AmberTree.Services;
using AmberTree.ViewModels;

namespace AmberTree.Controls;

/// <summary>
/// Treemap cizimi. Renkler tema kaynaklarindan (Brush/Color.Cat.*, Bg.*) okunur; tema ya da birim degisince yeniden cizilir.
/// Gezinme VM komutlariyla yapilir (ItemActivatedCommand), tuvalin kendi gezinme durumu yoktur.
/// </summary>
public class TreemapCanvas : Canvas
{
    private FsItem? _currentItem;
    private FsItem? _selected;
    private MainWindowViewModel? _vm;
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
        SizeChanged += (_, _) => RebuildTreemap();
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

    private void Detach()
    {
        if (_vm != null) _vm.UnitChanged -= OnUnitChanged;
        _vm = null;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(RebuildTreemap));
    private void OnUnitChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(RebuildTreemap));

    public FsItem? CurrentItem => _currentItem;

    // ---------------------------------------------------------------- renk yardimcilari

    private Color ResColor(string key, Color fallback)
        => TryFindResource(key) is Color c ? c : fallback;

    private static Color Mix(Color a, Color b, double t)
        => Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));

    private static double Luminance(Color c)
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
            foreach (var c in n.Children)
            {
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

    // ---------------------------------------------------------------- cizim

    public void RebuildTreemap()
    {
        Children.Clear();

        if (_currentItem == null || ActualWidth < 10 || ActualHeight < 10) return;

        var children = _currentItem.Children.Where(c => c.Size > 0).OrderByDescending(c => c.Size).ToList();
        if (children.Count == 0)
        {
            DrawTile(_currentItem, new Rect(4, 4, ActualWidth - 8, ActualHeight - 8));
            return;
        }

        long totalSize = children.Sum(c => c.Size);
        if (totalSize <= 0) return;

        var rects = Squarify(children, new Rect(2, 2, ActualWidth - 4, ActualHeight - 4), totalSize);
        foreach (var (item, rect) in rects) DrawTile(item, rect);
    }

    private void DrawTile(FsItem item, Rect rect)
    {
        if (rect.Width < 3 || rect.Height < 3) return;

        var baseColor = ResColor("Color.Bg.Base", Colors.Black);
        var surface = ResColor("Color.Bg.Surface", Colors.Gray);
        var cat = DominantCategory(item);
        var catColor = ResColor(FileCategoryMap.ColorKey(cat), Colors.Gray);

        // Klasor karolari daha sonuk (kategori rengi zemine karisir), dosyalar tam renk.
        var fill = item.IsDirectory ? Mix(surface, catColor, 0.55) : catColor;
        var hoverFill = Mix(fill, ResColor("Color.Text.Primary", Colors.White), 0.18);
        var fg = Luminance(fill) > 0.32 ? Colors.Black : Colors.White;
        bool selected = ReferenceEquals(item, _selected);

        var fillBrush = new SolidColorBrush(fill);
        var hoverBrush = new SolidColorBrush(hoverFill);
        var edgeBrush = new SolidColorBrush(baseColor);
        var accentBrush = new SolidColorBrush(ResColor("Color.Accent", Colors.Teal));
        var textBrush = new SolidColorBrush(fg);

        var border = new Border
        {
            Width = Math.Max(1, rect.Width - 2),
            Height = Math.Max(1, rect.Height - 2),
            Margin = new Thickness(1),
            BorderThickness = new Thickness(selected ? 2 : 1),
            BorderBrush = selected ? accentBrush : edgeBrush,
            Background = fillBrush,
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            DataContext = item,
            Tag = DataContext,
            ContextMenu = TileContextMenu,
            ToolTip = Strings.Format(Strings.TreemapTooltip, item.Name, item.SizeDisplay, item.AllocatedSizeDisplay, item.FileCount, item.FolderCount, item.FullPath)
        };

        border.MouseEnter += (_, _) =>
        {
            border.Background = hoverBrush;
            if (!ReferenceEquals(item, _selected)) border.BorderBrush = textBrush;
        };
        border.MouseLeave += (_, _) =>
        {
            border.Background = fillBrush;
            border.BorderBrush = ReferenceEquals(item, _selected) ? accentBrush : edgeBrush;
        };

        border.MouseLeftButtonDown += (_, e) =>
        {
            Focus();
            if (e.ClickCount == 1)
            {
                _selected = item;
                ItemSelected?.Invoke(item);
                RebuildTreemap();
            }
            else if (e.ClickCount == 2 && item.IsDirectory && item.Children.Count > 0)
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
                _selected = item;
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
                IsHitTestVisible = false
            };

            var nameRow = new DockPanel { LastChildFill = true };
            if (item.IsDirectory)
            {
                var glyph = new TextBlock { Text = (string)(TryFindResource("Icon.Folder") ?? string.Empty), FontSize = 12, Foreground = textBrush, Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center };
                glyph.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icons");
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
            nameText.SetResourceReference(TextBlock.FontFamilyProperty, "Font.UI");
            nameRow.Children.Add(nameText);
            stack.Children.Add(nameRow);
            stack.Width = Math.Max(10, rect.Width - 14);

            var sizeText = new TextBlock { Text = item.SizeDisplay, Foreground = textBrush, Opacity = 0.85, FontSize = 11 };
            sizeText.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Mono");
            stack.Children.Add(sizeText);

            if (rect.Height > 62)
            {
                var pctText = new TextBlock { Text = $"{item.PercentOfParent:F1}%", Foreground = textBrush, Opacity = 0.7, FontSize = 11 };
                pctText.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Mono");
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
        var result = new List<(FsItem, Rect)>();
        if (items.Count == 0 || bounds.Width <= 0 || bounds.Height <= 0) return result;

        double totalArea = bounds.Width * bounds.Height;
        var areas = items.Select(it => Math.Max(0.001, (double)it.Size / totalSize * totalArea)).ToList();

        int idx = 0;
        while (idx < items.Count && bounds.Width > 0 && bounds.Height > 0)
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
            for (int i = 0; i < rowCount; i++)
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

    private static double WorstAspect(List<double> areas, int start, int count, double length)
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
