namespace FluentGallery.Services;

/// <summary>
/// Applies a requested UI theme to the running application window.
/// Abstracted so that <c>SettingsViewModel</c> does not depend directly on WinUI types,
/// which makes the ViewModel unit-testable without a WinUI runtime.
/// </summary>
public interface IThemeService
{
    /// <summary>
    /// Changes the application theme at runtime.
    /// </summary>
    /// <param name="theme">0 = follow system, 1 = light, 2 = dark.</param>
    void Apply(int theme);
}
