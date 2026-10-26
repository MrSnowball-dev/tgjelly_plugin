using System.ComponentModel.DataAnnotations;
using Jellyfin.Plugin.TgJelly.Configuration;

namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// <c>PUT TgJelly/Settings</c>: replaces these sections, except <c>Message</c>, which merges; proxy fields aren't part of it.
/// </summary>
public sealed class SettingsUpdateRequest
{
    /// <summary>Gets or sets the revision the change is based on.</summary>
    public long ExpectedRevision { get; set; }

    /// <summary>Gets or sets the public server URL.</summary>
    [Required(AllowEmptyStrings = true)]
    public string PublicUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the message switches to change: entries merge by event key, and switches not sent keep their value.</summary>
    [Required]
    public MessageSettingsUpdate Message { get; set; } = new();

    /// <summary>Gets or sets the batching values.</summary>
    [Required]
    public BatchingOptions Batching { get; set; } = new();

    /// <summary>Gets or sets the update-check settings.</summary>
    [Required]
    public UpdateCheckOptions UpdateCheck { get; set; } = new();
}
