using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace FluentGallery.Helpers;

/// <summary>Visual style for a button inside a <see cref="ConfirmDialogHelper"/> dialog.</summary>
public enum DialogButtonStyle
{
    /// <summary>Standard button — adapts to the current theme (white text in dark, dark text in light).</summary>
    Default,

    /// <summary>Accent (primary) button — themed highlight colour (blue by default).</summary>
    Primary,

    /// <summary>Destructive action button — always red with white text.</summary>
    Danger,
}

/// <summary>
/// Lightweight helper for showing themed, consistently-styled ContentDialogs.
/// The dialog automatically inherits the host element's current theme so it
/// renders correctly in both light and dark mode.
/// </summary>
public static class ConfirmDialogHelper
{
    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Shows a confirmation dialog and returns <c>true</c> when the user clicks
    /// the confirm (primary) button.
    /// </summary>
    /// <param name="xamlRoot">XamlRoot of the calling page or window.</param>
    /// <param name="title">Dialog title.</param>
    /// <param name="content">
    ///   Dialog body — pass a <see cref="string"/> for plain text or any
    ///   <see cref="UIElement"/> for rich content.
    /// </param>
    /// <param name="confirmText">Label for the confirm button.</param>
    /// <param name="confirmStyle">Visual style of the confirm button.</param>
    /// <param name="cancelText">Label for the cancel button (default "取消").</param>
    /// <param name="cancelStyle">Visual style of the cancel button.</param>
    public static async Task<bool> ShowAsync(
        XamlRoot          xamlRoot,
        string            title,
        object            content,
        string            confirmText,
        DialogButtonStyle confirmStyle = DialogButtonStyle.Primary,
        string            cancelText   = "取消",
        DialogButtonStyle cancelStyle  = DialogButtonStyle.Default)
    {
        // Inherit the host element's theme so the dialog renders correctly in
        // both dark and light mode instead of always showing as light.
        var rootElement = xamlRoot.Content as FrameworkElement;
        var theme       = rootElement?.ActualTheme ?? ElementTheme.Default;

        var dialog = new ContentDialog
        {
            Title             = title,
            Content           = content,
            PrimaryButtonText = confirmText,
            CloseButtonText   = cancelText,
            // Never rely on DefaultButton for visual emphasis — we apply styles
            // manually so each button has exactly the requested appearance.
            DefaultButton     = ContentDialogButton.None,
            XamlRoot          = xamlRoot,
            RequestedTheme    = theme,
        };

        // Apply styles that can be set before ShowAsync.
        ApplyPreLoadStyle(dialog, button: true,  confirmStyle);
        ApplyPreLoadStyle(dialog, button: false, cancelStyle);

        // Styles that require visual-tree access are applied in Loaded.
        bool needLoadedHook = confirmStyle == DialogButtonStyle.Danger
                           || cancelStyle  == DialogButtonStyle.Danger;

        if (needLoadedHook)
        {
            dialog.Loaded += (_, _) =>
            {
                if (confirmStyle == DialogButtonStyle.Danger)
                    ApplyDangerStyle(dialog, "PrimaryButton");
                if (cancelStyle == DialogButtonStyle.Danger)
                    ApplyDangerStyle(dialog, "CloseButton");
            };
        }

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Applies Default / Primary styles that don't require visual-tree access.
    /// Danger is deferred to the Loaded handler.
    /// </summary>
    private static void ApplyPreLoadStyle(ContentDialog dialog, bool button, DialogButtonStyle style)
    {
        if (style != DialogButtonStyle.Primary) return;

        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var s)
            && s is Style accentStyle)
        {
            if (button) dialog.PrimaryButtonStyle = accentStyle;
            else        dialog.CloseButtonStyle   = accentStyle;
        }
    }

    /// <summary>
    /// Finds the named button in the ContentDialog's visual tree and makes it
    /// red by injecting brush overrides into its own resource dictionary, then
    /// applying AccentButtonStyle so the template uses those keys.
    /// </summary>
    private static void ApplyDangerStyle(ContentDialog dialog, string buttonName)
    {
        var btn = FindDescendant<Button>(dialog, buttonName);
        if (btn is null) return;

        // Inject red brushes at the button scope.  The AccentButtonStyle
        // template uses ThemeResource for these keys; the nearest-ancestor
        // lookup finds our overrides before the global accent-colour values.
        btn.Resources["AccentButtonBackground"]            = DangerBrush(0xFF, 0xC4, 0x2B, 0x1C);
        btn.Resources["AccentButtonBackgroundPointerOver"] = DangerBrush(0xFF, 0xA8, 0x22, 0x19);
        btn.Resources["AccentButtonBackgroundPressed"]     = DangerBrush(0xFF, 0x8B, 0x1B, 0x14);
        btn.Resources["AccentButtonBackgroundDisabled"]    = DangerBrush(0x66, 0xC4, 0x2B, 0x1C);

        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var s)
            && s is Style accentStyle)
        {
            btn.Style = accentStyle;
        }
    }

    private static SolidColorBrush DangerBrush(byte a, byte r, byte g, byte b)
        => new(Color.FromArgb(a, r, g, b));

    private static T? FindDescendant<T>(DependencyObject parent, string name)
        where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child  = VisualTreeHelper.GetChild(parent, i);
            if (child is T fe && fe.Name == name) return fe;
            var result = FindDescendant<T>(child, name);
            if (result is not null) return result;
        }
        return null;
    }
}
