using FluentGallery.Helpers;
using FluentGallery.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
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
        await ViewModel.LoadAsync();
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
        if (await ShowConfirmAsync(
                "清除缩略图缓存",
                "这将删除所有缓存的缩略图文件。下次查看照片时将重新生成，可能需要一些时间。\n\n确定要继续吗？",
                "清除"))
            await ViewModel.ClearThumbnailCacheAsync();
    }

    private async void ClearThumbnailsFromHint_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ShowThumbnailSizeHint = false;
        await ViewModel.ClearThumbnailCacheAsync();
    }

    private async void ClearDbCache_Click(object sender, RoutedEventArgs e)
    {
        if (await ShowConfirmAsync(
                "清除数据库缓存",
                "这将清空所有照片和缩略图的数据库记录，相册结构和设置将保留。\n\n再次扫描目录后照片将重新出现。确定要继续吗？",
                "清除"))
            await ViewModel.ClearDatabaseCacheAsync();
    }

    private async void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (await ShowConfirmAsync(
                "清除全部数据",
                "这将删除所有照片记录、相册、缩略图文件和应用设置，应用将恢复出厂状态。\n\n此操作不可撤销，确定要继续吗？",
                "清除全部数据",
                isDangerous: true))
            await ViewModel.ClearAllDataAsync();
    }

    private async Task<bool> ShowConfirmAsync(
        string title,
        string content,
        string primaryText,
        bool isDangerous = false)
    {
        var dialog = new ContentDialog
        {
            Title             = title,
            Content           = content,
            PrimaryButtonText = primaryText,
            CloseButtonText   = "取消",
            DefaultButton     = ContentDialogButton.Close,
            XamlRoot          = this.XamlRoot,
        };

        if (isDangerous
            && Application.Current.Resources.TryGetValue("AccentButtonStyle", out var s)
            && s is Style accentStyle)
        {
            dialog.PrimaryButtonStyle = accentStyle;
        }

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    // ────────────────────────────────────────────────────────────────────
    // InfoBar closed
    // ────────────────────────────────────────────────────────────────────

    private void StatusBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
        => ViewModel.StatusMessage = null;
}
