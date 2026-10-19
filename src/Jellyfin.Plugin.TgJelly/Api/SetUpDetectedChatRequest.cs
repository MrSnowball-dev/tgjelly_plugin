namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// <c>POST TgJelly/DetectedChats/{chatId}</c>: set up a detected chat (spec §11.2).
/// </summary>
public sealed class SetUpDetectedChatRequest
{
    /// <summary>Gets or sets the revision the change is based on.</summary>
    public long ExpectedRevision { get; set; }

    /// <summary>Gets or sets the preset: Everything, NewMedia, AdminAlerts or Custom.</summary>
    public string Preset { get; set; } = "Everything";

    /// <summary>Gets or sets the forum topic to send to; omitted or 0 for the chat itself (General in a forum).</summary>
    public int? ThreadId { get; set; }
}
