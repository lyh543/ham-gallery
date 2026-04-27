using FluentGallery.Controls;
using FluentGallery.Data;
using FluentGallery.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System;
using System.Threading.Tasks;

namespace FluentGallery.Views;

public sealed partial class PhotoDetailPage
{
    private string? _pendingIndexDirectory;
    private string? _pendingPromptDirectory;
    private bool _indexPromptShown;
    private Flyout _indexPromptFlyout = null!;
    private IndexPrompt _indexPromptContent = null!;
    private DispatcherTimer _indexPromptAutoHideTimer = null!;
    private bool _indexPromptClosingByButton;
    private bool _indexPromptReopenScheduled;
    private bool _indexPromptAllowImmediateClose;
    private bool _indexPromptDismissAnimating;

    private readonly ScanService _scanService =
        App.Current.Services.GetRequiredService<ScanService>();

    private void InitializeIndexState()
    {
        _indexPromptContent = new IndexPrompt();
        _indexPromptContent.ConfirmClicked += IndexPrompt_ConfirmClicked;
        _indexPromptContent.CancelClicked += IndexPrompt_CancelClicked;

        _indexPromptFlyout = new Flyout
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
            Content = _indexPromptContent,
        };

        _indexPromptFlyout.OverlayInputPassThroughElement = this;
        _indexPromptFlyout.Closing += IndexPromptFlyout_Closing;
        _indexPromptFlyout.Closed += IndexPromptFlyout_Closed;

        _indexPromptAutoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _indexPromptAutoHideTimer.Tick += IndexPromptAutoHideTimer_Tick;

