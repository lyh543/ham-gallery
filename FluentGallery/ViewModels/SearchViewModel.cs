using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentGallery.Data;
using System.Collections.ObjectModel;

namespace FluentGallery.ViewModels;

/// <summary>ViewModel for the global / album-scoped search page.</summary>
public sealed partial class SearchViewModel : ObservableObject
{
    private readonly DatabaseService _db;

    // ── Scope ─────────────────────────────────────────────────────────────────

    [ObservableProperty] public partial long?   AlbumId   { get; set; }
    [ObservableProperty] public partial string? AlbumName { get; set; }

    // ── Search inputs ─────────────────────────────────────────────────────────

    [ObservableProperty] public partial string          Keyword    { get; set; } = string.Empty;

    /// <summary>Which date field to filter: "TakenAt", "ModifiedAt", "CreatedAt".</summary>
    [ObservableProperty] public partial string          DateField  { get; set; } = "TakenAt";

    [ObservableProperty] public partial DateTimeOffset? DateFrom   { get; set; }
    [ObservableProperty] public partial DateTimeOffset? DateTo     { get; set; }

    // ── State ─────────────────────────────────────────────────────────────────

    [ObservableProperty] public partial bool IsLoading  { get; set; }
    [ObservableProperty] public partial bool HasSearched { get; set; }
    [ObservableProperty] public partial int  ColumnCount { get; set; } = 4;

    // ── Results ───────────────────────────────────────────────────────────────

    public ObservableCollection<PhotoItemViewModel> Results { get; } = new();

    // ── Constructor ───────────────────────────────────────────────────────────

    public SearchViewModel(DatabaseService db) => _db = db;

    // ── Search ────────────────────────────────────────────────────────────────

    [RelayCommand]
    public async Task SearchAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        Results.Clear();

        try
        {
            var dateFromStr = DateFrom.HasValue
                ? DateFrom.Value.Date.ToString("yyyy-MM-dd")
                : null;
            var dateToStr = DateTo.HasValue
                ? DateTo.Value.Date.ToString("yyyy-MM-dd")
                : null;

            var photos = await _db.SearchPhotosAsync(
                keyword  : string.IsNullOrWhiteSpace(Keyword) ? null : Keyword.Trim(),
                dateField: DateField,
                dateFrom : dateFromStr,
                dateTo   : dateToStr,
                albumId  : AlbumId,
                ct       : ct);

            foreach (var p in photos)
                Results.Add(new PhotoItemViewModel(p));
        }
        finally
        {
            IsLoading  = false;
            HasSearched = true;
        }
    }

    // ── Column count (pinch gesture) ──────────────────────────────────────────

    public void AdjustColumnCount(int delta)
        => ColumnCount = Math.Clamp(ColumnCount + delta, 2, 8);

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>True when both keyword and date range are empty (nothing to search).</summary>
    public bool IsQueryEmpty
        => string.IsNullOrWhiteSpace(Keyword)
           && DateFrom is null
           && DateTo   is null;
}
