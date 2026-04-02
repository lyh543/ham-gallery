using Microsoft.UI.Xaml;

namespace FluentGallery.Services;

/// <summary>
/// WinUI 3 implementation of <see cref="IThemeService"/>.
/// Changes <see cref="FrameworkElement.RequestedTheme"/> on the main window's root element,
/// which propagates the theme to all descendant controls.
/// </summary>
public sealed class WinUiThemeService : IThemeService
{
    public void Apply(int theme)
    {
        var requested = theme switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        if (App.Current.MainWindow?.Content is FrameworkElement root)
            root.RequestedTheme = requested;
    }
}
