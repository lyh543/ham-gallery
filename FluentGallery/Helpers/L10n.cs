using Microsoft.Windows.ApplicationModel.Resources;
using System.Globalization;

namespace FluentGallery.Helpers;

public static class L10n
{
    private static readonly ResourceLoader Loader = CreateLoader();

    private static ResourceLoader CreateLoader()
    {
        // WinUI 3 desktop apps should prefer the default loader instance.
        // Some view-independent paths can fail in app contexts during page/viewmodel init.
        return new ResourceLoader();
    }

    public static string Get(string key)
    {
        var value = Loader.GetString(key);
        return string.IsNullOrWhiteSpace(value) ? key : value;
    }

    public static string Format(string key, params object[] args)
    {
        var pattern = Get(key);
        return args.Length == 0
            ? pattern
            : string.Format(CultureInfo.CurrentCulture, pattern, args);
    }
}