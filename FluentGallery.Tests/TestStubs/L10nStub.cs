// Test stub: satisfies SettingsViewModel's L10n references without requiring WinUI ResourceLoader.
namespace FluentGallery.Helpers;

internal static class L10n
{
    public static string Get(string key) => key;
    public static string Format(string key, params object?[] args) => key;
}
