using CommunityToolkit.Mvvm.ComponentModel;
using FluentGallery.Data;
using FluentGallery.Helpers;
using FluentGallery.Models;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Serilog;
using System.Globalization;

namespace FluentGallery.ViewModels;

/// <summary>
/// Per-photo item ViewModel used by <see cref="PhotoListPage"/> and <see cref="AllPhotosPage"/>.
/// Holds a reference to the underlying <see cref="Photo"/> model and manages lazy thumbnail loading.
/// </summary>
public sealed partial class PhotoItemViewModel : ObservableObject
{
    private readonly Photo _photo;
    private string? _preferredThumbnailPath;

    // ── Read-only accessors ───────────────────────────────────────────────────

    public long    Id         => _photo.Id;
    public string  FilePath   => _photo.FilePath;
    public string  FileName   => _photo.FileName;
    public long    FileSize   => _photo.FileSize;
    public int?    Width      => _photo.Width;
    public int?    Height     => _photo.Height;
    public string? TakenAt    => _photo.TakenAt;
    public string  CreatedAt  => _photo.CreatedAt;
    public string  ModifiedAt => _photo.ModifiedAt;
    public long?   AlbumId    => _photo.AlbumId;

    public string TakenAtFormatted => FormatIsoDate(TakenAt) ?? L10n.Get("PhotoList_Metadata_Unknown");
    public string TakenAtTooltipText => FormatTakenAtTooltip(TakenAtFormatted);
    public string FileSizeFormatted => FormatFileSize(FileSize);
    public string ResolutionFormatted => Width is > 0 && Height is > 0
        ? $"{Width} × {Height}"
        : L10n.Get("PhotoList_Metadata_Unknown");

    // ── Observable state ─────────────────────────────────────────────────────

    [ObservableProperty] public partial ImageSource? ThumbnailSource { get; set; }
    [ObservableProperty] public partial bool         IsLoading       { get; set; }

    public PhotoItemViewModel(Photo photo) => _photo = photo;

    /// <summary>Returns the underlying <see cref="Photo"/> (for sort and DB operations).</summary>
    internal Photo GetPhoto() => _photo;

    internal void UpdateFileLocation(string filePath, string fileName, long? albumId, string modifiedAt)
    {
        _photo.FilePath = filePath;
        _photo.FileName = fileName;
        _photo.AlbumId = albumId;
        _photo.ModifiedAt = modifiedAt;

        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(AlbumId));
        OnPropertyChanged(nameof(ModifiedAt));
    }

    // ── Thumbnail loading ─────────────────────────────────────────────────────

    /// <summary>
    /// Loads the thumbnail from the cache (or generates it) and sets
    /// <see cref="ThumbnailSource"/>. Safe to call multiple times — subsequent calls
    /// are no-ops if the thumbnail is already loaded or currently loading.
    /// Must be called on the UI thread so that <see cref="BitmapImage"/> is created
    /// on the correct dispatcher.
    /// </summary>
    public async Task LoadThumbnailAsync(ThumbnailService thumbService, CancellationToken ct = default)
    {
        if (ThumbnailSource is not null || IsLoading) return;
        IsLoading = true;
        bool cancelled = false;
        try
        {
            var path = _preferredThumbnailPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                path = await Task.Run(() => thumbService.GetOrCreateThumbnailAsync(_photo, ct), ct);

            // GIF thumbnails are disabled (ThumbnailDisabled=true in DB); fall back
            // to the source file so the animated GIF displays in the grid.
            var displayPath = path ?? (string.Equals(
                Path.GetExtension(_photo.FilePath), ".gif",
                StringComparison.OrdinalIgnoreCase) ? _photo.FilePath : null);

            if (displayPath is null || !File.Exists(displayPath)) return;

            _preferredThumbnailPath = path;
            ThumbnailSource = CreateThumbnailSource(displayPath);
        }
        catch (OperationCanceledException)
        {
            // Page is navigating away. Skip IsLoading = false to avoid firing PropertyChanged
            // into a torn-down XAML binding, which crashes via DispatcherQueueSynchronizationContext.
            cancelled = true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "LoadThumbnailAsync failed for {Path}", _photo.FilePath);
        }
        finally
        {
            if (!cancelled)
                IsLoading = false;
        }
    }

    /// <summary>Creates an <see cref="ImageSource"/> for a thumbnail at the given path.</summary>
    private static ImageSource CreateThumbnailSource(string path) =>
        new BitmapImage
        {
            CreateOptions = BitmapCreateOptions.IgnoreImageCache,
            UriSource = new Uri(path)
        };

    public void RefreshThumbnail(string? thumbPath, string modifiedAt)
    {
        _photo.ModifiedAt = modifiedAt;
        OnPropertyChanged(nameof(ModifiedAt));

        var displayPath = thumbPath ?? (string.Equals(
            Path.GetExtension(_photo.FilePath), ".gif",
            StringComparison.OrdinalIgnoreCase) ? _photo.FilePath : null);

        _preferredThumbnailPath = thumbPath;

        if (displayPath is null || !File.Exists(displayPath))
            return;

        var old = ThumbnailSource;
        ThumbnailSource = CreateThumbnailSource(displayPath);
        if (old is IDisposable disposable)
        {
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()
                ?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () => { try { disposable.Dispose(); } catch { } });
        }
    }

    /// <summary>Clears the loaded thumbnail (called when an item is recycled by the GridView).</summary>
    public void ClearThumbnail()
    {
        var old = ThumbnailSource;
        ThumbnailSource = null;
        if (old is IDisposable d)
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()
                ?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () => { try { d.Dispose(); } catch { } });
    }

    private static string? FormatIsoDate(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return null;
        return DateTime.TryParse(iso, null, DateTimeStyles.RoundtripKind, out var dt)
            ? dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)
            : iso;
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        if (bytes >= 1024 * 1024)
            return $"{bytes / (1024.0 * 1024):F1} MB";
        if (bytes >= 1024)
            return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }

    private static string FormatTakenAtTooltip(string value)
    {
        var unknown = L10n.Get("PhotoList_Metadata_Unknown");
        if (string.Equals(value, unknown, StringComparison.CurrentCulture))
            return value;

        return L10n.Format("PhotoList_Metadata_TakenAt", value);
    }
}
