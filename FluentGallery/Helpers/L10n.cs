using Microsoft.Windows.ApplicationModel.Resources;
using System.Globalization;

namespace FluentGallery.Helpers;

public static class L10n
{
    private static readonly ResourceLoader Loader = CreateLoader();
    private static readonly ResourceManager ResourceManager = new();
    private static readonly ResourceContext EnglishContext = CreateEnglishContext();

    private static ResourceLoader CreateLoader()
    {
        // WinUI 3 desktop apps should prefer the default loader instance.
        // Some view-independent paths can fail in app contexts during page/viewmodel init.
        return new ResourceLoader();
    }

    private static ResourceContext CreateEnglishContext()
    {
        var context = ResourceManager.CreateResourceContext();
        context.QualifierValues["Language"] = "en-US";
        return context;
    }

    private static string? GetEnglishFallback(string key)
    {
        try
        {
            // Depending on projection/runtime version, keys may be present as
            // "Key" or "Resources/Key" in the main resource map.
            var candidate = ResourceManager.MainResourceMap.TryGetValue(key, EnglishContext)
                         ?? ResourceManager.MainResourceMap.TryGetValue($"Resources/{key}", EnglishContext);

            var value = candidate?.ValueAsString;
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    public static string Get(string key)
    {
        var value = Loader.GetString(key);
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        var fallback = GetEnglishFallback(key);
        return string.IsNullOrWhiteSpace(fallback) ? key : fallback;
    }

    public static string Format(string key, params object[] args)
    {
        var pattern = Get(key);
        return args.Length == 0
            ? pattern
            : string.Format(CultureInfo.CurrentCulture, pattern, args);
    }
}