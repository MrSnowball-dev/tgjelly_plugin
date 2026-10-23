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

    /// <summary>Gets the template text in use: the edited one, otherwise the built-in one.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Gets the built-in template text.</summary>
    public string BuiltIn { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether the text in use is an edited one.</summary>
    public bool Edited { get; init; }

    /// <summary>Gets the fields a template can use.</summary>
    public IReadOnlyList<TemplateFieldDto> Fields { get; init; } = [];

    /// <summary>Gets a model with a value for every field.</summary>
    public IReadOnlyDictionary<string, object?> SampleFull { get; init; } = new Dictionary<string, object?>();

    /// <summary>Gets a model as it looks when data is missing.</summary>
    public IReadOnlyDictionary<string, object?> SampleSparse { get; init; } = new Dictionary<string, object?>();

    /// <summary>
    /// Gets the message switches that change this message, in the order shown (<c>MessageSwitches.Applicable</c>,
    /// design §6.2): the dashboard shows only these.
    /// </summary>
    public IReadOnlyList<string> ApplicableSwitches { get; init; } = [];

    /// <summary>Gets the send-time problem flag, or <c>null</c>.</summary>
    public TemplateFlagDto? Flag { get; init; }
}
