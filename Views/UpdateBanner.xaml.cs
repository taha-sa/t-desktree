using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using AmberTree.Services;

namespace AmberTree.Views;

/// <summary>Güncelleme afişi: yalnızca 150 ms aşağı kayma animasyonu; mantık UpdateBannerViewModel'de.</summary>
public partial class UpdateBanner : UserControl
{
    private int _animToken;   // yalnız en son animasyonun Completed'ı Collapse eder

    public UpdateBanner()
    {
        InitializeComponent();
        DataContext = UpdateBannerViewModel.Instance;
        UpdateBannerViewModel.Instance.PropertyChanged += OnChanged;
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(UpdateBannerViewModel.IsBannerVisible)) return;
        int token = ++_animToken;
        if (UpdateBannerViewModel.Instance.IsBannerVisible)
        {
            Root.Visibility = Visibility.Visible;
            Slide.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        else
        {
            var close = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150));
            close.Completed += (_, _) => { if (token == _animToken) Root.Visibility = Visibility.Collapsed; };
            Slide.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, close);
        }
    }
}
