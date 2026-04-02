using CommunityToolkit.Mvvm.ComponentModel;
using FluentGallery.Models;

namespace FluentGallery.ViewModels;

/// <summary>
/// Per-item ViewModel that wraps an <see cref="Album"/> for display in the album grid.
/// Supports inline rename (IsEditing flag) and observable pin state.
/// </summary>
public sealed partial class AlbumItemViewModel : ObservableObject
{
    public long Id { get; }

    [ObservableProperty] public partial string Name       { get; set; }
    [ObservableProperty] public partial int    PhotoCount { get; set; }
    [ObservableProperty] public partial string ModifiedAt { get; set; }
    [ObservableProperty] public partial bool   IsPinned   { get; set; }
    [ObservableProperty] public partial bool   IsEditing  { get; set; }
    [ObservableProperty] public partial string EditName   { get; set; }

    public AlbumItemViewModel(Album album)
    {
        Id         = album.Id;
        Name       = album.Name;
        PhotoCount = album.PhotoCount;
        ModifiedAt = album.ModifiedAt;
        IsPinned   = album.IsPinned;
        EditName   = string.Empty;
    }

    public void BeginEdit()
    {
        EditName  = Name;
        IsEditing = true;
    }

    public string CommitEdit()
    {
        IsEditing = false;
        Name      = EditName.Trim();
        return Name;
    }

    public void CancelEdit() => IsEditing = false;
}
