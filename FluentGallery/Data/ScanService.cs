using FluentGallery.Models;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace FluentGallery.Data;

// ── Progress DTO ─────────────────────────────────────────────────────────────

/// <summary>
/// Snapshot of the scan progress, delivered via <see cref="ScanService.ProgressChanged"/>.
/// </summary>
public sealed record ScanProgress(
    int    Scanned,
    int    Discovered,
    bool   IsCompleted = false,
    string CurrentFile = "");

// ── Service ──────────────────────────────────────────────────────────────────

/// <summary>
/// Background directory scanner.
///
/// Performance design:
/// <list type="bullet">
///   <item>All DB existence checks use a single pre-fetched in-memory dictionary
///         (<see cref="DatabaseService.GetAllPhotoMetadataAsync"/>) — no per-file round trips.</item>
///   <item><see cref="ProgressChanged"/> is throttled to at most once per
///         <see cref="ProgressThrottleMs"/> milliseconds.</item>
///   <item>New/updated <see cref="Photo"/> objects are queued and flushed to the UI thread
///         in batches every <see cref="BatchFlushMs"/> ms, keeping ObservableCollection
///         updates coarse-grained rather than one-per-file.</item>
///   <item>Worker concurrency is capped at <c>max(1, CPU/2)</c> to leave headroom for the UI.</item>
/// </list>
/// </summary>
public sealed class ScanService : IDisposable
{
    // ── Tuning constants ──────────────────────────────────────────────────────

    private const int ProgressThrottleMs = 250;   // UI progress bar refresh rate
    private const int BatchFlushMs       = 500;   // ObservableCollection batch interval

    private static readonly string[] SupportedExtensions =
        [".jpg", ".jpeg", ".png", ".bmp", ".heic", ".heif", ".webp", ".gif"];

    // ── Dependencies ──────────────────────────────────────────────────────────

    private readonly DatabaseService          _db;
    private readonly ExifService              _exif;
    private readonly ILogger<ScanService>     _logger;

    // ── Scan lifecycle ────────────────────────────────────────────────────────

    private CancellationTokenSource? _cts;
    private Task?                    _scanTask;

    // ── Pending-photo batch queues (drained periodically onto the UI thread) ──

    private readonly ConcurrentQueue<Photo> _pendingDiscovered = new();
    private readonly ConcurrentQueue<Photo> _pendingUpdated    = new();

    // ── Per-scan counters (reset at scan start) ───────────────────────────────

    private int _countNew;
    private int _countUpdated;
    private int _countSkipped;

    // ── Album-id cache (parent-dir → album id, persists across scans) ─────────
    //    Uses a double-check lock pattern so only one thread creates each album.

    private readonly ConcurrentDictionary<string, long> _albumIdCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _albumCreateLock = new(1, 1);

    // ── Progress throttle ─────────────────────────────────────────────────────

    private long _lastProgressTick; // Environment.TickCount64 of last dispatch

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Raised on the UI thread at most every <see cref="ProgressThrottleMs"/> ms.
    /// </summary>
    public event Action<ScanProgress>? ProgressChanged;

    /// <summary>
    /// Raised on the UI thread with a batch of newly inserted photos.
    /// Batches are flushed every <see cref="BatchFlushMs"/> ms and once on completion.
    /// </summary>
    public event Action<IReadOnlyList<Photo>>? PhotosBatchDiscovered;

    /// <summary>
    /// Raised on the UI thread with a batch of updated photos.
    /// </summary>
    public event Action<IReadOnlyList<Photo>>? PhotosBatchUpdated;

    /// <summary>
    /// Raised on the UI thread once when a scan finishes successfully (not raised on cancellation).
    /// Subscribers can use this to reload the album list and apply any staleness cleanup.
    /// </summary>
    public event Action? ScanCompleted;

    // ── State ─────────────────────────────────────────────────────────────────

    /// <summary><c>true</c> while a scan is in progress.</summary>
    public bool IsScanning => _scanTask is { IsCompleted: false };

    // ── Constructor ───────────────────────────────────────────────────────────

