using FluentGallery.Data;
using FluentGallery.Models;
using FluentGallery.Services;
using FluentGallery.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FluentGallery.Tests;

/// <summary>
/// Unit tests for <see cref="SettingsViewModel"/>.
/// All tests use an isolated in-memory SQLite database; IThemeService is a no-op stub.
/// </summary>
public sealed class SettingsViewModelTests : IAsyncLifetime
{
    // ── Fixture ──────────────────────────────────────────────────────────

    private TestDbContextFactory? _factory;
    private DatabaseService?      _db;
    private SettingsViewModel?    _sut;

    public async Task InitializeAsync()
    {
        _factory = new TestDbContextFactory();
        _db      = new DatabaseService(_factory, NullLogger<DatabaseService>.Instance);
        await _db.InitializeAsync();
        _sut = BuildSut();
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    private SettingsViewModel BuildSut()
        => new(_db!, new NullThemeService(), NullLogger<SettingsViewModel>.Instance);

    private SettingsViewModel Sut => _sut!;

    // ── AddScanDirectory ─────────────────────────────────────────────────

    [Fact]
    public void AddScanDirectory_AppendsToCollection()
    {
        Sut.AddScanDirectory(@"C:\Photos");

        Assert.Single(Sut.ScanDirectories);
        Assert.Equal(@"C:\Photos", Sut.ScanDirectories[0]);
    }

    [Fact]
    public void AddScanDirectory_IgnoresDuplicates_CaseInsensitive()
    {
        Sut.AddScanDirectory(@"C:\Photos");
        Sut.AddScanDirectory(@"c:\photos");   // same path, different case

        Assert.Single(Sut.ScanDirectories);
    }

    [Fact]
    public void AddScanDirectory_IgnoresBlankPaths()
    {
        Sut.AddScanDirectory("");
        Sut.AddScanDirectory("   ");

        Assert.Empty(Sut.ScanDirectories);
    }

    [Fact]
    public async Task AddScanDirectory_PersistsToDatabase()
    {
        Sut.AddScanDirectory(@"C:\Photos");
        await Task.Delay(50); // let the fire-and-forget SaveAsync complete

        var loaded = await _db!.LoadSettingsAsync();
        Assert.Contains(@"C:\Photos", loaded.ScanDirectories);
    }

    // ── RemoveScanDirectory ──────────────────────────────────────────────

    [Fact]
    public void RemoveScanDirectory_RemovesFromCollection()
    {
        Sut.AddScanDirectory(@"C:\A");
        Sut.AddScanDirectory(@"C:\B");

        Sut.RemoveScanDirectory(@"C:\A");

        Assert.Single(Sut.ScanDirectories);
        Assert.Equal(@"C:\B", Sut.ScanDirectories[0]);
    }

    [Fact]
    public void RemoveScanDirectory_NonexistentPath_DoesNotThrow()
    {
        var ex = Record.Exception(() => Sut.RemoveScanDirectory(@"C:\DoesNotExist"));
        Assert.Null(ex);
    }

    [Fact]
    public async Task RemoveScanDirectory_PersistsToDatabase()
    {
        Sut.AddScanDirectory(@"C:\Keep");
        Sut.AddScanDirectory(@"C:\Remove");
        await Task.Delay(50);

        Sut.RemoveScanDirectory(@"C:\Remove");
        await Task.Delay(50);

        var loaded = await _db!.LoadSettingsAsync();
        Assert.Contains(@"C:\Keep", loaded.ScanDirectories);
        Assert.DoesNotContain(@"C:\Remove", loaded.ScanDirectories);
    }

    // ── AddScanDirectories (batch) ────────────────────────────────────────

    [Fact]
    public void AddScanDirectories_AddsAllPaths()
    {
        Sut.AddScanDirectories([@"C:\A", @"C:\B", @"C:\C"]);

        Assert.Equal(3, Sut.ScanDirectories.Count);
        Assert.Contains(@"C:\A", Sut.ScanDirectories);
        Assert.Contains(@"C:\B", Sut.ScanDirectories);
        Assert.Contains(@"C:\C", Sut.ScanDirectories);
    }

    [Fact]
    public void AddScanDirectories_SkipsDuplicatesAcrossBatch()
    {
        Sut.AddScanDirectory(@"C:\Existing");
        Sut.AddScanDirectories([@"C:\Existing", @"C:\New", @"c:\existing"]); // 2 duplicates

        Assert.Equal(2, Sut.ScanDirectories.Count);
        Assert.Contains(@"C:\New", Sut.ScanDirectories);
    }

    [Fact]
    public async Task AddScanDirectories_TriggersOneDbSave()
    {
        Sut.AddScanDirectories([@"C:\P1", @"C:\P2", @"C:\P3"]);
        await Task.Delay(80);

        var loaded = await _db!.LoadSettingsAsync();
        Assert.Equal(3, loaded.ScanDirectories.Count);
    }

    [Fact]
    public void AddScanDirectories_EmptyList_DoesNothing()
    {
        Sut.AddScanDirectories([]);
        Assert.Empty(Sut.ScanDirectories);
    }

    // ── ExcludeDirectories ───────────────────────────────────────────────

    [Fact]
    public void AddExcludeDirectory_AppendsToCollection()
    {
        Sut.AddExcludeDirectory(@"C:\System");

        Assert.Single(Sut.ExcludeDirectories);
        Assert.Equal(@"C:\System", Sut.ExcludeDirectories[0]);
    }

    [Fact]
    public void RemoveExcludeDirectory_RemovesFromCollection()
    {
        Sut.AddExcludeDirectory(@"C:\System");
        Sut.RemoveExcludeDirectory(@"C:\System");

        Assert.Empty(Sut.ExcludeDirectories);
    }

    [Fact]
    public void AddExcludeDirectories_AddsAllPaths()
    {
        Sut.AddExcludeDirectories([@"C:\Windows", @"C:\Temp"]);

        Assert.Equal(2, Sut.ExcludeDirectories.Count);
    }

    // ── LoadAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_PopulatesCollectionsFromDatabase()
    {
        // Arrange: save settings directly to DB
        await _db!.SaveSettingsAsync(new AppSettings
        {
            ScanDirectories    = [@"C:\A", @"C:\B"],
            ExcludeDirectories = [@"C:\Skip"],
            RecursiveScan      = false,
            Theme              = 2,
            PreloadCount       = 4,
            ConfirmBeforeDelete = false,
        });

        // Act
        var vm = BuildSut();
        await vm.LoadAsync();

        // Assert
        Assert.Equal(2, vm.ScanDirectories.Count);
        Assert.Contains(@"C:\A", vm.ScanDirectories);
        Assert.Contains(@"C:\B", vm.ScanDirectories);
        Assert.Single(vm.ExcludeDirectories);
        Assert.Equal(@"C:\Skip", vm.ExcludeDirectories[0]);
        Assert.Equal(2, vm.Theme);
        Assert.Equal(4, vm.PreloadCount);
        Assert.False(vm.ConfirmBeforeDelete);
    }

    [Fact]
    public async Task LoadAsync_UsesDefaults_WhenNothingSaved()
    {
        await Sut.LoadAsync();

        Assert.Empty(Sut.ScanDirectories);
        Assert.Empty(Sut.ExcludeDirectories);
        Assert.Equal(0, Sut.Theme);           // default = system
        Assert.Equal(5, Sut.PreloadCount);    // default = 2
        Assert.True(Sut.ConfirmBeforeDelete); // default = true
    }

    // ── Language index ────────────────────────────────────────────────────

    [Theory]
    [InlineData("",      0)]
    [InlineData("en-US", 1)]
    [InlineData("zh-CN", 2)]
    public async Task LoadAsync_MapsLanguageTagToIndex(string tag, int expectedIndex)
    {
        await _db!.SaveSettingsAsync(new AppSettings { Language = tag });
        var vm = BuildSut();
        await vm.LoadAsync();

        Assert.Equal(expectedIndex, vm.SelectedLanguageIndex);
    }

    // ── SaveAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveAsync_PersistsAllProperties()
    {
        await Sut.LoadAsync();

        Sut.AddScanDirectory(@"C:\Photos");
        Sut.AddExcludeDirectory(@"C:\Windows");
        Sut.Theme               = 1;
        Sut.PreloadCount        = 3;
        Sut.ConfirmBeforeDelete = false;
        Sut.SelectedLanguageIndex = 2; // zh-CN

        await Sut.SaveAsync();

        var loaded = await _db!.LoadSettingsAsync();
        Assert.Contains(@"C:\Photos",  loaded.ScanDirectories);
        Assert.Contains(@"C:\Windows", loaded.ExcludeDirectories);
        Assert.True(loaded.RecursiveScan);   // always true
        Assert.Equal(1,      loaded.Theme);
        Assert.Equal(3,      loaded.PreloadCount);
        Assert.False(loaded.ConfirmBeforeDelete);
        Assert.Equal("zh-CN", loaded.Language);
    }

    // ── StatusMessage ─────────────────────────────────────────────────────

    [Fact]
    public void StatusMessage_SetsHasStatusMessage()
    {
        Sut.StatusMessage = "Test message";

        Assert.True(Sut.HasStatusMessage);
    }

    [Fact]
    public void StatusMessage_ClearingHidesInfoBar()
    {
        Sut.StatusMessage = "msg";
        Sut.StatusMessage = null;

        Assert.False(Sut.HasStatusMessage);
    }

    // ── ClearAllData ──────────────────────────────────────────────────────

    [Fact]
    public async Task ClearAllData_ClearsCollectionsAndSetsSuccessMessage()
    {
        Sut.AddScanDirectory(@"C:\Photos");
        Sut.AddExcludeDirectory(@"C:\System");
        await Task.Delay(50);

        await Sut.ClearAllDataAsync();

        Assert.Empty(Sut.ScanDirectories);
        Assert.Empty(Sut.ExcludeDirectories);
        Assert.False(Sut.IsWarningStatus);
        Assert.True(Sut.HasStatusMessage);
    }

    // ── Inner helpers ─────────────────────────────────────────────────────

    /// <summary>No-op theme service for unit tests (avoids WinUI runtime dependency).</summary>
    private sealed class NullThemeService : IThemeService
    {
        public void Apply(int theme) { }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<GalleryDbContext>, IDisposable
    {
        private readonly SqliteConnection                _keepAlive;
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
