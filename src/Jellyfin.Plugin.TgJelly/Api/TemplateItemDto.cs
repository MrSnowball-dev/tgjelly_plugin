using System.Collections.Generic;

namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// One event's template, fields, sample models and flag.
/// </summary>
public sealed class TemplateItemDto
{
    /// <summary>Gets the event key.</summary>
    public string EventKey { get; init; } = string.Empty;

    /// <summary>Gets the event's display name.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether this is a media event (1024-character caption limit).</summary>
    public bool Media { get; init; }

    /// <summary>Gets a value indicating whether the custom template is used.</summary>
    public bool Enabled { get; init; }

    /// <summary>Gets the template text; empty without an override.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Gets the fields a template can use.</summary>
    public IReadOnlyList<TemplateFieldDto> Fields { get; init; } = [];

    /// <summary>Gets a model with a value for every field.</summary>
    public IReadOnlyDictionary<string, object?> SampleFull { get; init; } = new Dictionary<string, object?>();

    /// <summary>Gets a model as it looks when data is missing.</summary>
    public IReadOnlyDictionary<string, object?> SampleSparse { get; init; } = new Dictionary<string, object?>();

    /// <summary>Gets the send-time problem flag, or <c>null</c>.</summary>
    public TemplateFlagDto? Flag { get; init; }
}
