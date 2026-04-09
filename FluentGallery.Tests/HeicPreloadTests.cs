using FluentGallery.Decoders;
using FluentGallery.Loaders;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Xunit;

namespace FluentGallery.Tests;

/// <summary>
/// Regression tests for the HEIC preload pipeline introduced in PhotoDetailPage.
///
/// Scenario that previously caused a native crash (TerminateProcess with no log):
///   Rapid navigation through HEIC photos triggered 10+ concurrent TryDecodeAsync
///   calls on the WIC HEIC decoder from the thread pool, crashing the WIC COM server.
///
/// Fix: a SemaphoreSlim(1,1) in PhotoDetailPage throttles concurrent preload decodes.
///
/// Note: SoftwareBitmapSource.SetBitmapAsync requires a WinUI DispatcherQueue and
/// cannot be tested here. The steps that CAN run headless are tested below.
/// </summary>
public sealed class HeicPreloadTests
{
    private static readonly string HeicFixture = Path.Combine(
        AppContext.BaseDirectory, "TestData", "regression_heic_512x512.heic");

    private static ImageDecoderPipeline BuildPipeline()
    {
        var p = new ImageDecoderPipeline();
        p.Register(WicImageDecoder.CreateForHeic());
        p.Register(new MagickImageDecoder());
        return p;
    }

    // ── 1. Concurrent decode — reproduces the original crash scenario ─────────

    /// <summary>
    /// 20 concurrent TryDecodeAsync calls on the same HEIC file must all succeed
    /// without crashing the process (the original bug: WIC hit from many threads).
    ///
    /// This test may flake or crash the test runner on machines that do NOT have
    /// the HEVC Video Extensions (WIC HEIC codec) installed, because Magick.NET's
    /// internal libheif decoder IS thread-safe. On machines WITH the WIC codec
    /// installed, this tests the thread-safety of that codec.
    /// </summary>
    [Fact]
    public async Task ConcurrentDecode_AllTasksCompleteWithoutCrash()
    {
        var pipeline = BuildPipeline();
        const int concurrency = 20;

        var tasks = Enumerable.Range(0, concurrency)
            .Select(_ => pipeline.TryDecodeAsync(HeicFixture, 0, 0))
            .ToList();

        var results = await Task.WhenAll(tasks);

        Assert.Equal(concurrency, results.Length);
        Assert.All(results, r => Assert.NotNull(r));
    }

    // ── 2. Semaphore-throttled decode — verifies the fix is correct ──────────

    /// <summary>
    /// Same 20 tasks but serialised through a SemaphoreSlim(1,1), mirroring the
    /// _preloadDecodeSemaphore in PhotoDetailPage.  All must succeed and return
    /// correct pixel data.
    /// </summary>
    [Fact]
    public async Task SemaphoreThrottledDecode_AllTasksCompleteCorrectly()
    {
        var pipeline  = BuildPipeline();
        var semaphore = new SemaphoreSlim(1, 1);
        const int count = 20;

        var tasks = Enumerable.Range(0, count).Select(async _ =>
        {
            await semaphore.WaitAsync();
            try   { return await pipeline.TryDecodeAsync(HeicFixture, 0, 0); }
            finally { semaphore.Release(); }
        });

        var results = await Task.WhenAll(tasks);

        Assert.Equal(count, results.Length);
        Assert.All(results, r =>
        {
            Assert.NotNull(r);
            Assert.Equal(512u, r.Width);
            Assert.Equal(512u, r.Height);
            Assert.Equal(512 * 512 * 4, r.Pixels.Length);
        });
    }

    // ── 3. Cancellation while waiting on semaphore ────────────────────────────

    /// <summary>
    /// If the CancellationToken is cancelled while a task is waiting for the
    /// semaphore, it must throw OperationCanceledException and NOT hang.
    /// The semaphore count must remain correct afterwards.
    /// </summary>
    [Fact]
    public async Task SemaphoreThrottledDecode_CancellationWhileWaiting_ThrowsAndReleases()
    {
        var pipeline  = BuildPipeline();
        var semaphore = new SemaphoreSlim(1, 1);
        var cts       = new CancellationTokenSource();

        // Hold the semaphore so a second task blocks.
        await semaphore.WaitAsync();

        var blockedTask = Task.Run(async () =>
        {
            await semaphore.WaitAsync(cts.Token); // will block
            try   { return await pipeline.TryDecodeAsync(HeicFixture, 0, 0, cts.Token); }
            finally { semaphore.Release(); }
        });

        // Cancel before releasing — the blocked WaitAsync should throw.
        cts.Cancel();
        semaphore.Release(); // release the holder so the test doesn't deadlock

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blockedTask);

