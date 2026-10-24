namespace Jellyfin.Plugin.TgJelly.Templates;

/// <summary>
/// One top-level piece of a template (design §6.5): a run of literal text, one <c>{{ … }}</c> expression, or one whole
/// block such as <c>{{ if }}…{{ end }}</c>, with its place in the template text.
/// </summary>
/// <param name="Index">The piece's number, as the marked render writes it.</param>
/// <param name="Start">The offset of the piece in the template text, in UTF-16 code units.</param>
/// <param name="Length">The piece's length in the template text.</param>
public sealed record TemplatePiece(int Index, int Start, int Length);
