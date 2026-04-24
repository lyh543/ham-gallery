using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentGallery.Controls;

public sealed partial class IndexPrompt : UserControl
{
    public event RoutedEventHandler? ConfirmClicked;
    public event RoutedEventHandler? CancelClicked;

    public IndexPrompt()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => TitleTextBlock.Text;
        set => TitleTextBlock.Text = value ?? string.Empty;
    }

    public string Message
    {
        get => MessageTextBlock.Text;
        set => MessageTextBlock.Text = value ?? string.Empty;
    }

    public string ConfirmText
    {
        get => ConfirmButton.Content?.ToString() ?? string.Empty;
        set => ConfirmButton.Content = value;
    }

    public string CancelText
    {
        get => CancelButton.Content?.ToString() ?? string.Empty;
        set => CancelButton.Content = value;
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        ConfirmClicked?.Invoke(this, e);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CancelClicked?.Invoke(this, e);
    }
}