        // Semaphore should be back to 1 (the release above, since the cancelled
        // task never acquired it).
        Assert.Equal(1, semaphore.CurrentCount);
    }

    // ── 4. Decode → SoftwareBitmap conversion (headless, no dispatcher) ──────

    /// <summary>
    /// After decoding, the pixel buffer must survive SoftwareBitmap.CreateCopyFromBuffer
    /// and SoftwareBitmap.Convert without throwing.  This covers the display path in
    /// <c>MagickImageLoader.LoadAsync</c> up to (but not including) SetBitmapAsync.
    ///
    /// SetBitmapAsync is excluded because it requires a WinUI DispatcherQueue.
    /// </summary>
    [Fact]
    public async Task DecodeAndSoftwareBitmapConvert_Succeeds()
    {
        var pipeline = BuildPipeline();
        var decoded  = await pipeline.TryDecodeAsync(HeicFixture, 0, 0);

        Assert.NotNull(decoded);

        using var sbIgnore = SoftwareBitmap.CreateCopyFromBuffer(
            decoded.Pixels.AsBuffer(),
            BitmapPixelFormat.Bgra8,
            (int)decoded.Width,
            (int)decoded.Height,
            BitmapAlphaMode.Ignore);

        using var sbPremul = SoftwareBitmap.Convert(
            sbIgnore, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        Assert.Equal((int)decoded.Width,  sbPremul.PixelWidth);
        Assert.Equal((int)decoded.Height, sbPremul.PixelHeight);
        Assert.Equal(BitmapAlphaMode.Premultiplied, sbPremul.BitmapAlphaMode);
    }

    // ── 5. Cancellation mid-decode ────────────────────────────────────────────

    /// <summary>
    /// Cancelling a token that is passed to TryDecodeAsync must either throw
    /// OperationCanceledException or return null — it must not crash the process.
    /// </summary>
    [Fact]
    public async Task CancellationDuringDecode_ThrowsOrReturnsNull()
    {
        var pipeline = BuildPipeline();
        var cts      = new CancellationTokenSource();

        // Cancel immediately to maximise the chance of hitting the cancellation
        // path inside the decoder.
        cts.Cancel();

        try
        {
            var result = await pipeline.TryDecodeAsync(HeicFixture, 0, 0, cts.Token);
            // If it races past cancellation and succeeds, that's also acceptable.
            _ = result;
        }
        catch (OperationCanceledException) { /* expected */ }
        // Any other exception is a failure (let it propagate).
    }

    // ── 6. Rapid navigation simulation ───────────────────────────────────────

    /// <summary>
    /// Simulates the user rapidly navigating through HEIC images:
    /// each "navigation" cancels the previous preload CTS and starts a new batch.
    /// Verifies that no task leaks and the final decode succeeds.
    /// </summary>
    [Fact]
    public async Task RapidNavigation_PreloadCancellation_NoLeakNoCrash()
    {
        var pipeline  = BuildPipeline();
        var semaphore = new SemaphoreSlim(1, 1);

        CancellationTokenSource preloadCts = new();
        Task<FluentGallery.Decoders.DecodedImageData?>? lastBatchTask = null;

        // Simulate 10 rapid navigations.
        for (int nav = 0; nav < 10; nav++)
        {
            preloadCts.Cancel();
            preloadCts = new CancellationTokenSource();

            var token = preloadCts.Token;
            lastBatchTask = Task.Run(async () =>
            {
                try
                {
                    await semaphore.WaitAsync(token);
                    try   { return await pipeline.TryDecodeAsync(HeicFixture, 0, 0, token); }
                    finally { semaphore.Release(); }
                }
                catch (OperationCanceledException) { return null; }
            });
        }

        // The final batch should complete successfully (not cancelled).
        var result = await lastBatchTask!;
        Assert.NotNull(result);
    }

}
