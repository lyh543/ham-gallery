using FluentGallery.Helpers;
using FluentGallery.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using System.ComponentModel;
using WinRT.Interop;

namespace FluentGallery.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        ViewModel = App.Current.Services.GetRequiredService<SettingsViewModel>();
        this.InitializeComponent();
    }

    // ────────────────────────────────────────────────────────────────────
    // Navigation
    // ────────────────────────────────────────────────────────────────────

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Loaded += OnPageLoaded;
        await ViewModel.LoadAsync();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        Loaded -= OnPageLoaded;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnPageLoaded;
        ElasticScrollHelper.Attach(SettingsScrollViewer);

        // Restore thumbnail-generation UI state when returning to this page.
        // The ViewModel is a singleton and may still be building or showing the
        // "done" state from a previous visit.
        SyncThumbGenUIFromViewModel();
    }

    // ────────────────────────────────────────────────────────────────────
    // Multi-folder picker helper
    // ────────────────────────────────────────────────────────────────────

    private Task<IReadOnlyList<string>> PickFoldersAsync()
    {
        var hwnd = WindowNative.GetWindowHandle(App.Current.MainWindow);
        return MultiFolderPicker.PickAsync(hwnd);
    }

    // ────────────────────────────────────────────────────────────────────
    // Scan directory events
    // ────────────────────────────────────────────────────────────────────

    private async void AddScanDir_Click(object sender, RoutedEventArgs e)
    {
        var paths = await PickFoldersAsync();
        ViewModel.AddScanDirectories(paths);
    }

    private void RemoveScanDir_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path })
            ViewModel.RemoveScanDirectory(path);
    }

    // ────────────────────────────────────────────────────────────────────
    // Exclude directory events
    // ────────────────────────────────────────────────────────────────────

    private async void AddExcludeDir_Click(object sender, RoutedEventArgs e)
    {
        var paths = await PickFoldersAsync();
        ViewModel.AddExcludeDirectories(paths);
    }

    private void RemoveExcludeDir_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path })
            ViewModel.RemoveExcludeDirectory(path);
    }

    // ────────────────────────────────────────────────────────────────────
    // Cache operation events (require confirmation dialogs)
    // ────────────────────────────────────────────────────────────────────

    private async void ClearThumbnails_Click(object sender, RoutedEventArgs e)
    {
        if (await ConfirmDialogHelper.ShowAsync(
                XamlRoot,
            L10n.Get("Settings_ClearThumb_Title"),
            L10n.Get("Settings_ClearThumb_Content"),
            L10n.Get("Settings_ClearThumb_Confirm")))
            await ViewModel.ClearThumbnailCacheAsync();
    }

    private async void ClearThumbnailsFromHint_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ShowThumbnailSizeHint = false;
        await ViewModel.ClearThumbnailCacheAsync();
    }

    private async void ClearDbCache_Click(object sender, RoutedEventArgs e)
    {
        if (await ConfirmDialogHelper.ShowAsync(
                XamlRoot,
            L10n.Get("Settings_ClearDb_Title"),
            L10n.Get("Settings_ClearDb_Content"),
            L10n.Get("Settings_ClearDb_Confirm")))
            await ViewModel.ClearDatabaseCacheAsync();
    }

    private async void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (await ConfirmDialogHelper.ShowAsync(
                XamlRoot,
            L10n.Get("Settings_ClearAll_Title"),
            L10n.Get("Settings_ClearAll_Content"),
            L10n.Get("Settings_ClearAll_Confirm"),
                confirmStyle: DialogButtonStyle.Danger))
            await ViewModel.ClearAllDataAsync();
    }

    // ────────────────────────────────────────────────────────────────────
    // Debug
    // ────────────────────────────────────────────────────────────────────

    private void ForceGC_Click(object sender, RoutedEventArgs e)
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: false);
    }

    // ────────────────────────────────────────────────────────────────────
    // InfoBar closed
    // ────────────────────────────────────────────────────────────────────

    private void StatusBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
        => ViewModel.StatusMessage = null;

    // ────────────────────────────────────────────────────────────────────
    // Thumbnail batch generation — progress & done animation
    // ────────────────────────────────────────────────────────────────────

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ViewModel.IsBuildingThumbnails):
                UpdateThumbGenArea();
                break;

            case nameof(ViewModel.IsThumbnailBuildDone):
                if (ViewModel.IsThumbnailBuildDone)
                {
                    ThumbDoneSubText.Text = ViewModel.ThumbnailBuildTotal == 0
                        ? L10n.Get("Settings_DoneSub_AllExist")
                        : L10n.Format("Settings_DoneSub_Generated", ViewModel.ThumbnailBuildTotal);
                    ShowAndAnimateThumbDone();
                }
                else
                {
                    HideThumbDonePanel();
                    UpdateThumbGenArea();
                }
                break;
        }
    }

    /// <summary>
    /// Shows/hides the progress border and building panel based on current VM state.
    /// Called when <c>IsBuildingThumbnails</c> changes.
    /// </summary>
    private void UpdateThumbGenArea()
    {
        bool showArea = ViewModel.IsBuildingThumbnails || ViewModel.IsThumbnailBuildDone;
        ThumbGenProgressBorder.Visibility = showArea ? Visibility.Visible : Visibility.Collapsed;
        ThumbBuildingPanel.Visibility     = ViewModel.IsBuildingThumbnails
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ShowAndAnimateThumbDone()
    {
        ThumbGenProgressBorder.Visibility = Visibility.Visible;
        ThumbBuildingPanel.Visibility     = Visibility.Collapsed;
        ThumbDonePanel.Visibility         = Visibility.Visible;

        // Reset transforms so the animation always plays from scratch
        ThumbDoneCircleScale.ScaleX    = 0;
        ThumbDoneCircleScale.ScaleY    = 0;
        ThumbDoneTextPanel.Opacity     = 0;
        ThumbDoneTextTranslate.X       = 20;

        var sb = new Storyboard();

        // ── Circle bounces in ─────────────────────────────────────────
        foreach (var prop in new[] { "ScaleX", "ScaleY" })
        {
            var anim = new DoubleAnimationUsingKeyFrames();
            Storyboard.SetTarget(anim, ThumbDoneCircleScale);
            Storyboard.SetTargetProperty(anim, prop);
            anim.KeyFrames.Add(new DiscreteDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
                Value   = 0,
            });
            anim.KeyFrames.Add(new EasingDoubleKeyFrame
            {
                KeyTime        = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.45)),
                Value          = 1.0,
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 },
            });
            sb.Children.Add(anim);
        }

        // ── Text panel fades and slides in (slight delay) ─────────────
        var opacityAnim = new DoubleAnimation
        {
            From           = 0,
            To             = 1,
            Duration       = new Duration(TimeSpan.FromSeconds(0.3)),
            BeginTime      = TimeSpan.FromSeconds(0.3),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(opacityAnim, ThumbDoneTextPanel);
        Storyboard.SetTargetProperty(opacityAnim, "Opacity");
        sb.Children.Add(opacityAnim);

        var slideAnim = new DoubleAnimation
        {
            From           = 20,
            To             = 0,
            Duration       = new Duration(TimeSpan.FromSeconds(0.35)),
            BeginTime      = TimeSpan.FromSeconds(0.3),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(slideAnim, ThumbDoneTextTranslate);
        Storyboard.SetTargetProperty(slideAnim, "X");
        sb.Children.Add(slideAnim);

        sb.Begin();
    }

    /// <summary>
    /// Restores the thumbnail-generation UI (progress bar / done animation)
    /// from the singleton ViewModel state. Needed because the ViewModel
    /// outlives the page, but the XAML visibility is reset on each navigation.
    /// </summary>
    private void SyncThumbGenUIFromViewModel()
    {
        if (ViewModel.IsThumbnailBuildDone)
        {
            // Build completed while the page was hidden — replay the
            // done animation with the correct sub-text.
            ThumbDoneSubText.Text = ViewModel.ThumbnailBuildTotal == 0
                ? L10n.Get("Settings_DoneSub_AllExist")
                : L10n.Format("Settings_DoneSub_Generated", ViewModel.ThumbnailBuildTotal);
            ShowAndAnimateThumbDone();
        }
        else
        {
            UpdateThumbGenArea();
        }
    }

    private void HideThumbDonePanel()
    {
        ThumbDonePanel.Visibility = Visibility.Collapsed;
    }
}
