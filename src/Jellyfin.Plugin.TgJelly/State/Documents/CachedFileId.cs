using System;

namespace Jellyfin.Plugin.TgJelly.State.Documents;

/// <summary>
/// A poster's Telegram file_id, reused for other destinations and resends.
/// </summary>
public sealed class CachedFileId
{
    /// <summary>Gets or sets the file_id.</summary>
    public string FileId { get; set; } = string.Empty;

    /// <summary>Gets or sets when it was cached; entries live 24 h.</summary>
    public DateTimeOffset CachedAt { get; set; }
}
