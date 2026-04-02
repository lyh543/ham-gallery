using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace FluentGallery.Converters;

/// <summary>Converts <see cref="bool"/> to <see cref="Visibility"/>.</summary>
/// <remarks>Pass <c>True</c> as ConverterParameter to invert the result.</remarks>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool flag = value is bool b && b;
        if (parameter is string s && s == "True") flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is Visibility v && v == Visibility.Visible;
}
