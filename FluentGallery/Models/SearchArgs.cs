namespace FluentGallery.Models;

/// <summary>
/// Navigation parameter passed to <c>SearchPage</c>.
/// When <see cref="AlbumId"/> is set the search is scoped to that album;
/// otherwise it searches across the entire library.
/// </summary>
public sealed record SearchArgs(long? AlbumId = null, string? AlbumName = null);
