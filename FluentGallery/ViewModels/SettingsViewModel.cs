using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentGallery.Data;
using FluentGallery.Helpers;
using FluentGallery.Models;
using FluentGallery.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace FluentGallery.ViewModels;

/// <summary>
/// ViewModel for <see cref="FluentGallery.Views.SettingsPage"/> (spec §5.6).
/// Loads/saves <see cref="AppSettings"/> from the database and exposes
/// bindable properties for every settings group.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly DatabaseService            _db;
    private readonly ThumbnailService           _thumbnailService;
    private readonly ScanService                _scan;
    private readonly IThemeService              _themeService;
    private readonly ILogger<SettingsViewModel> _logger;
    private AppSettings       _settings    = new();
    private DispatcherQueue?  _dispatcher;

    // Prevents property-change handlers from issuing redundant saves while
    // the initial values are being loaded.
    private bool _isInitialized;

    // Debounce token for thumbnail-size slider: cancelled on each new tick,
    // so save + hint only fire after the user stops dragging.
    private CancellationTokenSource? _sizeDebounce;

    // ── Language index mapping ──────────────────────────────────────────
    // Index 0 = follow system, 1 = en-US, 2 = zh-CN
    private static readonly string[] LanguageTags = ["", "en-US", "zh-CN"];

    // ── Thumbnail size options ──────────────────────────────────────────
    public static readonly int[] ThumbnailSizeOptions = [128, 256, 384, 512, 768, 1024, 1536, 2048];

    // ── Scan ────────────────────────────────────────────────────────────
    public ObservableCollection<string> ScanDirectories    { get; } = [];
    public ObservableCollection<string> ExcludeDirectories { get; } = [];

    // ── Appearance ──────────────────────────────────────────────────────
    [ObservableProperty] public partial int SelectedLanguageIndex { get; set; }
    [ObservableProperty] public partial int Theme                 { get; set; }

    // ── Behaviour ───────────────────────────────────────────────────────
    [ObservableProperty] public partial bool ConfirmBeforeDelete  { get; set; }
    [ObservableProperty] public partial int  PreloadCount         { get; set; }
    [ObservableProperty] public partial int  ThumbnailSizeIndex   { get; set; }

    /// <summary>Actual pixel value for the currently selected thumbnail size index.</summary>
    public int ThumbnailSizePixels
        => ThumbnailSizeOptions[Math.Clamp(ThumbnailSizeIndex, 0, ThumbnailSizeOptions.Length - 1)];

    // ── Thumbnail-size hint bar ──────────────────────────────────────────
    /// <summary>
    /// True while the "thumbnail size changed – clear cache?" InfoBar is visible.
    /// Bind IsOpen TwoWay so the InfoBar hides itself when the user closes it.
    /// </summary>
    [ObservableProperty] public partial bool ShowThumbnailSizeHint { get; set; }

    // ── Cache display ───────────────────────────────────────────────────
    [ObservableProperty] public partial string ThumbnailCacheSizeText { get; set; } = "计算中…";
    [ObservableProperty] public partial bool   IsLoading              { get; set; }

    /// <summary>Shown as the SettingsExpander description, merges cache size with hint.</summary>
    public string ThumbnailExpanderDescription => $"缓存大小：{ThumbnailCacheSizeText}";

    partial void OnThumbnailCacheSizeTextChanged(string value)
        => OnPropertyChanged(nameof(ThumbnailExpanderDescription));

    // ── Status feedback ─────────────────────────────────────────────────
    [ObservableProperty] public partial string? StatusMessage    { get; set; }
    [ObservableProperty] public partial bool    HasStatusMessage { get; set; }

    /// <summary>True → show as warning/error; false → show as success/informational.</summary>
    [ObservableProperty] public partial bool    IsWarningStatus  { get; set; }

    // ── Thumbnail batch generation ───────────────────────────────────────
    private CancellationTokenSource? _buildCts;

    [ObservableProperty] public partial bool   IsBuildingThumbnails    { get; set; }
    [ObservableProperty] public partial bool   IsThumbnailBuildDone    { get; set; }
    [ObservableProperty] public partial int    ThumbnailBuildTotal     { get; set; }
    [ObservableProperty] public partial int    ThumbnailBuildCompleted { get; set; }
    [ObservableProperty] public partial double ThumbnailBuildSpeed     { get; set; }
    [ObservableProperty] public partial string ThumbnailBuildEtaText   { get; set; } = "";

    /// <summary>True when the "立即生成" button should be enabled.</summary>
    public bool ShowGenerateButton => !IsBuildingThumbnails && !IsThumbnailBuildDone;

    /// <summary>True while querying the database (total not yet known).</summary>
    public bool IsThumbnailBuildIndeterminate => IsBuildingThumbnails && ThumbnailBuildTotal == 0;

    /// <summary>0–100 value for the ProgressBar.</summary>
    public double ThumbnailBuildProgress
        => ThumbnailBuildTotal > 0 ? ThumbnailBuildCompleted * 100.0 / ThumbnailBuildTotal : 0;

    public string ThumbnailBuildProgressText
        => ThumbnailBuildTotal > 0
            ? $"已生成 {ThumbnailBuildCompleted} / {ThumbnailBuildTotal} 张"
            : (IsBuildingThumbnails ? "正在查询…" : "");

    public string ThumbnailBuildStatsText
        => IsBuildingThumbnails && ThumbnailBuildSpeed > 0
            ? $"速度：{ThumbnailBuildSpeed:F1} 张/秒　{ThumbnailBuildEtaText}"
            : "";

    partial void OnIsBuildingThumbnailsChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowGenerateButton));
        OnPropertyChanged(nameof(IsThumbnailBuildIndeterminate));
        OnPropertyChanged(nameof(ThumbnailBuildProgressText));
    }

    partial void OnIsThumbnailBuildDoneChanged(bool value)
        => OnPropertyChanged(nameof(ShowGenerateButton));

    partial void OnThumbnailBuildTotalChanged(int value)
    {
        OnPropertyChanged(nameof(ThumbnailBuildProgress));
        OnPropertyChanged(nameof(ThumbnailBuildProgressText));
        OnPropertyChanged(nameof(IsThumbnailBuildIndeterminate));
    }

    partial void OnThumbnailBuildCompletedChanged(int value)
    {
        OnPropertyChanged(nameof(ThumbnailBuildProgress));
        OnPropertyChanged(nameof(ThumbnailBuildProgressText));
    }

    partial void OnThumbnailBuildSpeedChanged(double value)
        => OnPropertyChanged(nameof(ThumbnailBuildStatsText));

    partial void OnThumbnailBuildEtaTextChanged(string value)
        => OnPropertyChanged(nameof(ThumbnailBuildStatsText));

    partial void OnStatusMessageChanged(string? value)
        => HasStatusMessage = !string.IsNullOrEmpty(value);

    public SettingsViewModel(
        DatabaseService            db,
        ThumbnailService           thumbnailService,
        ScanService                scan,
        IThemeService              themeService,
        ILogger<SettingsViewModel> logger)
    {
        _db               = db;
        _thumbnailService = thumbnailService;
        _scan             = scan;
        _themeService     = themeService;
        _logger           = logger;
    }

    // ────────────────────────────────────────────────────────────────────
    // Load / Save
    // ────────────────────────────────────────────────────────────────────

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct = default)
    {
        _dispatcher    = DispatcherQueue.GetForCurrentThread();
        _isInitialized = false;
        IsLoading      = true;
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

            var sizeIdx = Array.IndexOf(ThumbnailSizeOptions, _settings.ThumbnailSize);
            ThumbnailSizeIndex = sizeIdx >= 0 ? sizeIdx : Array.IndexOf(ThumbnailSizeOptions, 512);

            var tag = _settings.Language ?? string.Empty;
            var idx = Array.IndexOf(LanguageTags, tag);
            SelectedLanguageIndex = idx >= 0 ? idx : 0;

            _thumbnailService.ThumbSize = (uint)ThumbnailSizePixels;

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
        _settings.ThumbnailSize       = ThumbnailSizePixels;

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
        _ = SaveAndRescanAsync();
    }

    /// <summary>
    /// Batch-adds multiple scan directories in one operation.
    /// Duplicates (case-insensitive) are silently skipped.
    /// Triggers a single save + re-scan when at least one path was added.
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
        if (added) _ = SaveAndRescanAsync();
    }

    public void RemoveScanDirectory(string path)
    {
        if (ScanDirectories.Remove(path))
            _ = SaveAndRescanAsync();
    }

    public void AddExcludeDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (ExcludeDirectories.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        ExcludeDirectories.Add(path);
        _ = SaveAndRescanAsync();
    }

    /// <summary>
    /// Batch-adds multiple exclude directories in one operation.
    /// Triggers a single save + re-scan when at least one path was added.
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
        if (added) _ = SaveAndRescanAsync();
    }

    public void RemoveExcludeDirectory(string path)
    {
        if (ExcludeDirectories.Remove(path))
            _ = SaveAndRescanAsync();
    }

    /// <summary>
    /// Saves current settings then starts a fresh background scan so that
    /// newly-added directories are indexed and removed directories are pruned.
    /// No thumbnails are generated proactively during the scan.
    /// </summary>
    private async Task SaveAndRescanAsync()
    {
        await SaveAsync();
        await _scan.StartAsync(_settings, _dispatcher);
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

    partial void OnThumbnailSizeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(ThumbnailSizePixels));
        if (!_isInitialized) return;

        // Debounce: cancel any pending delayed commit and start a new one.
        _sizeDebounce?.Cancel();
        _sizeDebounce = new CancellationTokenSource();
        _ = CommitThumbnailSizeAsync(_sizeDebounce.Token);
    }

    private async Task CommitThumbnailSizeAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(600, ct);          // wait for slider to settle

            _thumbnailService.ThumbSize = (uint)ThumbnailSizePixels;
            await SaveAsync(ct);
            ShowThumbnailSizeHint = true;       // show the "clear cache?" bar
        }
        catch (OperationCanceledException) { }  // user moved slider again — ignore
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
    // Logs folder
    // ────────────────────────────────────────────────────────────────────

    /// <summary>Opens the thumbnails cache directory in Windows Explorer.</summary>
    [RelayCommand]
    public void OpenThumbnailsFolder()
    {
        var dir = AppDataPaths.ThumbnailsDirectory;
        Directory.CreateDirectory(dir);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = dir,
                UseShellExecute = true,
            });
            _logger.LogInformation("用户打开了缩略图目录: {Dir}", dir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "无法打开缩略图目录");
            IsWarningStatus = true;
            StatusMessage   = $"无法打开缩略图目录：{ex.Message}";
        }
    }

    /// <summary>Opens the logs directory in Windows Explorer.</summary>
    [RelayCommand]
    public void OpenLogsFolder()
    {
        var logsDir = AppDataPaths.LogsDirectory;
        Directory.CreateDirectory(logsDir);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = logsDir,
                UseShellExecute = true,
            });
            _logger.LogInformation("用户打开了日志目录: {Dir}", logsDir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "无法打开日志目录");
            IsWarningStatus = true;
            StatusMessage   = $"无法打开日志目录：{ex.Message}";
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

    // ────────────────────────────────────────────────────────────────────
    // Batch thumbnail generation
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Queries photos with missing or stale thumbnails and generates them all,
    /// reporting speed and ETA throughout. Shows a completion animation when done.
    /// Always shows the animation even when there is nothing to generate.
    /// </summary>
    [RelayCommand]
    public async Task GenerateMissingThumbnailsAsync()
    {
        if (IsBuildingThumbnails) return;

        IsBuildingThumbnails    = true;
        IsThumbnailBuildDone    = false;
        ThumbnailBuildTotal     = 0;
        ThumbnailBuildCompleted = 0;
        ThumbnailBuildSpeed     = 0;
        ThumbnailBuildEtaText   = "";

        _buildCts?.Dispose();
        _buildCts = new CancellationTokenSource();
        var localCts  = _buildCts;
        bool completed = false;

        try
        {
            var photos = await _db.GetPhotosWithoutThumbnailAsync(localCts.Token);
            ThumbnailBuildTotal = photos.Count;

            if (photos.Count > 0)
            {
                // Track last cache-size refresh so we update at most every 500 ms.
                // Progress<T> always marshals to the UI thread — no concurrent access.
                long lastCacheRefreshTick = 0L;

                var progress = new Progress<ThumbnailBatchProgress>(p =>
                {
                    ThumbnailBuildCompleted = p.Done;
                    ThumbnailBuildSpeed     = p.SpeedPerSec;
                    ThumbnailBuildEtaText   = p.Eta.HasValue ? FormatEta(p.Eta.Value) : "";

                    var now = Environment.TickCount64;
                    if (now - lastCacheRefreshTick >= 500)
                    {
                        lastCacheRefreshTick = now;
                        _ = RefreshThumbnailCacheSizeAsync();
                    }
                });

                await Task.Run(
                    () => _thumbnailService.GenerateMissingAsync(photos, progress, localCts.Token),
                    localCts.Token);
            }

            completed = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "批量生成缩略图失败");
            IsWarningStatus = true;
            StatusMessage   = $"生成缩略图失败：{ex.Message}";
        }
        finally
        {
            IsBuildingThumbnails = false;
            localCts.Dispose();
            if (ReferenceEquals(_buildCts, localCts))
                _buildCts = null;
        }

        if (completed)
        {
            // Final precise cache-size update before showing the done animation
            await RefreshThumbnailCacheSizeAsync();
            IsThumbnailBuildDone = true;
            await Task.Delay(4500);   // hold the done animation before auto-dismiss
            IsThumbnailBuildDone = false;
        }
    }

    /// <summary>Cancels an in-progress batch thumbnail generation.</summary>
    [RelayCommand]
    public void CancelThumbnailBuild() => _buildCts?.Cancel();

    private static string FormatEta(TimeSpan eta)
    {
        if (eta.TotalSeconds < 60)  return $"预计剩余 {(int)eta.TotalSeconds} 秒";
        if (eta.TotalMinutes < 60)  return $"预计剩余 {(int)eta.TotalMinutes} 分 {eta.Seconds} 秒";
        return $"预计剩余 {(int)eta.TotalHours} 小时 {eta.Minutes} 分";
    }
}
