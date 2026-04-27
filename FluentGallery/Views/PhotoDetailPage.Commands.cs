using FluentGallery.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.IO;
using System.Threading.Tasks;

namespace FluentGallery.Views;

public sealed partial class PhotoDetailPage
{
    // ── Toolbar button handlers ───────────────────────────────────────────────

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current.MainWindow is MainWindow mw)
            mw.ClosePhotoDetail();
        else if (Frame.CanGoBack)
            Frame.GoBack();
    }

    private async void OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        var path = ViewModel.CurrentImagePath;
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Current.MainWindow);

            var oainfo = new WindowsApiHelper.OPENASINFO
            {
                pcszFile = path,
                pcszClass = null,
                oaifInFlags = WindowsApiHelper.OAIF_ALLOW_REGISTRATION | WindowsApiHelper.OAIF_EXEC
            };

            int hResult = WindowsApiHelper.SHOpenWithDialog(hwnd, ref oainfo);
            if (hResult != 0)
            {
                _logger.LogWarning("SHOpenWithDialog failed with HRESULT: {HResult:X8}", hResult);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open file with dialog");
        }
    }

    private void ShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        var path = ViewModel.CurrentImagePath;
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            WindowsApiHelper.SHParseDisplayName(
                Path.GetDirectoryName(path) ?? string.Empty,
                IntPtr.Zero,
                out IntPtr pidlFolder,
                0,
                out _);

            if (pidlFolder != IntPtr.Zero)
            {
                try
                {
                    WindowsApiHelper.SHParseDisplayName(
                        path,
                        IntPtr.Zero,
                        out IntPtr pidlFile,
                        0,
                        out _);

                    if (pidlFile != IntPtr.Zero)
                    {
                        try
                        {
                            int hResult = WindowsApiHelper.SHOpenFolderAndSelectItems(
                                pidlFolder,
                                1,
                                new[] { pidlFile },
                                0);

                            if (hResult != 0)
                            {
                                _logger.LogWarning("SHOpenFolderAndSelectItems failed with HRESULT: {HResult:X8}", hResult);
                            }
                        }
                        finally
                        {
                            WindowsApiHelper.CoTaskMemFree(pidlFile);
                        }
                    }
                }
                finally
                {
                    WindowsApiHelper.CoTaskMemFree(pidlFolder);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to show file in explorer");
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
        => await DeleteWithConfirmAsync();

    private void Fullscreen_Click(object sender, RoutedEventArgs e)
        => ToggleFullscreen();

    private void InfoToggle_Click(object sender, RoutedEventArgs e)
        => ToggleInfoPanel();

    // ── Info panel toggle ─────────────────────────────────────────────────────

    private void ToggleInfoPanel()
    {
        ViewModel.IsInfoPanelOpen = !ViewModel.IsInfoPanelOpen;
        InfoToggleButton.IsChecked = ViewModel.IsInfoPanelOpen;
        InfoPanelColumn.Width = ViewModel.IsInfoPanelOpen
            ? new GridLength(300)
            : new GridLength(0);
    }

    // ── Delete with confirmation ──────────────────────────────────────────────

    private async Task DeleteWithConfirmAsync()
    {
        if (ViewModel.CurrentPhoto is null) return;

        if (ViewModel.ConfirmBeforeDelete)
        {
            // Build the checkbox「下次不再提示」
            var dontAskCheck = new CheckBox
            {
                Content = L10n.Get("PhotoDetail_Delete_DontAsk"),
                Margin = new Thickness(0, 12, 0, 0),
            };

            var panel = new StackPanel { Spacing = 0 };
            panel.Children.Add(new TextBlock
            {
                Text = L10n.Format("PhotoDetail_Delete_ConfirmText", ViewModel.CurrentPhoto.FileName),
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(dontAskCheck);

            bool confirmed = await ConfirmDialogHelper.ShowAsync(
                XamlRoot,
                L10n.Get("PhotoDetail_Delete_DialogTitle"),
                panel,
                L10n.Get("PhotoDetail_Delete_DialogConfirm"),
                confirmStyle: DialogButtonStyle.Danger);

            if (!confirmed) return;

            if (dontAskCheck.IsChecked == true)
            {
                await ViewModel.DisableDeleteConfirmAsync(_cts.Token);
                ShowToast(L10n.Get("PhotoDetail_Toast_DisableConfirm"), ToastKind.Normal, showUndo: false);
            }
        }

        var deletedName = await ViewModel.DeleteAsync(_cts.Token);

        if (deletedName is null)
        {
            ShowToast(L10n.Get("PhotoDetail_Toast_DeleteFailed"), ToastKind.Error, showUndo: false);
            return;
        }

        ShowToast(L10n.Format("PhotoDetail_Toast_Deleted", deletedName), ToastKind.Normal, showUndo: true);

        if (ViewModel.CurrentImagePath is null)
        {
            if (App.Current.MainWindow is MainWindow mw)
                mw.ClosePhotoDetail();
            else if (Frame.CanGoBack)
                Frame.GoBack();
        }
    }

    // ── Undo delete ───────────────────────────────────────────────────────────

    private async Task UndoDeleteAsync()
    {
        if (!ViewModel.CanUndo) return;

        HideToast();

        var restoredName = await ViewModel.UndoDeleteAsync(_cts.Token);

        if (restoredName is null)
            ShowToast(L10n.Get("PhotoDetail_Toast_UndoFailed"), ToastKind.Error, showUndo: false);
        else
            ShowToast(L10n.Format("PhotoDetail_Toast_Restored", restoredName), ToastKind.Normal, showUndo: false);
    }

    // ── Toast undo button handler ─────────────────────────────────────────────

    private async void ToastUndo_Click(object sender, RoutedEventArgs e)
        => await UndoDeleteAsync();
}
