using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentGallery.Controls;

public sealed partial class AppIcon : UserControl
{
    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(
            nameof(Kind),
            typeof(AppIconKind),
            typeof(AppIcon),
            new PropertyMetadata(AppIconKind.RotateLeft, OnIconPropertyChanged));

    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(
            nameof(IconSize),
            typeof(double),
            typeof(AppIcon),
            new PropertyMetadata(13d));

    public AppIcon()
    {
        InitializeComponent();
        UpdateVisualState();
    }

    public AppIconKind Kind
    {
        get => (AppIconKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public Uri IconUri { get; private set; } = new(AppIconCatalog.RotateAssetPath);

    public double MirrorScaleX { get; private set; } = 1d;

    private static void OnIconPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((AppIcon)d).UpdateVisualState();
    }

    private void UpdateVisualState()
    {
        IconUri = new Uri(AppIconCatalog.GetAssetPath(Kind));
        MirrorScaleX = AppIconCatalog.ShouldMirror(Kind) ? -1d : 1d;
    }
}