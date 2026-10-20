using System.Collections.Generic;

namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// <c>GET TgJelly/Templates</c> (spec §11.2): every event's custom template with its fields.
/// </summary>
public sealed class TemplatesDto
{
    /// <summary>Gets the settings revision, for <c>expectedRevision</c>.</summary>
    public long Revision { get; init; }

    /// <summary>Gets one item per event key, in catalog order.</summary>
    public IReadOnlyList<TemplateItemDto> Items { get; init; } = [];
}
