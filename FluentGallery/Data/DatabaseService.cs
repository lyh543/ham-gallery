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
    /// Also runs idempotent ALTER TABLE migrations for columns added in later versions.
    /// Call once at application startup before any other method.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Database initialised at: {Path}",
            db.Database.GetDbConnection().DataSource);

        // Idempotent: add photo-sort preference columns to Albums (existing databases).
        // Use PRAGMA to check existence first so EF Core doesn't log a command failure.
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        var columns = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(\"Albums\")";
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                columns.Add(reader.GetString(1)); // column 1 = name
        }
        if (!columns.Contains("PhotoSortField"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Albums\" ADD COLUMN \"PhotoSortField\" INTEGER NOT NULL DEFAULT 4", ct).ConfigureAwait(false);
        if (!columns.Contains("PhotoSortDirection"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Albums\" ADD COLUMN \"PhotoSortDirection\" INTEGER NOT NULL DEFAULT 1", ct).ConfigureAwait(false);

        // Idempotent: add ThumbnailDisabled column to Thumbnails (existing databases).
        var thumbColumns = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(\"Thumbnails\")";
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                thumbColumns.Add(reader.GetString(1));
        }
        if (!thumbColumns.Contains("ThumbnailDisabled"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Thumbnails\" ADD COLUMN \"ThumbnailDisabled\" INTEGER NOT NULL DEFAULT 0", ct).ConfigureAwait(false);

        // Fix: value 5 was the old "Natural" sort which has been removed.
        // Reset any albums that still have it back to TakenAt (4).
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE \"Albums\" SET \"PhotoSortField\" = 4 WHERE \"PhotoSortField\" = 5", ct)
            .ConfigureAwait(false);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Albums
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all albums ordered by name, enriched with photo counts and
    /// photo-timestamp aggregates (Min/Max TakenAt, CreatedAt, ModifiedAt) so the
    /// album list can be sorted by the content inside each album.
    /// </summary>
    public async Task<IReadOnlyList<Album>> GetAlbumsAsync(CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        return await db.Albums
            .OrderBy(a => a.Name)
            .Select(a => new Album
            {
                Id                 = a.Id,
                Name               = a.Name,
                CoverPath          = a.CoverPath,
                DirectoryPath      = a.DirectoryPath,
                CreatedAt          = a.CreatedAt,
                ModifiedAt         = a.ModifiedAt,
                IsPinned           = a.IsPinned,
                SortOrder          = a.SortOrder,
                PhotoSortField     = a.PhotoSortField,
                PhotoSortDirection = a.PhotoSortDirection,
                PhotoCount         = db.Photos.Count(p => p.AlbumId == a.Id),
                // MAX photo-timestamp per album — used for album-level sorting.
                // Cast to string? so EF Core maps SQL NULL (empty album) to null instead of throwing.
                MaxPhotoTakenAt    = db.Photos.Where(p => p.AlbumId == a.Id && p.TakenAt != null && p.TakenAt != "").Max(p => p.TakenAt),
                MaxPhotoCreatedAt  = db.Photos.Where(p => p.AlbumId == a.Id).Max(p => (string?)p.CreatedAt),
                MaxPhotoModifiedAt = db.Photos.Where(p => p.AlbumId == a.Id).Max(p => (string?)p.ModifiedAt),
            })
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<Album?> GetAlbumAsync(long id, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        return await db.Albums.FindAsync(new object[] { id }, ct).ConfigureAwait(false);
    }

    public async Task<long> GetAlbumTotalSizeAsync(long albumId, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        return await db.Photos
            .Where(p => p.AlbumId == albumId)
            .SumAsync(p => (long?)p.FileSize, ct)
            .ConfigureAwait(false) ?? 0;
    }

    /// <summary>
    /// Finds the album whose <see cref="Album.DirectoryPath"/> matches <paramref name="dirPath"/>,
    /// creating one (named after the leaf directory) if none exists.
    /// </summary>
    public async Task<long> GetOrCreateDirectoryAlbumAsync(string dirPath, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();

        var existing = await db.Albums
            .Where(a => a.DirectoryPath == dirPath)
            .Select(a => (long?)a.Id)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (existing.HasValue) return existing.Value;

        var name = Path.GetFileName(
            dirPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            ?? dirPath;

        var now   = NowIso();
        var album = new Album { Name = name, DirectoryPath = dirPath, CreatedAt = now, ModifiedAt = now };
        db.Albums.Add(album);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Created directory album '{Name}' for {Dir}", name, dirPath);
        return album.Id;
    }

    /// <summary>
    /// Finds every photo whose AlbumId is null, groups them by parent directory,
    /// creates (or finds) an album per directory, and batch-assigns AlbumId.
    /// </summary>
    public async Task RepairOrphanAlbumIdsAsync(CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();

        var orphans = await db.Photos
            .AsNoTracking()
            .Where(p => p.AlbumId == null)
            .Select(p => new { p.Id, p.FilePath })
            .ToListAsync(ct).ConfigureAwait(false);

        if (orphans.Count == 0) return;

        var byDir = orphans
            .GroupBy(o => Path.GetDirectoryName(o.FilePath) ?? string.Empty,
                     StringComparer.OrdinalIgnoreCase)
            .Where(g => !string.IsNullOrEmpty(g.Key));

        int repaired = 0;
        foreach (var group in byDir)
        {
            var albumId = await GetOrCreateDirectoryAlbumAsync(group.Key, ct).ConfigureAwait(false);
            var ids     = group.Select(o => o.Id).ToList();

            foreach (var chunk in ids.Chunk(500))
            {
                using var db2 = _factory.CreateDbContext();
                repaired += await db2.Photos
                    .Where(p => chunk.Contains(p.Id))
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.AlbumId, albumId), ct)
                    .ConfigureAwait(false);
            }
        }

        if (repaired > 0)
            _logger.LogInformation("Repaired missing AlbumId for {N} orphan photos", repaired);
    }

    /// <summary>Inserts a new album and returns the generated Id.</summary>
    public async Task<long> InsertAlbumAsync(Album album, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var now = NowIso();
        album.CreatedAt  = now;
        album.ModifiedAt = now;
        db.Albums.Add(album);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return album.Id;
    }

    public async Task UpdateAlbumAsync(Album album, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        album.ModifiedAt = NowIso();
        db.Albums.Update(album);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Returns albums where <see cref="Album.IsPinned"/> is true, ordered by SortOrder then name.</summary>
    public async Task<IReadOnlyList<Album>> GetPinnedAlbumsAsync(CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        return await db.Albums
            .Where(a => a.IsPinned)
            .OrderBy(a => a.SortOrder)
            .ThenBy(a => a.Name)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Sets the <see cref="Album.IsPinned"/> flag for the specified album.</summary>
    public async Task SetAlbumPinnedAsync(long id, bool isPinned, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var album = await db.Albums.FindAsync(new object[] { id }, ct).ConfigureAwait(false);
        if (album is null) return;
        album.IsPinned   = isPinned;
        album.ModifiedAt = NowIso();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAlbumAsync(long id, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        await db.Albums.Where(a => a.Id == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAlbumsAsync(IEnumerable<long> ids, CancellationToken ct = default)
    {
        var idList = ids.Distinct().ToList();
        if (idList.Count == 0) return;

        using var db = _factory.CreateDbContext();
        await db.Albums.Where(a => idList.Contains(a.Id)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Deletes albums that have no photos (e.g. after a scan directory is removed).</summary>
    public async Task DeleteEmptyAlbumsAsync(CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        int deleted = await db.Albums
            .Where(a => !db.Photos.Any(p => p.AlbumId == a.Id))
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        if (deleted > 0)
            _logger.LogInformation("Removed {N} empty albums", deleted);
    }

    /// <summary>
    /// Persists only the photo-sort preference for the given album.
    /// Does not update ModifiedAt (sort is a display preference, not a data change).
    /// </summary>
    public async Task SaveAlbumPhotoSortAsync(
        long albumId, int sortField, int sortDirection, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        await db.Albums
            .Where(a => a.Id == albumId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.PhotoSortField,     sortField)
                .SetProperty(a => a.PhotoSortDirection, sortDirection), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Returns the most-recently modified photo in an album (used for album cover).</summary>
    public async Task<Photo?> GetLatestPhotoByAlbumAsync(long albumId, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        return await db.Photos
            .Where(p => p.AlbumId == albumId)
            .OrderByDescending(p => p.ModifiedAt)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Photos
    // ────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<Photo>> GetPhotosByAlbumAsync(
        long albumId, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        return await db.Photos
            .Where(p => p.AlbumId == albumId)
            .OrderBy(p => p.TakenAt).ThenBy(p => p.FileName)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Photo>> GetAllPhotosAsync(CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        return await db.Photos
            .OrderByDescending(p => p.TakenAt).ThenBy(p => p.FileName)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<Photo?> GetPhotoByPathAsync(string filePath, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        return await db.Photos.FirstOrDefaultAsync(p => p.FilePath == filePath, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Returns all photos whose album's <see cref="Album.DirectoryPath"/> matches
    /// <paramref name="dirPath"/>, ordered by TakenAt then FileName.
    /// Returns an empty list when the directory has no corresponding album in the database
    /// (i.e. the folder has not been scanned / indexed).
    /// </summary>
    public async Task<IReadOnlyList<Photo>> GetPhotosByDirectoryAsync(
        string dirPath, CancellationToken ct = default)
    {
        dirPath = dirPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        using var db = _factory.CreateDbContext();

        var albumId = await db.Albums
            .Where(a => a.DirectoryPath == dirPath)
            .Select(a => (long?)a.Id)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (!albumId.HasValue) return Array.Empty<Photo>();

        return await db.Photos
            .AsNoTracking()
            .Where(p => p.AlbumId == albumId.Value)
            .OrderBy(p => p.TakenAt).ThenBy(p => p.FileName)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<Photo?> GetPhotoByIdAsync(long id, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        return await db.Photos.FindAsync(new object[] { id }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns a dictionary keyed by FilePath, containing lightweight scan metadata
    /// (Id + ModifiedAt) for all photos. Used by <see cref="ScanService"/> to avoid
    /// per-file DB round-trips.
    /// </summary>
    public async Task<Dictionary<string, PhotoScanMeta>> GetAllPhotoMetadataAsync(
        CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        return await db.Photos
            .AsNoTracking()
            .Select(p => new { p.FilePath, p.Id, p.ModifiedAt })
            .ToDictionaryAsync(
                p => p.FilePath,
                p => new PhotoScanMeta(p.Id, p.ModifiedAt),
                StringComparer.OrdinalIgnoreCase,
                ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Inserts the photo if the file path is not already in the database (idempotent).
    /// Returns the (possibly pre-existing) row Id.
    /// </summary>
    public async Task<long> InsertPhotoAsync(Photo photo, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();

        var existingId = await db.Photos
            .Where(p => p.FilePath == photo.FilePath)
            .Select(p => (long?)p.Id)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (existingId.HasValue) return existingId.Value;

        if (string.IsNullOrEmpty(photo.CreatedAt))
            photo.CreatedAt = NowIso();

        db.Photos.Add(photo);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return photo.Id;
    }

    public async Task UpdatePhotoAsync(Photo photo, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        db.Photos.Update(photo);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task DeletePhotoAsync(long id, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        await db.Photos.Where(p => p.Id == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    public async Task DeletePhotosByAlbumIdsAsync(
        IEnumerable<long> albumIds,
        CancellationToken ct = default)
    {
        var idList = albumIds.Distinct().ToList();
        if (idList.Count == 0) return;

        using var db = _factory.CreateDbContext();
        await db.Photos
            .Where(p => p.AlbumId.HasValue && idList.Contains(p.AlbumId.Value))
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Removes photo records whose file paths are not in <paramref name="existingPaths"/>.
    /// Processes in 500-row batches to stay well under SQLite parameter limits.
    /// </summary>
    public async Task DeleteStalePhotosAsync(
        IEnumerable<string> existingPaths, CancellationToken ct = default)
    {
        var keepSet = existingPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);

        using var db = _factory.CreateDbContext();
        var dbPaths = await db.Photos.Select(p => p.FilePath).ToListAsync(ct).ConfigureAwait(false);
        var stale   = dbPaths.Where(p => !keepSet.Contains(p)).ToList();

        if (stale.Count == 0) return;

        int deleted = 0;
        foreach (var chunk in stale.Chunk(500))
        {
            deleted += await db.Photos
                .Where(p => chunk.Contains(p.FilePath))
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }

        _logger.LogInformation("Removed {N} stale photo records", deleted);
    }

    /// <summary>
    /// Searches photos by keyword and/or date range, optionally filtered to one album.
    /// Date filtering is done in memory after a keyword/album pre-filter.
    /// </summary>
    public async Task<IReadOnlyList<Photo>> SearchPhotosAsync(
        string?           keyword,
        string?           dateField,
        string?           dateFrom,
        string?           dateTo,
        long?             albumId,
        CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        IQueryable<Photo> q = db.Photos.AsNoTracking();

        if (albumId.HasValue)
            q = q.Where(p => p.AlbumId == albumId.Value);

        if (!string.IsNullOrEmpty(keyword))
            q = q.Where(p => p.FileName.Contains(keyword));

        var photos = await q.OrderBy(p => p.FileName).ToListAsync(ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(dateFrom) || !string.IsNullOrEmpty(dateTo))
        {
            photos = photos.Where(p =>
            {
                var date = dateField switch
                {
                    "TakenAt"    => p.TakenAt,
                    "ModifiedAt" => p.ModifiedAt,
                    _            => p.CreatedAt,
                };
                if (string.IsNullOrEmpty(date)) return false;
                var d = date[..Math.Min(10, date.Length)];
                if (!string.IsNullOrEmpty(dateFrom) && string.Compare(d, dateFrom, StringComparison.Ordinal) < 0) return false;
                if (!string.IsNullOrEmpty(dateTo)   && string.Compare(d, dateTo,   StringComparison.Ordinal) > 0) return false;
                return true;
            }).ToList();
        }

        return photos;
    }

    // ────────────────────────────────────────────────────────────────────────
    // Thumbnails
    // ────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<string>> GetAllThumbnailPathsAsync(CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        return await db.Thumbnails
            .AsNoTracking()
            .Where(t => t.ThumbPath != null && t.ThumbPath != "")
            .Select(t => t.ThumbPath!)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<Thumbnail?> GetThumbnailAsync(long photoId, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        return await db.Thumbnails.FindAsync(new object[] { photoId }, ct).ConfigureAwait(false);
    }

    public async Task UpsertThumbnailAsync(Thumbnail thumb, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        thumb.GeneratedAt = NowIso();

        var existing = await db.Thumbnails.FindAsync(new object[] { thumb.PhotoId }, ct)
            .ConfigureAwait(false);
        if (existing is null)
            db.Thumbnails.Add(thumb);
        else
        {
            existing.ThumbPath          = thumb.ThumbPath;
            existing.ThumbnailDisabled  = thumb.ThumbnailDisabled;
            existing.GeneratedAt        = thumb.GeneratedAt;
            existing.SourceModifiedAt   = thumb.SourceModifiedAt;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Settings (JSON-serialised AppSettings)
    // ────────────────────────────────────────────────────────────────────────

    private const string AppSettingsKey = "AppSettings";

    public async Task<AppSettings> LoadSettingsAsync(CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var row = await db.Settings.FindAsync(new object[] { AppSettingsKey }, ct)
            .ConfigureAwait(false);
        if (row?.Value is null) return new AppSettings();
        return JsonSerializer.Deserialize<AppSettings>(row.Value) ?? new AppSettings();
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var json = JsonSerializer.Serialize(settings);

        var row = await db.Settings.FindAsync(new object[] { AppSettingsKey }, ct)
            .ConfigureAwait(false);
        if (row is null)
            db.Settings.Add(new Setting { Key = AppSettingsKey, Value = json });
        else
            row.Value = json;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Maintenance
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Deletes only the Thumbnails rows, leaving Photos intact.</summary>
    public async Task ClearThumbnailsAsync(CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        await db.Thumbnails.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Thumbnail DB entries cleared");
    }

    /// <summary>Deletes all Photos and Thumbnails rows while preserving Albums and Settings.</summary>
    public async Task ClearPhotoCacheAsync(CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        await db.Thumbnails.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.Photos.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Photo and thumbnail cache cleared");
    }

    /// <summary>Drops all application data (Photos, Thumbnails, Albums, Settings).</summary>
    public async Task ClearAllDataAsync(CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        await db.Thumbnails.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.Photos.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.Albums.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.Settings.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("All application data cleared");
    }

    // ────────────────────────────────────────────────────────────────────────
    // DeletedPhotos (undo history)
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns photos that do not yet have a corresponding Thumbnails row.
    /// Used by Settings → "rebuild thumbnails" to find work to do.
    /// </summary>
    public async Task<IReadOnlyList<Photo>> GetPhotosWithoutThumbnailAsync(CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        return await db.Photos
            .AsNoTracking()
            .Where(p => !db.Thumbnails.Any(t => t.PhotoId == p.Id))
            .OrderBy(p => p.FilePath)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Persists a snapshot of <paramref name="photo"/> and its thumbnail so the
    /// deletion can be undone later. Returns the new DeletedPhoto row Id.
    /// </summary>
    public async Task<long> InsertDeletedPhotoAsync(
        Photo             photo,
        string?           thumbPath,
        string?           thumbSourceModifiedAt,
        CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var record = new DeletedPhoto
        {
            FilePath              = photo.FilePath,
            PhotoJson             = JsonSerializer.Serialize(photo),
            ThumbPath             = thumbPath,
            ThumbSourceModifiedAt = thumbSourceModifiedAt,
            DeletedAt             = NowIso(),
        };
        db.DeletedPhotos.Add(record);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return record.Id;
    }

    /// <summary>Returns the undo record for the given row Id, or <c>null</c> if not found.</summary>
    public async Task<DeletedPhoto?> GetDeletedPhotoAsync(long id, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        return await db.DeletedPhotos.FindAsync(new object[] { id }, ct).ConfigureAwait(false);
    }

    /// <summary>Removes the undo record after a successful restore.</summary>
    public async Task DeleteDeletedPhotoAsync(long id, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        await db.DeletedPhotos.Where(d => d.Id == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes all <see cref="DeletedPhoto"/> records whose <c>DeletedAt</c> timestamp
    /// is older than one month. Call this at application startup.
    /// </summary>
    public async Task CleanupOldDeletedPhotosAsync(CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddMonths(-1).ToString("O");
        using var db = _factory.CreateDbContext();
        int deleted = await db.DeletedPhotos
            .Where(d => string.Compare(d.DeletedAt, cutoff) < 0)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        if (deleted > 0)
            _logger.LogInformation("Cleaned up {N} stale DeletedPhoto records", deleted);
    }

    // ────────────────────────────────────────────────────────────────────────

    private static string NowIso() => DateTime.UtcNow.ToString("O");
}

/// <summary>
/// Lightweight projection used by <see cref="DatabaseService.GetAllPhotoMetadataAsync"/>.
/// Carries only the columns needed to decide whether a file has changed during a scan.
/// </summary>
public sealed record PhotoScanMeta(long Id, string ModifiedAt);
