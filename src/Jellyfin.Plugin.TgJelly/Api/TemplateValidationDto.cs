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

    /// <summary>Gets the raw sample render, not fitted; empty when there are errors before rendering.</summary>
    public string Preview { get; init; } = string.Empty;

    /// <summary>Gets the sample message's HTML as it is sent, fitted to <see cref="Limit"/>; empty when there are errors.</summary>
    public string Html { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether the message goes out as a photo caption.</summary>
    public bool Caption { get; init; }

    /// <summary>Gets a value indicating whether the message has a poster.</summary>
    public bool Poster { get; init; }

    /// <summary>Gets the sample message's button rows.</summary>
    public IReadOnlyList<IReadOnlyList<TemplateButtonDto>> Buttons { get; init; } = [];

    /// <summary>Gets the length of the unfitted preview without markup.</summary>
    public int Length { get; init; }

    /// <summary>Gets the length limit: 1024 for a caption, otherwise 4096.</summary>
    public int Limit { get; init; }

    /// <summary>Gets the text this result is for, so the page can tell a result for older text.</summary>
    public string ForText { get; init; } = string.Empty;

    /// <summary>Gets the template's top-level pieces in <see cref="ForText"/>; empty when there are errors.</summary>
    public IReadOnlyList<TemplatePieceDto> Pieces { get; init; } = [];

    /// <summary>Gets <see cref="Html"/> with each piece's output between invisible markers, for the preview only; empty when there are errors.</summary>
    public string MarkedHtml { get; init; } = string.Empty;
}
