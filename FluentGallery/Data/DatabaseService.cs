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
    /// Also ensures tables added in later versions exist in older databases.
    /// Call once at application startup before any other method.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.Database.EnsureCreatedAsync(ct);
        _logger.LogInformation("Database initialised at: {Path}",
            db.Database.GetDbConnection().DataSource);

        // Idempotent: creates the DeletedPhotos table if it doesn't exist yet
        // (needed for databases created before this table was added).
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "DeletedPhotos" (
                "Id"                    INTEGER NOT NULL CONSTRAINT "PK_DeletedPhotos" PRIMARY KEY AUTOINCREMENT,
                "FilePath"              TEXT    NOT NULL,
                "PhotoJson"             TEXT    NOT NULL,
                "ThumbPath"             TEXT,
                "ThumbSourceModifiedAt" TEXT,
                "DeletedAt"             TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "idx_deletedphotos_deletedat"
                ON "DeletedPhotos" ("DeletedAt");
            """, ct);
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

    /// <summary>
    /// Finds the album whose <see cref="Album.DirectoryPath"/> matches <paramref name="dirPath"/>,
    /// creating one (named after the leaf directory) if none exists.
    /// Uses a serialised retry to handle the rare concurrent-insert race.
    /// </summary>
    public async Task<long> GetOrCreateDirectoryAlbumAsync(string dirPath, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var existing = await db.Albums
            .Where(a => a.DirectoryPath == dirPath)
            .Select(a => (long?)a.Id)
            .FirstOrDefaultAsync(ct);

        if (existing.HasValue) return existing.Value;

        // Derive album name from the leaf folder name
        var name = Path.GetFileName(
            dirPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            ?? dirPath;

        var now   = NowIso();
        var album = new Album
        {
            Name          = name,
            DirectoryPath = dirPath,
            CreatedAt     = now,
            ModifiedAt    = now,
        };

        db.Albums.Add(album);
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Created directory album '{Name}' for {Dir}", name, dirPath);
        return album.Id;
    }

    /// <summary>
    /// Finds every photo whose <see cref="Photo.AlbumId"/> is null, groups them by
    /// their parent directory, creates (or finds) an album for each directory, and
    /// batch-assigns the <see cref="Photo.AlbumId"/>.
    /// Call this after a scan to repair photos inserted before album tracking existed.
    /// </summary>
    public async Task RepairOrphanAlbumIdsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var orphans = await db.Photos
            .AsNoTracking()
            .Where(p => p.AlbumId == null)
            .Select(p => new { p.Id, p.FilePath })
            .ToListAsync(ct);

        if (orphans.Count == 0) return;

        // Group by parent directory — each group gets its own album
        var byDir = orphans
            .GroupBy(o => Path.GetDirectoryName(o.FilePath) ?? string.Empty,
                     StringComparer.OrdinalIgnoreCase)
            .Where(g => !string.IsNullOrEmpty(g.Key));

        int repaired = 0;
        foreach (var group in byDir)
        {
            var albumId = await GetOrCreateDirectoryAlbumAsync(group.Key, ct);
            var ids     = group.Select(o => o.Id).ToList();

            foreach (var chunk in ids.Chunk(500))
            {
                repaired += await db.Photos
                    .Where(p => chunk.Contains(p.Id))
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(p => p.AlbumId, albumId), ct);
            }
        }

        if (repaired > 0)
            _logger.LogInformation("已为 {N} 张孤立照片补齐 AlbumId", repaired);
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

    /// <summary>Returns albums where <see cref="Album.IsPinned"/> is true, ordered by <see cref="Album.SortOrder"/> then name.</summary>
    public async Task<IReadOnlyList<Album>> GetPinnedAlbumsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Albums
            .Where(a => a.IsPinned)
            .OrderBy(a => a.SortOrder)
            .ThenBy(a => a.Name)
            .ToListAsync(ct);
    }

    /// <summary>Sets the <see cref="Album.IsPinned"/> flag for the specified album.</summary>
    public async Task SetAlbumPinnedAsync(long id, bool isPinned, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var album = await db.Albums.FindAsync(new object[] { id }, ct);
        if (album is null) return;
        album.IsPinned   = isPinned;
        album.ModifiedAt = NowIso();
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

    /// <summary>
    /// Returns the most recently inserted photo in the album (by row Id),
    /// or <c>null</c> if the album is empty. Used to derive the album cover thumbnail.
    /// </summary>
    public async Task<Photo?> GetLatestPhotoByAlbumAsync(long albumId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Photos
            .Where(p => p.AlbumId == albumId)
            .OrderByDescending(p => p.Id)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<Photo>> GetAllPhotosAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Photos
            .OrderByDescending(p => p.TakenAt).ThenBy(p => p.FileName)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns photos that have no thumbnail record, or whose thumbnail record is stale
    /// (the source file was modified after the thumbnail was generated).
    /// These are the candidates for batch thumbnail generation.
    /// </summary>
    public async Task<IReadOnlyList<Photo>> GetPhotosWithoutThumbnailAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Photos
            .Where(p => !db.Thumbnails.Any(t =>
                t.PhotoId == p.Id && t.SourceModifiedAt == p.ModifiedAt))
            .OrderBy(p => p.FileName)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Searches photos by file name keyword and/or a date range on a chosen date field.
    /// All parameters are optional; omitting all returns nothing (caller should validate).
    /// </summary>
    /// <param name="keyword">Case-insensitive substring match on <see cref="Photo.FileName"/>.</param>
    /// <param name="dateField">
    ///   Which date field to filter on: <c>"TakenAt"</c>, <c>"ModifiedAt"</c>, or <c>"CreatedAt"</c>.
    ///   Ignored when both <paramref name="dateFrom"/> and <paramref name="dateTo"/> are null.
    /// </param>
    /// <param name="dateFrom">Inclusive lower bound as <c>yyyy-MM-dd</c> string, or null.</param>
    /// <param name="dateTo">Inclusive upper bound as <c>yyyy-MM-dd</c> string, or null.</param>
    /// <param name="albumId">When supplied, restricts results to the specified album.</param>
    public async Task<IReadOnlyList<Photo>> SearchPhotosAsync(
        string?  keyword,
        string?  dateField,
        string?  dateFrom,
        string?  dateTo,
        long?    albumId   = null,
        CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        IQueryable<Photo> query = db.Photos.AsNoTracking();

        if (albumId.HasValue)
            query = query.Where(p => p.AlbumId == albumId.Value);

        if (!string.IsNullOrWhiteSpace(keyword))
            query = query.Where(p => EF.Functions.Like(p.FileName, $"%{keyword}%"));

        // Pull matching rows first, then apply date-range filter in memory.
        // ISO 8601 strings are lexicographically sortable so this is safe.
        var photos = await query
            .OrderBy(p => p.FileName)
            .ToListAsync(ct);

        bool hasDateFilter = !string.IsNullOrWhiteSpace(dateFrom) || !string.IsNullOrWhiteSpace(dateTo);
        if (hasDateFilter && !string.IsNullOrWhiteSpace(dateField))
        {
            photos = photos.Where(p =>
            {
                var raw = dateField switch
                {
                    "TakenAt"    => p.TakenAt,
                    "ModifiedAt" => p.ModifiedAt,
                    _            => p.CreatedAt,
                };

                if (string.IsNullOrEmpty(raw)) return false;

                // Take only the date portion (first 10 chars) for yyyy-MM-dd comparison.
                var dateOnly = raw.Length >= 10 ? raw[..10] : raw;

                if (!string.IsNullOrWhiteSpace(dateFrom) &&
                    string.Compare(dateOnly, dateFrom, StringComparison.Ordinal) < 0)
                    return false;

                if (!string.IsNullOrWhiteSpace(dateTo) &&
                    string.Compare(dateOnly, dateTo, StringComparison.Ordinal) > 0)
                    return false;

                return true;
            }).ToList();
        }

        return photos;
    }

    public async Task<Photo?> GetPhotoByPathAsync(string filePath, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Photos.FirstOrDefaultAsync(p => p.FilePath == filePath, ct);
    }

    public async Task<Photo?> GetPhotoByIdAsync(long id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Photos.FindAsync(new object[] { id }, ct);
    }

    /// <summary>
    /// Loads a lightweight snapshot of every photo row — only the columns needed for the
    /// "skip unchanged / detect changed" decision during a directory scan.
    /// Avoids issuing one query per file and is safe to call from any thread.
    /// </summary>
    public async Task<Dictionary<string, PhotoScanMeta>> GetAllPhotoMetadataAsync(
        CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Photos
            .AsNoTracking()
            .Select(p => new { p.FilePath, p.Id, p.ModifiedAt })
            .ToDictionaryAsync(
                p => p.FilePath,
                p => new PhotoScanMeta(p.Id, p.ModifiedAt),
                StringComparer.OrdinalIgnoreCase,
                ct);
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
    // DeletedPhotos (undo history)
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Persists a snapshot of <paramref name="photo"/> and its thumbnail so the
    /// deletion can be undone later.
    /// </summary>
    public async Task<long> InsertDeletedPhotoAsync(
        Photo       photo,
        string?     thumbPath,
        string?     thumbSourceModifiedAt,
        CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var record = new DeletedPhoto
        {
            FilePath              = photo.FilePath,
            PhotoJson             = System.Text.Json.JsonSerializer.Serialize(photo),
            ThumbPath             = thumbPath,
            ThumbSourceModifiedAt = thumbSourceModifiedAt,
            DeletedAt             = NowIso(),
        };
        db.DeletedPhotos.Add(record);
        await db.SaveChangesAsync(ct);
        return record.Id;
    }

    /// <summary>Returns the undo record for the given row Id, or <c>null</c> if not found.</summary>
    public async Task<DeletedPhoto?> GetDeletedPhotoAsync(long id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.DeletedPhotos.FindAsync(new object[] { id }, ct);
    }

    /// <summary>Removes the undo record after a successful restore.</summary>
    public async Task DeleteDeletedPhotoAsync(long id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.DeletedPhotos.Where(d => d.Id == id).ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Deletes all <see cref="DeletedPhoto"/> records whose <c>DeletedAt</c> timestamp
    /// is older than one month. Call this at application startup.
    /// </summary>
    public async Task CleanupOldDeletedPhotosAsync(CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddMonths(-1).ToString("O");
        await using var db = await _factory.CreateDbContextAsync(ct);
        int deleted = await db.DeletedPhotos
            .Where(d => string.Compare(d.DeletedAt, cutoff, StringComparison.Ordinal) < 0)
            .ExecuteDeleteAsync(ct);
        if (deleted > 0)
            _logger.LogInformation("Cleaned up {N} stale DeletedPhoto records", deleted);
    }

    // ────────────────────────────────────────────────────────────────────────

    private static string NowIso() => DateTime.UtcNow.ToString("O");
}

/// <summary>
/// Lightweight projection returned by <see cref="DatabaseService.GetAllPhotoMetadataAsync"/>.
/// Only carries the columns needed to decide whether a file has changed during a scan.
/// </summary>
public sealed record PhotoScanMeta(long Id, string ModifiedAt);