    public ScanService(
        DatabaseService      db,
        ExifService          exif,
        ILogger<ScanService> logger)
    {
        _db     = db;
        _exif   = exif;
        _logger = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a background scan. Cancels and awaits any previously running scan first.
    /// </summary>
    public async Task StartAsync(AppSettings settings, DispatcherQueue? dispatcher = null)
    {
        await StopAsync().ConfigureAwait(false);

        _cts      = new CancellationTokenSource();
        _scanTask = RunScanAsync(settings, dispatcher, _cts.Token);
    }

    /// <summary>
    /// Cancels the running scan and waits for it to finish cleanly.
    /// </summary>
    public async Task StopAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _cts.Dispose();
            _cts = null;
        }

        if (_scanTask is not null)
        {
            try   { await _scanTask.ConfigureAwait(false); }
            catch { /* OperationCanceledException or scan errors — already logged */ }
            _scanTask = null;
        }
    }

    // ── Core scan loop ────────────────────────────────────────────────────────

    private async Task RunScanAsync(
        AppSettings      settings,
        DispatcherQueue? dispatcher,
        CancellationToken ct)
    {
        // Reset per-scan state
        _countNew = _countUpdated = _countSkipped = 0;
        _albumIdCache.Clear(); // stale IDs cause FK failures if albums were deleted between scans

        _logger.LogInformation(
            "═══ 扫描开始 ═══  目录数: {DirCount}  递归: {Recursive}",
            settings.ScanDirectories.Count, settings.RecursiveScan);
        foreach (var d in settings.ScanDirectories)
            _logger.LogInformation("  扫描目录: {Dir}", d);

        if (settings.ScanDirectories.Count == 0)
        {
            _logger.LogInformation("未配置任何扫描目录，清除所有照片记录。");
            // No directories → treat every existing photo as stale and remove it.
            await _db.DeleteStalePhotosAsync([], ct).ConfigureAwait(false);
            await _db.DeleteEmptyAlbumsAsync(ct).ConfigureAwait(false);
            DispatchProgress(new ScanProgress(0, 0, IsCompleted: true), dispatcher, force: true);
            Dispatch(dispatcher, () => ScanCompleted?.Invoke());
            return;
        }

        // Verify scan directories exist (albums are created lazily per file, not here)
        foreach (var dir in settings.ScanDirectories)
        {
            if (!Directory.Exists(dir))
                _logger.LogWarning("扫描目录不存在，将跳过: {Dir}", dir);
        }

        // ── 1. Pre-fetch all known photo metadata from DB (single query) ───────
        //    Using an in-memory dictionary eliminates one DB round-trip per file.
        var knownPhotos = await _db.GetAllPhotoMetadataAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("数据库中已有照片记录: {N} 张", knownPhotos.Count);

        // ── 3. Enumerate files on a background thread ─────────────────────────
        var allFiles = await Task.Run(() => EnumerateFiles(settings), ct).ConfigureAwait(false);
        int total    = allFiles.Count;
        _logger.LogInformation("磁盘上共找到支持格式的文件: {Total} 个", total);

        // ── 4. Start periodic UI-flush task ───────────────────────────────────
        using var flushCts  = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var flushTask = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(BatchFlushMs, flushCts.Token).ConfigureAwait(false);
                    FlushPendingPhotos(dispatcher);
                }
            }
            catch (OperationCanceledException) { }
        });

        // ── 5. Producer-consumer pipeline ─────────────────────────────────────
        int workerCount = Math.Max(1, Environment.ProcessorCount / 2);
        var channel     = Channel.CreateBounded<string>(new BoundedChannelOptions(workerCount * 8)
        {
            FullMode     = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = false,
        });

        int processed = 0;

        var workers = Enumerable.Range(0, workerCount).Select(_ => Task.Run(async () =>
        {
            await foreach (var path in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                // Skip files whose parent scan directory was removed after enumeration
                if (!BelongsToAnyScanDir(path, settings))
                {
                    _logger.LogDebug("跳过已移除目录中的文件: {Path}", path);
                    Interlocked.Increment(ref processed);
                    continue;
                }

                await ProcessFileAsync(path, knownPhotos, ct).ConfigureAwait(false);

                int n = Interlocked.Increment(ref processed);
                DispatchProgress(
                    new ScanProgress(n, total, CurrentFile: Path.GetFileName(path)),
                    dispatcher,
                    force: false);
            }
        }, ct)).ToArray();

        foreach (var path in allFiles)
        {
            if (ct.IsCancellationRequested) break;
            await channel.Writer.WriteAsync(path, ct).ConfigureAwait(false);
        }
        channel.Writer.Complete();

        await Task.WhenAll(workers).ConfigureAwait(false);

        // ── 6. Stop flush loop, do a final flush ──────────────────────────────
        await flushCts.CancelAsync().ConfigureAwait(false);
        await flushTask.ConfigureAwait(false);
        FlushPendingPhotos(dispatcher);

        // ── 7. Prune stale DB records ─────────────────────────────────────────
        if (!ct.IsCancellationRequested)
            await _db.DeleteStalePhotosAsync(allFiles, ct).ConfigureAwait(false);

        // ── 7b. Remove albums that are now empty (e.g. scan directory was removed) ─
        if (!ct.IsCancellationRequested)
            await _db.DeleteEmptyAlbumsAsync(ct).ConfigureAwait(false);

        // ── 8. Repair photos that were previously inserted without an AlbumId ──
        if (!ct.IsCancellationRequested)
            await _db.RepairOrphanAlbumIdsAsync(ct).ConfigureAwait(false);

        // ── 9. Done ───────────────────────────────────────────────────────────
        DispatchProgress(
            new ScanProgress(processed, total, IsCompleted: true),
            dispatcher,
            force: true);

        _logger.LogInformation(
            "═══ 扫描完成 ═══  合计: {Total}  新增: {New}  更新: {Updated}  跳过(未变化): {Skipped}",
            total, _countNew, _countUpdated, _countSkipped);

        Dispatch(dispatcher, () => ScanCompleted?.Invoke());
    }

    // ── File enumeration ──────────────────────────────────────────────────────

    private List<string> EnumerateFiles(AppSettings settings)
    {
        var files = new List<string>();

        foreach (var dir in settings.ScanDirectories)
        {
            if (!Directory.Exists(dir))
            {
                _logger.LogWarning("Directory not found: {Dir}", dir);
                continue;
            }

            var option = settings.RecursiveScan
                ? SearchOption.AllDirectories
                : SearchOption.TopDirectoryOnly;

            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*.*", option))
                {
                    if (IsSupported(f) && !IsExcluded(f, settings.ExcludeDirectories))
                        files.Add(f);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cannot enumerate {Dir}", dir);
            }
        }

        return files;
    }

    private static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    private static bool IsExcluded(string path, IReadOnlyList<string> excluded)
    {
        foreach (var excl in excluded)
        {
            if (!string.IsNullOrEmpty(excl) &&
                path.StartsWith(excl, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="filePath"/> starts with at least one of the
    /// currently configured scan directories in <paramref name="settings"/>.
    /// Called inside the worker loop so that files whose parent directory was removed by the
    /// user after enumeration are silently skipped rather than written to the database.
    /// </summary>
    private static bool BelongsToAnyScanDir(string filePath, AppSettings settings)
    {
        foreach (var dir in settings.ScanDirectories)
        {
            if (!string.IsNullOrEmpty(dir) &&
                filePath.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // ── Per-file processing ───────────────────────────────────────────────────

    private async Task ProcessFileAsync(
        string                              filePath,
        Dictionary<string, PhotoScanMeta>   knownPhotos,
        CancellationToken                   ct)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists) return;

            var modifiedAt = info.LastWriteTimeUtc.ToString("O");
            // Album = the photo's direct parent directory, created lazily on first encounter
            var albumId    = await GetOrCreateAlbumForFileAsync(filePath, ct).ConfigureAwait(false);

            // In-memory lookup — no DB query here
            if (!knownPhotos.TryGetValue(filePath, out var meta))
            {
                await InsertNewPhotoAsync(info, modifiedAt, albumId, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _countNew);
            }
            else if (meta.ModifiedAt != modifiedAt)
            {
                // Load full row only when we know it changed (minority case)
                var existing = await _db.GetPhotoByIdAsync(meta.Id, ct).ConfigureAwait(false);
                if (existing is not null)
                    await UpdateExistingPhotoAsync(existing, info, modifiedAt, albumId, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _countUpdated);
            }
            else
            {
                Interlocked.Increment(ref _countSkipped);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error processing {Path}", filePath);
        }
    }

    private async Task InsertNewPhotoAsync(
        FileInfo          info,
        string            modifiedAt,
        long?             albumId,
        CancellationToken ct)
    {
        var exif = await Task.Run(() => _exif.ReadExif(info.FullName), ct).ConfigureAwait(false);

        var photo = new Photo
        {
            FilePath    = info.FullName,
            FileName    = info.Name,
            FileSize    = info.Length,
            Width       = exif.Width,
            Height      = exif.Height,
            TakenAt     = exif.TakenAt,
            CreatedAt   = DateTime.UtcNow.ToString("O"),
            ModifiedAt  = modifiedAt,
            AlbumId     = albumId,
            Latitude    = exif.Latitude,
            Longitude   = exif.Longitude,
            CameraModel = exif.CameraModel,
            CameraMake  = exif.CameraMake,
            Orientation = exif.Orientation,
        };

        var id = await _db.InsertPhotoAsync(photo, ct).ConfigureAwait(false);
        photo.Id = id;

        _pendingDiscovered.Enqueue(photo);
        _logger.LogDebug("[新增] {File}  AlbumId={AlbumId}  Id={Id}", photo.FileName, photo.AlbumId, photo.Id);
    }

    private async Task UpdateExistingPhotoAsync(
        Photo             existing,
        FileInfo          info,
        string            modifiedAt,
        long?             albumId,
        CancellationToken ct)
    {
        var exif = await Task.Run(() => _exif.ReadExif(info.FullName), ct).ConfigureAwait(false);

        existing.FileName    = info.Name;
        existing.FileSize    = info.Length;
        existing.Width       = exif.Width;
        existing.Height      = exif.Height;
        existing.TakenAt     = exif.TakenAt;
        existing.ModifiedAt  = modifiedAt;
        existing.AlbumId     = albumId;          // update in case file moved between dirs
        existing.Latitude    = exif.Latitude;
        existing.Longitude   = exif.Longitude;
        existing.CameraModel = exif.CameraModel;
        existing.CameraMake  = exif.CameraMake;
        existing.Orientation = exif.Orientation;

        await _db.UpdatePhotoAsync(existing, ct).ConfigureAwait(false);

        _pendingUpdated.Enqueue(existing);
        _logger.LogDebug("[更新] {File}  AlbumId={AlbumId}  Id={Id}", existing.FileName, existing.AlbumId, existing.Id);
    }

    // ── Album resolution (per-file parent directory) ──────────────────────────

    /// <summary>
    /// Returns the album ID for the directory that directly contains
    /// <paramref name="filePath"/>, creating the album if it does not yet exist.
    /// Thread-safe: uses a double-check lock so only one DB insert is issued per
    /// unique directory even when many workers encounter it simultaneously.
    /// </summary>
    private async Task<long?> GetOrCreateAlbumForFileAsync(
        string            filePath,
        CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir)) return null;

        // Fast path — already cached
        if (_albumIdCache.TryGetValue(dir, out var cached))
            return cached;

        // Slow path — create album (serialised to avoid duplicate DB rows)
        await _albumCreateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Double-check after acquiring the lock
            if (_albumIdCache.TryGetValue(dir, out var id))
                return id;

            id = await _db.GetOrCreateDirectoryAlbumAsync(dir, ct).ConfigureAwait(false);
            _albumIdCache[dir] = id;
            return id;
        }
        finally
        {
            _albumCreateLock.Release();
        }
    }

    // ── Batch flush ───────────────────────────────────────────────────────────

    private void FlushPendingPhotos(DispatcherQueue? dispatcher)
    {
        var discovered = Drain(_pendingDiscovered);
        var updated    = Drain(_pendingUpdated);

        if (discovered.Count > 0)
            Dispatch(dispatcher, () => PhotosBatchDiscovered?.Invoke(discovered));
        if (updated.Count > 0)
            Dispatch(dispatcher, () => PhotosBatchUpdated?.Invoke(updated));
    }

    private static List<T> Drain<T>(ConcurrentQueue<T> queue)
    {
        var list = new List<T>();
        while (queue.TryDequeue(out var item))
            list.Add(item);
        return list;
    }

    // ── Progress throttle ─────────────────────────────────────────────────────

    private void DispatchProgress(ScanProgress progress, DispatcherQueue? dispatcher, bool force)
    {
        if (!force)
        {
            var now = Environment.TickCount64;
            if (now - Volatile.Read(ref _lastProgressTick) < ProgressThrottleMs)
                return;
            Volatile.Write(ref _lastProgressTick, now);
        }

        Dispatch(dispatcher, () => ProgressChanged?.Invoke(progress));
    }

    // ── Dispatch helper ───────────────────────────────────────────────────────

    private static void Dispatch(DispatcherQueue? dispatcher, Action action)
    {
        if (dispatcher is null)
            action();
        else
            dispatcher.TryEnqueue(() => action());
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
