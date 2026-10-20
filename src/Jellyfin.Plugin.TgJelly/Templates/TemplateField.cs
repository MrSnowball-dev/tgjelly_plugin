namespace Jellyfin.Plugin.TgJelly.Templates;

/// <summary>
/// One field a custom template can use for an event key (spec §8.5).
/// </summary>
/// <param name="Name">The field name, as written in a template.</param>
/// <param name="Type">One of <c>string</c>, <c>number</c>, <c>bool</c>, <c>string[]</c> or <c>episode[]</c>.</param>
/// <param name="Description">What the field holds.</param>
/// <param name="Sample">A sample value of that type, for previews and tests.</param>
/// <param name="Optional">Whether the field can be empty (<c>null</c>) when the data is missing.</param>
public sealed record TemplateField(string Name, string Type, string Description, object? Sample, bool Optional);
