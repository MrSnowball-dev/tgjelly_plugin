using System.Collections.Generic;
using Jellyfin.Plugin.TgJelly.Configuration;

namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// A 400 response: what's wrong, per field.
/// </summary>
public sealed class ErrorsDto
{
    /// <summary>Gets the errors.</summary>
    public IReadOnlyList<SettingsError> Errors { get; init; } = [];
}
