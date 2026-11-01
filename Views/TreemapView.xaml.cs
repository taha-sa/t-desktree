using System;
using System.Windows;
using System.Windows.Controls;
using AmberTree.Models;
using AmberTree.Services;
using AmberTree.ViewModels;

namespace AmberTree.Views;

/// <summary>Treemap gorunumu: kategori lejantini olusturur ve karo secimini VM'e aktarir (olay koprusu).</summary>
public partial class TreemapView : UserControl
{
    public TreemapView()
    {
        InitializeComponent();
        BuildLegend();
    }

    private void BuildLegend()
    {
        foreach (FileCategory cat in Enum.GetValues<FileCategory>())
        {
            var swatch = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center };
            swatch.SetResourceReference(Border.BackgroundProperty, FileCategoryMap.BrushKey(cat));

            var label = new TextBlock { Text = FileCategoryMap.DisplayName(cat), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(FrameworkElement.StyleProperty, "Text.Caption");

            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 2, 0, 2) };
            item.Children.Add(swatch);
            item.Children.Add(label);
            Legend.Children.Add(item);
        }
    }

    private void Map_ItemSelected(FsItem item)
    {
        if (DataContext is MainWindowViewModel vm) vm.SelectedItem = item;
    }
}
