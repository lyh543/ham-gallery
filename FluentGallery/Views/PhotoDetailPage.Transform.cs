using System.Threading.Tasks;

namespace FluentGallery.Views;

public sealed partial class PhotoDetailPage
{
    private double _rotationAngle = 0.0;

    private async void RotateCw_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        _rotationAngle = (_rotationAngle + 90) % 360;
        ZoomImage.RotationAngle = _rotationAngle;
        await ViewModel.RotateAsync(clockwise: true, _cts.Token);
    }

    private async void RotateCcw_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        _rotationAngle = (_rotationAngle - 90 + 360) % 360;
        ZoomImage.RotationAngle = _rotationAngle;
        await ViewModel.RotateAsync(clockwise: false, _cts.Token);
    }
}
