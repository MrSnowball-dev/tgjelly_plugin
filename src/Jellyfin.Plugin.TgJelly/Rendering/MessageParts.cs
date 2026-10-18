using System.Collections.Generic;

namespace Jellyfin.Plugin.TgJelly.Rendering;

/// <summary>
/// A message in the segments <see cref="MessageFitter"/> shortens (spec §8.6), joined by blank lines in this order.
/// </summary>
/// <param name="Head">The heading lines (HTML).</param>
/// <param name="List">List lines (HTML), shortened from the end with "+N more".</param>
/// <param name="Overview">Plain text, escaped by the fitter, shortened first.</param>
/// <param name="Tail">The closing lines (HTML), e.g. the library.</param>
public sealed record MessageParts(string Head, IReadOnlyList<string> List, string? Overview, string Tail);
