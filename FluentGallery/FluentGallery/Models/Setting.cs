namespace FluentGallery.Models;

/// <summary>
/// Key-value row in the Settings table.
/// Complex values (e.g. AppSettings) are stored as JSON in <see cref="Value"/>.
/// </summary>
public sealed class Setting
{
    public string Key { get; set; } = string.Empty;
    public string? Value { get; set; }
}
