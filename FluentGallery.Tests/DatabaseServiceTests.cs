using FluentGallery.Data;
using FluentGallery.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FluentGallery.Tests;

/// <summary>
/// Integration tests for <see cref="DatabaseService"/> using an in-memory SQLite database.
/// Each test gets a completely isolated database — no shared state.
/// </summary>
public sealed class DatabaseServiceTests : IAsyncLifetime
{
    // ── Fixture helpers ──────────────────────────────────────────────────────

    private TestDbContextFactory? _factoryHolder;
    private DatabaseService? _sut;

    /// <summary>Per-test setup: create a fresh in-memory SQLite DB and initialise the schema.</summary>
    public async Task InitializeAsync()
    {
        // TestDbContextFactory keeps one connection open so the in-memory DB
        // is not dropped between context instances.
        _factoryHolder = new TestDbContextFactory();
        _sut = new DatabaseService(_factoryHolder, NullLogger<DatabaseService>.Instance);
        await _sut.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        _factoryHolder?.Dispose();
        _sut     = null;
        _factoryHolder = null;
        return Task.CompletedTask;
    }

    private DatabaseService Sut => _sut!;

    // ── Schema ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task InitializeAsync_CreatesAllTables()
    {
        // Arrange / Act — done in InitializeAsync

        // Assert: verify by querying each table without runtime exceptions
        var albums     = await Sut.GetAlbumsAsync();
        var photos     = await Sut.GetAllPhotosAsync();
        var settings   = await Sut.LoadSettingsAsync();

        Assert.Empty(albums);
        Assert.Empty(photos);
        Assert.NotNull(settings);
    }

    // ── Albums ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task InsertAlbum_ReturnsGeneratedId()
    {
        var id = await Sut.InsertAlbumAsync(new Album { Name = "Holidays" });

        Assert.True(id > 0);
    }

    [Fact]
    public async Task GetAlbum_ReturnsCorrectRow()
    {
        var id = await Sut.InsertAlbumAsync(new Album { Name = "Vacation" });

        var album = await Sut.GetAlbumAsync(id);

        Assert.NotNull(album);
        Assert.Equal("Vacation", album!.Name);
        Assert.False(string.IsNullOrEmpty(album.CreatedAt));
    }

    [Fact]
    public async Task UpdateAlbum_PersistsChanges()
    {
        var id    = await Sut.InsertAlbumAsync(new Album { Name = "Old Name" });
        var album = await Sut.GetAlbumAsync(id);

        album!.Name     = "New Name";
        album.IsPinned  = true;
        await Sut.UpdateAlbumAsync(album);

        var updated = await Sut.GetAlbumAsync(id);
        Assert.Equal("New Name", updated!.Name);
        Assert.True(updated.IsPinned);
    }

    [Fact]
    public async Task DeleteAlbum_RemovesRow()
    {
        var id = await Sut.InsertAlbumAsync(new Album { Name = "To Delete" });
        await Sut.DeleteAlbumAsync(id);

        Assert.Null(await Sut.GetAlbumAsync(id));
    }

    [Fact]
    public async Task DeleteAlbum_SetsPhotoAlbumIdToNull()
    {
        // Arrange: album with a photo
        var albumId = await Sut.InsertAlbumAsync(new Album { Name = "Parent" });
        var photoId = await Sut.InsertPhotoAsync(MakePhoto("C:/img/a.jpg", albumId));

        // Act
        await Sut.DeleteAlbumAsync(albumId);

        // Assert: photo still exists but AlbumId = null (ON DELETE SET NULL)
        var photos = await Sut.GetAllPhotosAsync();
        var photo  = photos.FirstOrDefault(p => p.Id == photoId);
        Assert.NotNull(photo);
        Assert.Null(photo!.AlbumId);
    }

    [Fact]
    public async Task GetAlbums_PhotoCountIsCorrect()
    {
        var albumId = await Sut.InsertAlbumAsync(new Album { Name = "With Photos" });
        await Sut.InsertPhotoAsync(MakePhoto("C:/img/1.jpg", albumId));
        await Sut.InsertPhotoAsync(MakePhoto("C:/img/2.jpg", albumId));

        var albums = await Sut.GetAlbumsAsync();
        var album  = albums.Single(a => a.Id == albumId);

        Assert.Equal(2, album.PhotoCount);
    }

    // ── Photos ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task InsertPhoto_ReturnsGeneratedId()
    {
        var id = await Sut.InsertPhotoAsync(MakePhoto("C:/photos/test.jpg"));
        Assert.True(id > 0);
    }

    [Fact]
    public async Task InsertPhoto_IsIdempotent_WhenPathAlreadyExists()
    {
        const string path = "C:/photos/dup.jpg";
        var id1 = await Sut.InsertPhotoAsync(MakePhoto(path));
        var id2 = await Sut.InsertPhotoAsync(MakePhoto(path));

        Assert.Equal(id1, id2);
        // Only one row should exist
        var photos = await Sut.GetAllPhotosAsync();
        Assert.Single(photos, p => p.FilePath == path);
    }

