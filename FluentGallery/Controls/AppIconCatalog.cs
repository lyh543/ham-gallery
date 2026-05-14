namespace FluentGallery.Controls;

public static class AppIconCatalog
{
    public const string RotateAssetPath = "ms-appx:///Assets/Icons/rotate.png";

    public static IReadOnlyList<AppIconKind> All { get; } =
    [
        AppIconKind.RotateLeft,
        AppIconKind.RotateRight,
    ];

    public static string GetAssetPath(AppIconKind kind) => kind switch
    {
        AppIconKind.RotateLeft => RotateAssetPath,
        AppIconKind.RotateRight => RotateAssetPath,
        _ => RotateAssetPath,
    };

    public static bool ShouldMirror(AppIconKind kind) => kind switch
    {
        AppIconKind.RotateLeft => true,
        AppIconKind.RotateRight => false,
        _ => false,
    };
}