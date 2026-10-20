namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// A field a template can use.
/// </summary>
public sealed class TemplateFieldDto
{
    /// <summary>Gets the field name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the field type.</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>Gets what the field holds.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether the field can be empty.</summary>
    public bool Optional { get; init; }
}