    [Fact]
    public async Task GetPhotoByPath_ReturnsCorrectRow()
    {
        const string path = "C:/photos/lookup.jpg";
        await Sut.InsertPhotoAsync(MakePhoto(path));

        var photo = await Sut.GetPhotoByPathAsync(path);

        Assert.NotNull(photo);
        Assert.Equal(path, photo!.FilePath);
    }

    [Fact]
    public async Task UpdatePhoto_PersistsChanges()
    {
        var id    = await Sut.InsertPhotoAsync(MakePhoto("C:/photos/edit.jpg"));
        var photo = (await Sut.GetAllPhotosAsync()).First(p => p.Id == id);

        photo.Width  = 1920;
        photo.Height = 1080;
        await Sut.UpdatePhotoAsync(photo);

        var updated = await Sut.GetPhotoByPathAsync("C:/photos/edit.jpg");
        Assert.Equal(1920, updated!.Width);
        Assert.Equal(1080, updated.Height);
    }

    [Fact]
    public async Task DeletePhoto_RemovesRow()
    {
        var id = await Sut.InsertPhotoAsync(MakePhoto("C:/photos/gone.jpg"));
        await Sut.DeletePhotoAsync(id);

        Assert.Null(await Sut.GetPhotoByPathAsync("C:/photos/gone.jpg"));
    }

    [Fact]
    public async Task DeletePhoto_CascadeDeletesThumbnail()
    {
        var photoId = await Sut.InsertPhotoAsync(MakePhoto("C:/photos/with-thumb.jpg"));
        await Sut.UpsertThumbnailAsync(new Thumbnail
        {
            PhotoId          = photoId,
            ThumbPath        = "C:/cache/thumb.jpg",
            SourceModifiedAt = DateTime.UtcNow.ToString("O"),
        });

        await Sut.DeletePhotoAsync(photoId);

        Assert.Null(await Sut.GetThumbnailAsync(photoId));
    }

    [Fact]
    public async Task GetPhotosByAlbum_ReturnOnlyAlbumPhotos()
    {
        var albumA = await Sut.InsertAlbumAsync(new Album { Name = "A" });
        var albumB = await Sut.InsertAlbumAsync(new Album { Name = "B" });

        await Sut.InsertPhotoAsync(MakePhoto("C:/a/1.jpg", albumA));
        await Sut.InsertPhotoAsync(MakePhoto("C:/a/2.jpg", albumA));
        await Sut.InsertPhotoAsync(MakePhoto("C:/b/1.jpg", albumB));

        var photosA = await Sut.GetPhotosByAlbumAsync(albumA);
        Assert.Equal(2, photosA.Count);
        Assert.All(photosA, p => Assert.Equal(albumA, p.AlbumId));
    }

    // ── Stale photo cleanup ──────────────────────────────────────────────────

    [Fact]
    public async Task DeleteStalePhotos_RemovesOnlyMissingPaths()
    {
        await Sut.InsertPhotoAsync(MakePhoto("C:/keep/1.jpg"));
        await Sut.InsertPhotoAsync(MakePhoto("C:/keep/2.jpg"));
        await Sut.InsertPhotoAsync(MakePhoto("C:/stale/gone.jpg"));

        // Report only the two "keep" files as still on disk
        await Sut.DeleteStalePhotosAsync(["C:/keep/1.jpg", "C:/keep/2.jpg"]);

        var remaining = await Sut.GetAllPhotosAsync();
        Assert.Equal(2, remaining.Count);
        Assert.DoesNotContain(remaining, p => p.FilePath == "C:/stale/gone.jpg");
    }

    [Fact]
    public async Task DeleteStalePhotos_WhenNoneAreStale_DoesNothing()
    {
        await Sut.InsertPhotoAsync(MakePhoto("C:/ok/img.jpg"));

        await Sut.DeleteStalePhotosAsync(["C:/ok/img.jpg"]);

        Assert.Single(await Sut.GetAllPhotosAsync());
    }

    // ── Thumbnails ───────────────────────────────────────────────────────────

    [Fact]
    public async Task UpsertThumbnail_InsertsNewRow()
    {
        var photoId = await Sut.InsertPhotoAsync(MakePhoto("C:/photos/thumb-test.jpg"));
        var thumb = new Thumbnail
        {
            PhotoId          = photoId,
            ThumbPath        = "C:/cache/t1.jpg",
            SourceModifiedAt = "2024-01-01T00:00:00Z",
        };

        await Sut.UpsertThumbnailAsync(thumb);

        var fetched = await Sut.GetThumbnailAsync(photoId);
        Assert.NotNull(fetched);
        Assert.Equal("C:/cache/t1.jpg", fetched!.ThumbPath);
    }

