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
        if (value is not string path || string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;

        try
        {
            // new Uri(absolutePath) produces a file:/// URI that BitmapImage accepts.
            return new BitmapImage(new Uri(path));
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
