using FluentGallery.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace FluentGallery.Data;

/// <summary>
/// Application-level data access facade built on top of <see cref="GalleryDbContext"/>.
/// Uses <see cref="IDbContextFactory{GalleryDbContext}"/> so every operation gets its own
/// short-lived context — safe for concurrent background threads.
/// </summary>
public sealed class DatabaseService
{
    private readonly IDbContextFactory<GalleryDbContext> _factory;
    private readonly ILogger<DatabaseService> _logger;

    public DatabaseService(
        IDbContextFactory<GalleryDbContext> factory,
        ILogger<DatabaseService> logger)
    {
        _factory = factory;
        _logger  = logger;
    }

    // ────────────────────────────────────────────────────────────────────────
    // Initialisation
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates (or validates) the database schema via EF Core.
    /// Call once at application startup before any other method.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.Database.EnsureCreatedAsync(ct);
        _logger.LogInformation("Database initialised at: {Path}",
            db.Database.GetDbConnection().DataSource);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Albums
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Returns all albums ordered by name, enriched with photo counts.</summary>
    public async Task<IReadOnlyList<Album>> GetAlbumsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Albums
            .OrderBy(a => a.Name)
            .Select(a => new Album
            {
                Id            = a.Id,
                Name          = a.Name,
                CoverPath     = a.CoverPath,
                DirectoryPath = a.DirectoryPath,
                CreatedAt     = a.CreatedAt,
                ModifiedAt    = a.ModifiedAt,
                IsPinned      = a.IsPinned,
                SortOrder     = a.SortOrder,
                PhotoCount    = db.Photos.Count(p => p.AlbumId == a.Id),
            })
            .ToListAsync(ct);
    }

    public async Task<Album?> GetAlbumAsync(long id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Albums.FindAsync(new object[] { id }, ct);
    }

    /// <summary>Inserts a new album and returns the generated Id.</summary>
    public async Task<long> InsertAlbumAsync(Album album, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var now = NowIso();
        album.CreatedAt  = now;
        album.ModifiedAt = now;
        db.Albums.Add(album);
        await db.SaveChangesAsync(ct);
        return album.Id;
    }

    public async Task UpdateAlbumAsync(Album album, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        album.ModifiedAt = NowIso();
        db.Albums.Update(album);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAlbumAsync(long id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        // ON DELETE SET NULL (FK constraint) handles Photos.AlbumId automatically.
        await db.Albums.Where(a => a.Id == id).ExecuteDeleteAsync(ct);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Photos
    // ────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<Photo>> GetPhotosByAlbumAsync(
        long albumId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Photos
            .Where(p => p.AlbumId == albumId)
            .OrderBy(p => p.TakenAt).ThenBy(p => p.FileName)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Photo>> GetAllPhotosAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Photos
            .OrderByDescending(p => p.TakenAt).ThenBy(p => p.FileName)
            .ToListAsync(ct);
    }

    public async Task<Photo?> GetPhotoByPathAsync(string filePath, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Photos.FirstOrDefaultAsync(p => p.FilePath == filePath, ct);
    }

    /// <summary>
    /// Inserts the photo if the file path is not already in the database (idempotent).
    /// Returns the (possibly pre-existing) row Id.
    /// </summary>
    public async Task<long> InsertPhotoAsync(Photo photo, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var existingId = await db.Photos
            .Where(p => p.FilePath == photo.FilePath)
            .Select(p => (long?)p.Id)
            .FirstOrDefaultAsync(ct);
        if (existingId.HasValue) return existingId.Value;

        if (string.IsNullOrEmpty(photo.CreatedAt))
            photo.CreatedAt = NowIso();

        db.Photos.Add(photo);
        await db.SaveChangesAsync(ct);
        return photo.Id;
    }

    public async Task UpdatePhotoAsync(Photo photo, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.Photos.Update(photo);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeletePhotoAsync(long id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        // ON DELETE CASCADE removes the Thumbnails row automatically.
        await db.Photos.Where(p => p.Id == id).ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Removes photo records whose file paths are not in <paramref name="existingPaths"/>.
    /// Processes in 500-row batches to stay well under SQLite parameter limits.
    /// </summary>
    public async Task DeleteStalePhotosAsync(
        IEnumerable<string> existingPaths, CancellationToken ct = default)
    {
        var keepSet = existingPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);

        await using var db = await _factory.CreateDbContextAsync(ct);
        // Load only file paths to avoid pulling full Photo rows for large libraries.
        var dbPaths = await db.Photos.Select(p => p.FilePath).ToListAsync(ct);
        var stale   = dbPaths.Where(p => !keepSet.Contains(p)).ToList();

        if (stale.Count == 0) return;

        int deleted = 0;
        foreach (var chunk in stale.Chunk(500))
        {
            deleted += await db.Photos
                .Where(p => chunk.Contains(p.FilePath))
                .ExecuteDeleteAsync(ct);
        }

        _logger.LogInformation("Removed {N} stale photo records", deleted);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Thumbnails
    // ────────────────────────────────────────────────────────────────────────

    public async Task<Thumbnail?> GetThumbnailAsync(long photoId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Thumbnails.FindAsync(new object[] { photoId }, ct);
    }

    public async Task UpsertThumbnailAsync(Thumbnail thumb, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        thumb.GeneratedAt = NowIso();

        var existing = await db.Thumbnails.FindAsync(new object[] { thumb.PhotoId }, ct);
        if (existing is null)
            db.Thumbnails.Add(thumb);
        else
        {
            existing.ThumbPath        = thumb.ThumbPath;
            existing.GeneratedAt      = thumb.GeneratedAt;
            existing.SourceModifiedAt = thumb.SourceModifiedAt;
        }

        await db.SaveChangesAsync(ct);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Settings (JSON-serialised AppSettings)
    // ────────────────────────────────────────────────────────────────────────

    private const string AppSettingsKey = "AppSettings";

    public async Task<AppSettings> LoadSettingsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.Settings.FindAsync(new object[] { AppSettingsKey }, ct);
        if (row?.Value is null) return new AppSettings();
        return JsonSerializer.Deserialize<AppSettings>(row.Value) ?? new AppSettings();
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var json = JsonSerializer.Serialize(settings);

        var row = await db.Settings.FindAsync(new object[] { AppSettingsKey }, ct);
        if (row is null)
            db.Settings.Add(new Setting { Key = AppSettingsKey, Value = json });
        else
            row.Value = json;

        await db.SaveChangesAsync(ct);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Maintenance
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Deletes all Photos and Thumbnails rows while preserving Albums and Settings.</summary>
    public async Task ClearPhotoCacheAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.Thumbnails.ExecuteDeleteAsync(ct);
        await db.Photos.ExecuteDeleteAsync(ct);
        _logger.LogInformation("Photo and thumbnail cache cleared");
    }

    /// <summary>Drops all application data (Photos, Thumbnails, Albums, Settings).</summary>
    public async Task ClearAllDataAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.Thumbnails.ExecuteDeleteAsync(ct);
        await db.Photos.ExecuteDeleteAsync(ct);
        await db.Albums.ExecuteDeleteAsync(ct);
        await db.Settings.ExecuteDeleteAsync(ct);
        _logger.LogInformation("All application data cleared");
    }

    // ────────────────────────────────────────────────────────────────────────

    private static string NowIso() => DateTime.UtcNow.ToString("O");
}
