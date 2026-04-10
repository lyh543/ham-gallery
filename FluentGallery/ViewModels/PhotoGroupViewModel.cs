using System.Collections.ObjectModel;

namespace FluentGallery.ViewModels;

/// <summary>
/// Represents a time-line group (year/month) of photos for the AllPhotosPage.
/// The Key is formatted as "YYYY年MM月" (e.g., "2024年12月") or "未知日期" for photos without dates.
/// </summary>
public sealed class PhotoGroupViewModel
{
    public string Key { get; }
    public ObservableCollection<PhotoItemViewModel> Photos { get; }

    public PhotoGroupViewModel(string key)
    {
        Key = key;
        Photos = new ObservableCollection<PhotoItemViewModel>();
    }
}
