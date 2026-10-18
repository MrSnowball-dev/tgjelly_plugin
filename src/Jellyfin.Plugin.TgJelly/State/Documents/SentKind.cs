namespace Jellyfin.Plugin.TgJelly.State.Documents;

/// <summary>
/// What a sent message is, which decides how it is edited.
/// </summary>
public enum SentKind
{
    /// <summary>A photo with a caption (<c>editMessageCaption</c>).</summary>
    Photo,

    /// <summary>A text message (<c>editMessageText</c>).</summary>
    Text,
}
