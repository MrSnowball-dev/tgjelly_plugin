namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// One top-level piece of a template, for the hover link between the template and its preview.
/// </summary>
public sealed class TemplatePieceDto
{
    /// <summary>Gets the piece's number, as the preview's markers carry it.</summary>
    public int Index { get; init; }

    /// <summary>Gets the offset of the piece in the validated text (UTF-16 code units).</summary>
    public int Start { get; init; }

    /// <summary>Gets the piece's length in the validated text.</summary>
    public int Length { get; init; }
}
