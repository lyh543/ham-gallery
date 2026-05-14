using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FluentGallery.Helpers;

public static class ContextMenuHelper
{
    public const double DefaultMaxWidth = 200;

    private const int FileNameLineLength = 28;
    private const int FileNameMaxLines = 3;
    private const double InfoTitleFontSize = 13;
    private const double InfoDetailFontSize = 11;
    private static readonly Thickness InfoTitlePadding = new(12, 4, 12, 1);
    private static readonly Thickness InfoDetailPadding = new(12, 1, 12, 1);

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
        var items = new List<MenuFlyoutItem>();
        var primaryBrush = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        var secondaryBrush = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

        foreach (var line in WrapFileName(title))
            items.Add(CreateStaticInfoLine(line, primaryBrush, InfoTitleFontSize, maxWidth, InfoTitlePadding));

        foreach (var detail in details)
        {
            if (!string.IsNullOrWhiteSpace(detail))
                items.Add(CreateStaticInfoLine(detail.Trim(), secondaryBrush, InfoDetailFontSize, maxWidth, InfoDetailPadding));
        }

        return items;
    }

    private static MenuFlyoutItem CreateStaticInfoLine(string text, Brush foreground, double fontSize, double maxWidth, Thickness padding)
    {
        return new MenuFlyoutItem
        {
            Text = text,
            IsHitTestVisible = false,
            IsTabStop = false,
            MaxWidth = maxWidth,
            MinHeight = 0,
            Padding = padding,
            FontSize = fontSize,
            Foreground = foreground,
        };
    }

    private static IReadOnlyList<string> WrapFileName(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var lines = new List<string>();
        int index = 0;

        while (index < text.Length && lines.Count < FileNameMaxLines)
        {
            int remaining = text.Length - index;
            int take = Math.Min(FileNameLineLength, remaining);
            bool isLastVisibleLine = lines.Count == FileNameMaxLines - 1;
            bool hasMoreAfterThisLine = remaining > take;

            var segment = text.Substring(index, take);
            index += take;

            if (isLastVisibleLine && hasMoreAfterThisLine)
            {
                segment = segment[..Math.Max(0, FileNameLineLength - 3)] + "...";
                lines.Add(segment);
                break;
            }

            lines.Add(segment);
        }

        return lines;
    }
}