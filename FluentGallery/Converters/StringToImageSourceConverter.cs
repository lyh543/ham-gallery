using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FluentGallery.Converters;

/// <summary>
/// Converts an absolute file-system path string into a <see cref="BitmapImage"/>.
/// Returns null when the path is empty or the file does not exist.
/// </summary>
public sealed class StringToImageSourceConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string rawPath || string.IsNullOrEmpty(rawPath))
            return null;

        var filePath = rawPath.Split('#')[0];
        if (!File.Exists(filePath))
            return null;

        try
        {
            return new BitmapImage
            {
                CreateOptions = BitmapCreateOptions.IgnoreImageCache,
                UriSource = new Uri(filePath)
            };
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
