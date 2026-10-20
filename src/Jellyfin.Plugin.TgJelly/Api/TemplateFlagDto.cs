using System;

namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// A send-time template problem (spec §9.4 row 6).
/// </summary>
public sealed class TemplateFlagDto
{
    /// <summary>Gets the kind: <c>error</c> or <c>truncated</c>.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>Gets the error text, empty for <c>truncated</c>.</summary>
    public string Error { get; init; } = string.Empty;

    /// <summary>Gets when the flag was set.</summary>
    public DateTimeOffset At { get; init; }
}
