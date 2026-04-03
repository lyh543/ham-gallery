namespace FluentGallery.Models;

/// <summary>
/// Navigation parameter passed from <see cref="FluentGallery.Views.PhotoListPage"/>
/// (and AllPhotosPage) to <see cref="FluentGallery.Views.PhotoDetailPage"/>.
/// Carries the ordered list of photos in the current view and the index of the
/// photo the user tapped, so the detail page can support swipe-to-next/previous.
/// </summary>
public sealed record PhotoDetailNavigationArgs(IReadOnlyList<Photo> Photos, int StartIndex);
