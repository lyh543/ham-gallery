using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace FluentGallery.Views;

public sealed partial class PhotoDetailPage
{
    private double _rotationAngle = 0.0;

    private async void RotateCw_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var path = ViewModel.CurrentImagePath;
        _logger.LogDebug("Rotate click CW: path={Path}, visualAngleBefore={Angle}", path, _rotationAngle);
        if (!string.IsNullOrEmpty(path))
        {
            _wicLoader.InvalidatePath(path);
            _magickLoader.InvalidatePath(path);
        }

        _rotationAngle = (_rotationAngle + 90) % 360;
        ZoomImage.RotationAngle = _rotationAngle;
        _logger.LogDebug("Rotate click CW visual angle applied: path={Path}, visualAngleAfter={Angle}", path, _rotationAngle);
        await ViewModel.RotateAsync(clockwise: true, _cts.Token);
    }

    private async void RotateCcw_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var path = ViewModel.CurrentImagePath;
        _logger.LogDebug("Rotate click CCW: path={Path}, visualAngleBefore={Angle}", path, _rotationAngle);
        if (!string.IsNullOrEmpty(path))
        {
            _wicLoader.InvalidatePath(path);
            _magickLoader.InvalidatePath(path);
        }

        _rotationAngle = (_rotationAngle - 90 + 360) % 360;
        ZoomImage.RotationAngle = _rotationAngle;
        _logger.LogDebug("Rotate click CCW visual angle applied: path={Path}, visualAngleAfter={Angle}", path, _rotationAngle);
        await ViewModel.RotateAsync(clockwise: false, _cts.Token);
    }

    internal void ResetRotation()
    {
        _rotationAngle = 0.0;
        ZoomImage.RotationAngle = 0.0;
    }
}
