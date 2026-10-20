using System.Collections.Generic;

namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// The outcome of validating a template (spec §8.5).
/// </summary>
public sealed class TemplateValidationDto
{
    /// <summary>Gets the problems that block saving.</summary>
    public IReadOnlyList<TemplateErrorDto> Errors { get; init; } = [];

    /// <summary>Gets the notes that don't block saving.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Gets the sample render; empty when there are errors before rendering.</summary>
    public string Preview { get; init; } = string.Empty;

    /// <summary>Gets the preview's length without markup.</summary>
    public int Length { get; init; }

    /// <summary>Gets the length limit for this event.</summary>
    public int Limit { get; init; }
}
