using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// <c>POST TgJelly/Status/Dismiss</c>.
/// </summary>
public sealed class DismissRequest
{
    /// <summary>Gets or sets the ID of the notice to dismiss.</summary>
    [Required]
    public string NoticeId { get; set; } = string.Empty;
}
