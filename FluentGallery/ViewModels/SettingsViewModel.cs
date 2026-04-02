using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentGallery.Data;
using FluentGallery.Helpers;
using FluentGallery.Models;
using FluentGallery.Services;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace FluentGallery.ViewModels;

/// <summary>
/// ViewModel for <see cref="FluentGallery.Views.SettingsPage"/> (spec §5.6).
/// Loads/saves <see cref="AppSettings"/> from the database and exposes
/// bindable properties for every settings group.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly DatabaseService _db;
    private readonly IThemeService   _themeService;
    private readonly ILogger<SettingsViewModel> _logger;
    private AppSettings _settings = new();

    // Prevents property-change handlers from issuing redundant saves while
    // the initial values are being loaded.
    private bool _isInitialized;

    // ── Language index mapping ──────────────────────────────────────────
    // Index 0 = follow system, 1 = en-US, 2 = zh-CN
    private static readonly string[] LanguageTags = ["", "en-US", "zh-CN"];

    // ── Scan ────────────────────────────────────────────────────────────
    public ObservableCollection<string> ScanDirectories    { get; } = [];
    public ObservableCollection<string> ExcludeDirectories { get; } = [];

    // ── Appearance ──────────────────────────────────────────────────────
    [ObservableProperty] public partial int SelectedLanguageIndex { get; set; }
    [ObservableProperty] public partial int Theme                 { get; set; }

    // ── Behaviour ───────────────────────────────────────────────────────
    [ObservableProperty] public partial bool ConfirmBeforeDelete { get; set; }
    [ObservableProperty] public partial int  PreloadCount        { get; set; }

    // ── Cache display ───────────────────────────────────────────────────
    [ObservableProperty] public partial string ThumbnailCacheSizeText { get; set; } = "计算中…";
    [ObservableProperty] public partial bool   IsLoading              { get; set; }

    // ── Status feedback ─────────────────────────────────────────────────
    [ObservableProperty] public partial string? StatusMessage    { get; set; }
    [ObservableProperty] public partial bool    HasStatusMessage { get; set; }

    /// <summary>True → show as warning/error; false → show as success/informational.</summary>
    [ObservableProperty] public partial bool    IsWarningStatus  { get; set; }

    partial void OnStatusMessageChanged(string? value)
        => HasStatusMessage = !string.IsNullOrEmpty(value);

    public SettingsViewModel(
        DatabaseService db,
        IThemeService themeService,
        ILogger<SettingsViewModel> logger)
    {
        _db           = db;
        _themeService = themeService;
        _logger       = logger;
    }

    // ────────────────────────────────────────────────────────────────────
    // Load / Save
    // ────────────────────────────────────────────────────────────────────

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct = default)
    {
        _isInitialized = false;
        IsLoading = true;
        try
        {
            _settings = await _db.LoadSettingsAsync(ct);

            ScanDirectories.Clear();
            foreach (var d in _settings.ScanDirectories)
                ScanDirectories.Add(d);

            ExcludeDirectories.Clear();
            foreach (var d in _settings.ExcludeDirectories)
                ExcludeDirectories.Add(d);

            Theme               = _settings.Theme;
            ConfirmBeforeDelete = _settings.ConfirmBeforeDelete;
            PreloadCount        = _settings.PreloadCount;

            var tag = _settings.Language ?? string.Empty;
            var idx = Array.IndexOf(LanguageTags, tag);
            SelectedLanguageIndex = idx >= 0 ? idx : 0;

            await RefreshThumbnailCacheSizeAsync(ct);
        }
        finally
        {
            _isInitialized = true;
            IsLoading = false;
        }
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        _settings.ScanDirectories     = [.. ScanDirectories];
        _settings.ExcludeDirectories  = [.. ExcludeDirectories];
        _settings.RecursiveScan       = true;   // always recursive
        _settings.Theme               = Theme;
        _settings.ConfirmBeforeDelete = ConfirmBeforeDelete;
        _settings.PreloadCount        = PreloadCount;

        var langIdx = SelectedLanguageIndex;
        _settings.Language = langIdx >= 0 && langIdx < LanguageTags.Length
            ? LanguageTags[langIdx]
            : string.Empty;

        await _db.SaveSettingsAsync(_settings, ct);
        _logger.LogDebug("Settings saved");
    }

    // ────────────────────────────────────────────────────────────────────
    // Scan directories
    // ────────────────────────────────────────────────────────────────────

    public void AddScanDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (ScanDirectories.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        ScanDirectories.Add(path);
        _ = SaveAsync();
    }

    /// <summary>
    /// Batch-adds multiple scan directories in one operation.
    /// Duplicates (case-insensitive) are silently skipped.
    /// Calls <see cref="SaveAsync"/> at most once when at least one path was added.
    /// </summary>
    public void AddScanDirectories(IEnumerable<string> paths)
    {
        bool added = false;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (ScanDirectories.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
            ScanDirectories.Add(path);
            added = true;
        }
        if (added) _ = SaveAsync();
    }

    public void RemoveScanDirectory(string path)
    {
        if (ScanDirectories.Remove(path))
            _ = SaveAsync();
    }

    public void AddExcludeDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (ExcludeDirectories.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        ExcludeDirectories.Add(path);
        _ = SaveAsync();
    }

    /// <summary>
    /// Batch-adds multiple exclude directories in one operation.
    /// </summary>
    public void AddExcludeDirectories(IEnumerable<string> paths)
    {
        bool added = false;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (ExcludeDirectories.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
            ExcludeDirectories.Add(path);
            added = true;
        }
        if (added) _ = SaveAsync();
    }

    public void RemoveExcludeDirectory(string path)
    {
        if (ExcludeDirectories.Remove(path))
            _ = SaveAsync();
    }

    // ────────────────────────────────────────────────────────────────────
    // Auto-save on property changes (only after initial load)
    // ────────────────────────────────────────────────────────────────────

    partial void OnThemeChanged(int value)
    {
        if (!_isInitialized) return;
        _themeService.Apply(value);
        _ = SaveAsync();
    }

    partial void OnSelectedLanguageIndexChanged(int value)
    {
        if (!_isInitialized) return;
        _ = SaveAsync();
        IsWarningStatus = true;
        StatusMessage   = "语言将在下次启动应用时生效";
    }

    partial void OnConfirmBeforeDeleteChanged(bool value)
    {
        if (!_isInitialized) return;
        _ = SaveAsync();
    }

    partial void OnPreloadCountChanged(int value)
    {
        if (!_isInitialized) return;
        _ = SaveAsync();
    }

    // ────────────────────────────────────────────────────────────────────
    // Cache management
    // ────────────────────────────────────────────────────────────────────

    private async Task RefreshThumbnailCacheSizeAsync(CancellationToken ct = default)
    {
        try
        {
            var dir = AppDataPaths.ThumbnailsDirectory;
            if (!Directory.Exists(dir))
            {
                ThumbnailCacheSizeText = "0 B";
                return;
            }

            long total = await Task.Run(() =>
                new DirectoryInfo(dir)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Sum(f => f.Length), ct);

            ThumbnailCacheSizeText = FormatBytes(total);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to compute thumbnail cache size");
            ThumbnailCacheSizeText = "未知";
        }
    }

    [RelayCommand]
    public async Task ClearThumbnailCacheAsync(CancellationToken ct = default)
    {
        try
        {
            var dir = AppDataPaths.ThumbnailsDirectory;
            if (Directory.Exists(dir))
            {
                await Task.Run(() =>
                {
                    foreach (var f in Directory.EnumerateFiles(dir, "*.jpg",
                                 SearchOption.AllDirectories))
                        File.Delete(f);
                }, ct);
            }
            await RefreshThumbnailCacheSizeAsync(ct);
            IsWarningStatus = false;
            StatusMessage   = "缩略图缓存已清除";
            _logger.LogInformation("Thumbnail cache cleared");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear thumbnail cache");
            IsWarningStatus = true;
            StatusMessage   = $"清除失败：{ex.Message}";
        }
    }

    [RelayCommand]
    public async Task ClearDatabaseCacheAsync(CancellationToken ct = default)
    {
        try
        {
            await _db.ClearPhotoCacheAsync(ct);
            IsWarningStatus = false;
            StatusMessage   = "数据库缓存已清除（照片和缩略图记录已删除，相册结构保留）";
            _logger.LogInformation("Database photo cache cleared");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear database cache");
            IsWarningStatus = true;
            StatusMessage   = $"清除失败：{ex.Message}";
        }
    }

    [RelayCommand]
    public async Task ClearAllDataAsync(CancellationToken ct = default)
    {
        try
        {
            await _db.ClearAllDataAsync(ct);

            var dir = AppDataPaths.ThumbnailsDirectory;
            if (Directory.Exists(dir))
            {
                await Task.Run(() =>
                {
                    foreach (var f in Directory.EnumerateFiles(dir, "*.jpg",
                                 SearchOption.AllDirectories))
                        File.Delete(f);
                }, ct);
            }

            ThumbnailCacheSizeText = "0 B";
            ScanDirectories.Clear();
            ExcludeDirectories.Clear();
            IsWarningStatus = false;
            StatusMessage   = "所有数据已清除，应用已恢复出厂状态";
            _logger.LogInformation("All application data cleared");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear all data");
            IsWarningStatus = true;
            StatusMessage   = $"清除失败：{ex.Message}";
        }
    }

    // ────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024                  => $"{bytes} B",
        < 1024 * 1024           => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024   => $"{bytes / (1024.0 * 1024):F1} MB",
        _                       => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
    };
}
