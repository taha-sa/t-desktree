using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AmberTree.Converters;
using AmberTree.Models;

namespace AmberTree.Controls;

public class TreemapCanvas : Canvas
{
    private FsItem? _currentItem;
    public event Action<FsItem>? ItemDoubleClicked;
    public event Action<FsItem>? ItemSelected;

    public static readonly DependencyProperty SourceItemProperty =
        DependencyProperty.Register(
            nameof(SourceItem),
            typeof(FsItem),
            typeof(TreemapCanvas),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSourceItemChanged));

    public FsItem? SourceItem
    {
        get => (FsItem?)GetValue(SourceItemProperty);
        set => SetValue(SourceItemProperty, value);
    }

    private static void OnSourceItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TreemapCanvas canvas)
        {
            canvas._currentItem = e.NewValue as FsItem;
            canvas.RebuildTreemap();
        }
    }

    public TreemapCanvas()
    {
        ClipToBounds = true;
        Background = new SolidColorBrush(Color.FromRgb(15, 15, 18));
        SizeChanged += (s, e) => RebuildTreemap();
    }

    public void NavigateTo(FsItem item)
    {
        _currentItem = item;
        RebuildTreemap();
    }

    public void NavigateUp()
    {
        if (_currentItem?.Parent != null)
        {
            _currentItem = _currentItem.Parent;
            RebuildTreemap();
        }
    }

    public FsItem? CurrentItem => _currentItem;

    public void RebuildTreemap()
    {
        Children.Clear();

        if (_currentItem == null || ActualWidth < 10 || ActualHeight < 10) return;

        var children = _currentItem.Children.Where(c => c.Size > 0).ToList();
        if (children.Count == 0)
        {
            // Single leaf rectangle
            DrawLeaf(_currentItem, new Rect(4, 4, ActualWidth - 8, ActualHeight - 8), 0);
            return;
        }

        double totalArea = ActualWidth * ActualHeight;
        long totalSize = children.Sum(c => c.Size);
        if (totalSize <= 0) return;

        var rects = Squarify(children, new Rect(2, 2, ActualWidth - 4, ActualHeight - 4), totalSize);

        for (int i = 0; i < rects.Count; i++)
        {
            var (item, rect) = rects[i];
            DrawTile(item, rect, i);
        }
    }

    private void DrawLeaf(FsItem item, Rect rect, int index)
    {
        DrawTile(item, rect, index);
    }

    private void DrawTile(FsItem item, Rect rect, int index)
    {
        if (rect.Width < 2 || rect.Height < 2) return;

        var border = new Border
        {
            Width = rect.Width,
            Height = rect.Height,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            ClipToBounds = true
        };

        // Industrial Yellow & Black Heatmap Gradient
        // Larger items get brighter amber/yellow border and subtle tint
        double ratio = _currentItem != null && _currentItem.Size > 0 ? (double)item.Size / _currentItem.Size : 0.5;
        byte yellowIntensity = (byte)Math.Clamp(120 + ratio * 135, 120, 255);
        byte bgLuminance = (byte)Math.Clamp(18 + ratio * 28, 18, 55);

        var borderColor = Color.FromRgb(yellowIntensity, (byte)(yellowIntensity * 0.85), (byte)(yellowIntensity * 0.1));
        var bgColor = Color.FromRgb(bgLuminance, (byte)(bgLuminance * 0.95), (byte)(bgLuminance * 0.6));

        border.BorderBrush = new SolidColorBrush(borderColor);
        border.Background = new SolidColorBrush(bgColor);

        // Hover effect
        border.MouseEnter += (s, e) =>
        {
            border.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 230, 0));
            border.Background = new SolidColorBrush(Color.FromRgb(55, 48, 15));
        };
        border.MouseLeave += (s, e) =>
        {
            border.BorderBrush = new SolidColorBrush(borderColor);
            border.Background = new SolidColorBrush(bgColor);
        };

        // Click selection / drill down
        border.MouseDown += (s, e) =>
        {
            if (e.ClickCount == 1)
            {
                ItemSelected?.Invoke(item);
            }
            else if (e.ClickCount == 2 && item.IsDirectory && item.Children.Count > 0)
            {
                NavigateTo(item);
                ItemDoubleClicked?.Invoke(item);
            }
        };

        // Tooltip
        border.ToolTip = new ToolTip
        {
            Content = $"{item.Name}\nSize: {SizeFormatter.FormatBytes(item.Size)}\nAllocated: {SizeFormatter.FormatBytes(item.AllocatedSize)}\nFiles: {item.FileCount:N0}\nFolders: {item.FolderCount:N0}\nPath: {item.FullPath}",
            Background = new SolidColorBrush(Color.FromRgb(24, 24, 28)),
            Foreground = new SolidColorBrush(Color.FromRgb(255, 230, 0)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(255, 214, 0)),
            BorderThickness = new Thickness(1)
        };

        // Text inside tile if large enough
        if (rect.Width > 55 && rect.Height > 35)
        {
            var stack = new StackPanel
            {
                Margin = new Thickness(4),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var nameText = new TextBlock
            {
                Text = item.Name,
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                FontSize = Math.Clamp(rect.Width / 12, 10, 14),
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var sizeText = new TextBlock
            {
                Text = SizeFormatter.FormatBytes(item.Size),
                Foreground = new SolidColorBrush(Color.FromRgb(255, 214, 0)),
                FontSize = Math.Clamp(rect.Width / 14, 9, 12),
                HorizontalAlignment = HorizontalAlignment.Center
            };

            stack.Children.Add(nameText);
            stack.Children.Add(sizeText);

            if (rect.Height > 60)
            {
                var pctText = new TextBlock
                {
                    Text = $"{item.PercentOfParent:F1}%",
                    Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 190)),
                    FontSize = 10,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                stack.Children.Add(pctText);
            }

            border.Child = stack;
        }

        Canvas.SetLeft(border, rect.X);
        Canvas.SetTop(border, rect.Y);
        Children.Add(border);
    }

    private List<(FsItem item, Rect rect)> Squarify(List<FsItem> items, Rect bounds, long totalSize)
    {
        var result = new List<(FsItem, Rect)>();
        if (items.Count == 0 || bounds.Width <= 0 || bounds.Height <= 0) return result;

        double totalArea = bounds.Width * bounds.Height;
        var areas = items.Select(it => Math.Max(0.001, (double)it.Size / totalSize * totalArea)).ToList();

        SquarifyRecursive(items, areas, bounds, result);
        return result;
    }

    private void SquarifyRecursive(List<FsItem> items, List<double> areas, Rect bounds, List<(FsItem, Rect)> result)
    {
        if (items.Count == 0 || bounds.Width <= 0 || bounds.Height <= 0) return;

        if (items.Count == 1)
        {
            result.Add((items[0], bounds));
            return;
        }

        bool horizontal = bounds.Width >= bounds.Height;
        double sideLength = horizontal ? bounds.Height : bounds.Width;

        int rowCount = 1;
        double currentWorst = WorstAspect(areas.Take(rowCount).ToList(), sideLength);

        while (rowCount < items.Count)
        {
            double nextWorst = WorstAspect(areas.Take(rowCount + 1).ToList(), sideLength);
            if (nextWorst > currentWorst) break;
            currentWorst = nextWorst;
            rowCount++;
        }

        var rowItems = items.Take(rowCount).ToList();
        var rowAreas = areas.Take(rowCount).ToList();
        double rowAreaSum = rowAreas.Sum();

        double rowBreadth = rowAreaSum / sideLength;
        double offset = 0;

        foreach (var (item, area) in rowItems.Zip(rowAreas))
        {
            double itemLength = area / rowBreadth;
            Rect itemRect = horizontal
                ? new Rect(bounds.X, bounds.Y + offset, rowBreadth, itemLength)
                : new Rect(bounds.X + offset, bounds.Y, itemLength, rowBreadth);

            result.Add((item, itemRect));
            offset += itemLength;
        }

        Rect remainingBounds = horizontal
            ? new Rect(bounds.X + rowBreadth, bounds.Y, Math.Max(0, bounds.Width - rowBreadth), bounds.Height)
            : new Rect(bounds.X, bounds.Y + rowBreadth, bounds.Width, Math.Max(0, bounds.Height - rowBreadth));

        SquarifyRecursive(items.Skip(rowCount).ToList(), areas.Skip(rowCount).ToList(), remainingBounds, result);
    }

    private double WorstAspect(List<double> areas, double length)
    {
        if (areas.Count == 0 || length <= 0) return double.MaxValue;
        double sum = areas.Sum();
        if (sum <= 0) return double.MaxValue;
        double max = areas.Max();
        double min = areas.Min();
        double lengthSq = length * length;
        double sumSq = sum * sum;
        return Math.Max((lengthSq * max) / sumSq, sumSq / (lengthSq * min));
    }
}
