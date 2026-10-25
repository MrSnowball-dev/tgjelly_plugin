namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// One notice in <see cref="StatusDto.Notices"/>.
/// </summary>
public sealed class NoticeDto
{
    /// <summary>Gets the notice ID, passed back to dismiss it.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets the notice kind.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>Gets the plain text; the page escapes it.</summary>
    public string Text { get; init; } = string.Empty;
}