        _scanService.ScanCompleted += OnScanCompleted;
        SizeChanged += PhotoDetailPage_SizeChanged;
    }

    private async Task PromptToIndexDirectoryIfNeededAsync()
    {
        if (_indexPromptShown || !ViewModel.ShouldPromptToIndexCurrentDirectory())
            return;

        var directoryPath = ViewModel.GetCurrentDirectoryPath();
        if (string.IsNullOrEmpty(directoryPath))
            return;

        _indexPromptShown = true;
        _pendingPromptDirectory = directoryPath;

        _indexPromptContent.Title = L10n.Get("PhotoDetail_IndexPrompt_Title");
        _indexPromptContent.Message = L10n.Get("PhotoDetail_IndexPrompt_Message");
        _indexPromptContent.ConfirmText = L10n.Get("PhotoDetail_IndexPrompt_Confirm");
        _indexPromptContent.CancelText = L10n.Get("PhotoDetail_IndexPrompt_Cancel");
        ShowIndexPrompt();
    }

    private async void IndexPrompt_ConfirmClicked(object sender, RoutedEventArgs e)
    {
        var directoryPath = _pendingPromptDirectory;
        _pendingPromptDirectory = null;
        await HideIndexPromptWithFadeAsync(fromButton: true);

        if (string.IsNullOrEmpty(directoryPath))
            return;

        _pendingIndexDirectory = directoryPath;
        await ViewModel.EnsureDirectoryIndexedAsync(directoryPath, DispatcherQueue, _cts.Token);
        ShowToast(L10n.Get("PhotoDetail_Toast_Indexing"), ToastKind.Normal, showUndo: false);
    }

    private async void IndexPrompt_CancelClicked(object sender, RoutedEventArgs e)
    {
        _pendingPromptDirectory = null;
        await HideIndexPromptWithFadeAsync(fromButton: true);
    }

    private void IndexPromptFlyout_Closing(FlyoutBase sender, FlyoutBaseClosingEventArgs args)
    {
        if (_indexPromptAllowImmediateClose)
            return;

        if (DebugKeepChromeVisible && !_indexPromptClosingByButton)
        {
            args.Cancel = true;
            return;
        }

        if (!_indexPromptClosingByButton)
        {
            args.Cancel = true;
            if (_indexPromptDismissAnimating)
                return;

            _pendingPromptDirectory = null;
            _ = HideIndexPromptWithFadeAsync(fromButton: false);
        }
    }

    private void IndexPromptFlyout_Closed(object? sender, object e)
    {
        _indexPromptAutoHideTimer.Stop();
        _indexPromptDismissAnimating = false;
        _indexPromptAllowImmediateClose = false;

        if (_indexPromptClosingByButton)
        {
            _indexPromptClosingByButton = false;
            return;
        }

        if (DebugKeepChromeVisible &&
            !string.IsNullOrEmpty(_pendingPromptDirectory) &&
            !_indexPromptReopenScheduled)
        {
            _indexPromptReopenScheduled = true;
            DispatcherQueue.TryEnqueue(() =>
            {
                _indexPromptReopenScheduled = false;
                if (!string.IsNullOrEmpty(_pendingPromptDirectory))
                    ShowIndexPrompt();
            });
            return;
        }

        _pendingPromptDirectory = null;
    }

    private async void IndexPromptAutoHideTimer_Tick(object? sender, object e)
    {
        _indexPromptAutoHideTimer.Stop();
        if (DebugKeepChromeVisible || !_indexPromptFlyout.IsOpen)
            return;

        _pendingPromptDirectory = null;
        await HideIndexPromptWithFadeAsync(fromButton: false);
    }

    private void PhotoDetailPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!DebugKeepChromeVisible)
            return;

        if (string.IsNullOrEmpty(_pendingPromptDirectory))
            return;

        if (_indexPromptFlyout.IsOpen || _indexPromptReopenScheduled)
            return;

        _indexPromptReopenScheduled = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _indexPromptReopenScheduled = false;
            if (!string.IsNullOrEmpty(_pendingPromptDirectory) && !_indexPromptFlyout.IsOpen)
                ShowIndexPrompt();
        });
    }

    private void ShowIndexPrompt()
    {
        _indexPromptAutoHideTimer.Stop();
        _indexPromptFlyout.ShowAt(IndexPromptAnchor, new FlyoutShowOptions
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
        });

        _indexPromptContent.Opacity = 0;
        AnimateOpacity(_indexPromptContent, 1.0, durationMs: 250);

        if (!DebugKeepChromeVisible)
            _indexPromptAutoHideTimer.Start();
    }

    private async Task HideIndexPromptWithFadeAsync(bool fromButton)
    {
        _indexPromptAutoHideTimer.Stop();
        if (!_indexPromptFlyout.IsOpen)
            return;

        _indexPromptClosingByButton = fromButton;
        AnimateOpacity(_indexPromptContent, 0.0, durationMs: 150);
        _indexPromptDismissAnimating = true;
        await Task.Delay(150);
        _indexPromptAllowImmediateClose = true;
        _indexPromptFlyout.Hide();
    }

    private async void OnScanCompleted()
    {
        var pendingDirectory = _pendingIndexDirectory;
        if (string.IsNullOrEmpty(pendingDirectory))
            return;

        if (!ViewModel.IsCurrentFileInDirectory(pendingDirectory))
            return;

        try
        {
            await ViewModel.ReloadCurrentFileContextAsync(DispatcherQueue, _cts.Token);
            _pendingIndexDirectory = null;
            _pendingPromptDirectory = null;

            if (ViewModel.AlbumId is long albumId && App.Current.MainWindow is MainWindow mw)
                mw.NavigateContentToAlbum(albumId);

            ApplyFilmStripPinState();
            Bindings.Update();
            ShowToast(L10n.Get("PhotoDetail_Toast_IndexDone"), ToastKind.Normal, showUndo: false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to reload direct-open file after indexing");
        }
    }

    private void HandleDebugKeepChromeVisibleChanged()
    {
        if (DebugKeepChromeVisible)
        {
            _indexPromptAutoHideTimer.Stop();
            if (!string.IsNullOrEmpty(_pendingPromptDirectory) && !_indexPromptFlyout.IsOpen)
                ShowIndexPrompt();
        }
        else if (!string.IsNullOrEmpty(_pendingPromptDirectory) && _indexPromptFlyout.IsOpen)
        {
            _indexPromptAutoHideTimer.Stop();
            _indexPromptAutoHideTimer.Start();
        }
    }
}
