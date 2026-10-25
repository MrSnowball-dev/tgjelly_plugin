namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// A forum topic TgJelly knows for a destination's chat: one it created (with its name) or one chosen by link.
/// </summary>
public class KnownTopic
{
    /// <summary>Gets or sets the topic's thread id.</summary>
    public int ThreadId { get; set; }

    /// <summary>Gets or sets the topic's name, or empty when TgJelly doesn't know it (a topic chosen by link).</summary>
    public string Name { get; set; } = string.Empty;
}
