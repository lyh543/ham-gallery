using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentGallery.Helpers;

public static class ContextMenuHelper
{
    public const double DefaultMaxWidth = 200;

    public static MenuFlyoutItem CreateDirectoryMenuItem(string name, string directoryPath, double maxWidth = DefaultMaxWidth)
    {
        var item = new MenuFlyoutItem
        {
            Text = name,
            MaxWidth = maxWidth,
        };

        ToolTipService.SetToolTip(item, directoryPath);
        return item;
    }

    public static IReadOnlyList<MenuFlyoutItem> CreateInfoItems(string title, IEnumerable<string?> details, double maxWidth = DefaultMaxWidth)
    {
        var titleStyle = (Style)Application.Current.Resources["ContextMenuInfoTitleItemStyle"];
        var detailStyle = (Style)Application.Current.Resources["ContextMenuInfoDetailItemStyle"];
        var items = new List<MenuFlyoutItem>
        {
            CreateStaticInfoLine(title, titleStyle, maxWidth),
        };

        foreach (var detail in details)
        {
            if (!string.IsNullOrWhiteSpace(detail))
                items.Add(CreateStaticInfoLine(detail.Trim(), detailStyle, maxWidth));
        }

        return items;
    }

    private static MenuFlyoutItem CreateStaticInfoLine(string text, Style style, double maxWidth)
    {
        return new MenuFlyoutItem
        {
            Text = text,
            Style = style,
            IsHitTestVisible = false,
            IsTabStop = false,
            MaxWidth = maxWidth,
        };
    }
}