    [Fact]
    public async Task UpsertThumbnail_UpdatesExistingRow()
    {
        var photoId = await Sut.InsertPhotoAsync(MakePhoto("C:/photos/thumb-update.jpg"));
        await Sut.UpsertThumbnailAsync(new Thumbnail
        {
            PhotoId          = photoId,
            ThumbPath        = "C:/cache/old.jpg",
            SourceModifiedAt = "2024-01-01T00:00:00Z",
        });

        // Upsert again with new path
        await Sut.UpsertThumbnailAsync(new Thumbnail
        {
            PhotoId          = photoId,
            ThumbPath        = "C:/cache/new.jpg",
            SourceModifiedAt = "2024-06-01T00:00:00Z",
        });

        var fetched = await Sut.GetThumbnailAsync(photoId);
        Assert.Equal("C:/cache/new.jpg", fetched!.ThumbPath);
    }

    // ── Settings ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveSettings_LoadSettings_RoundTrip()
    {
        var settings = new AppSettings
        {
            Language       = "zh-CN",
            Theme          = 2,
            PreloadCountForward = 4,
            RecursiveScan  = false,
            ScanDirectories = ["C:/Pictures", "D:/Photos"],
        };

        await Sut.SaveSettingsAsync(settings);
        var loaded = await Sut.LoadSettingsAsync();

        Assert.Equal("zh-CN",       loaded.Language);
        Assert.Equal(2,             loaded.Theme);
        Assert.Equal(4,             loaded.PreloadCountForward);
        Assert.False(loaded.RecursiveScan);
        Assert.Equal(2,             loaded.ScanDirectories.Count);
        Assert.Contains("C:/Pictures", loaded.ScanDirectories);
    }

    [Fact]
    public async Task LoadSettings_ReturnsDefaults_WhenNothingSaved()
    {
        var settings = await Sut.LoadSettingsAsync();

        Assert.Equal(string.Empty, settings.Language);
        Assert.Equal(0,            settings.Theme);
        Assert.Equal(5,            settings.PreloadCountForward);
        Assert.True(settings.RecursiveScan);
    }

    [Fact]
    public async Task SaveSettings_SecondSave_OverwritesPrevious()
    {
        await Sut.SaveSettingsAsync(new AppSettings { Language = "en-US" });
        await Sut.SaveSettingsAsync(new AppSettings { Language = "zh-CN" });

        var loaded = await Sut.LoadSettingsAsync();
        Assert.Equal("zh-CN", loaded.Language);
    }

    // ── Maintenance ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ClearPhotoCache_RemovesPhotosAndThumbnails_KeepsAlbums()
    {
        var albumId = await Sut.InsertAlbumAsync(new Album { Name = "Keep" });
        var photoId = await Sut.InsertPhotoAsync(MakePhoto("C:/photos/a.jpg", albumId));
        await Sut.UpsertThumbnailAsync(new Thumbnail
        {
            PhotoId          = photoId,
            ThumbPath        = "C:/cache/a.jpg",
            SourceModifiedAt = "2024-01-01T00:00:00Z",
        });

        await Sut.ClearPhotoCacheAsync();

        Assert.Empty(await Sut.GetAllPhotosAsync());
        Assert.Null(await Sut.GetThumbnailAsync(photoId));
        Assert.NotEmpty(await Sut.GetAlbumsAsync());     // album preserved
    }

    [Fact]
    public async Task ClearAllData_RemovesEverything()
    {
        await Sut.InsertAlbumAsync(new Album { Name = "Gone" });
        await Sut.InsertPhotoAsync(MakePhoto("C:/photos/gone.jpg"));
        await Sut.SaveSettingsAsync(new AppSettings { Language = "zh-CN" });

        await Sut.ClearAllDataAsync();

        Assert.Empty(await Sut.GetAlbumsAsync());
        Assert.Empty(await Sut.GetAllPhotosAsync());
        // Settings should be gone too — next load returns defaults
        var s = await Sut.LoadSettingsAsync();
        Assert.Equal(string.Empty, s.Language);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Photo MakePhoto(string path, long? albumId = null) => new()
    {
        FilePath   = path,
        FileName   = Path.GetFileName(path),
        FileSize   = 1024,
        CreatedAt  = DateTime.UtcNow.ToString("O"),
        ModifiedAt = DateTime.UtcNow.ToString("O"),
        AlbumId    = albumId,
    };

    // ── Inner test factory ────────────────────────────────────────────────────

    /// <summary>
    /// Keeps a single <see cref="SqliteConnection"/> open for the lifetime of
    /// the test so that the in-memory SQLite database survives across multiple
    /// <see cref="GalleryDbContext"/> instances (EF Core closes its own connection
    /// on disposal, which would wipe the in-memory DB between operations).
    /// </summary>
    private sealed class TestDbContextFactory : IDbContextFactory<GalleryDbContext>, IDisposable
    {
        private readonly SqliteConnection _keepAlive;
        private readonly DbContextOptions<GalleryDbContext> _options;

        public TestDbContextFactory()
        {
            _keepAlive = new SqliteConnection("Data Source=:memory:");
            _keepAlive.Open();

            _options = new DbContextOptionsBuilder<GalleryDbContext>()
                .UseSqlite(_keepAlive)
                .Options;
        }

        public GalleryDbContext CreateDbContext() => new(_options);

        public Task<GalleryDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(new GalleryDbContext(_options));

        public void Dispose() => _keepAlive.Dispose();
    }
}
