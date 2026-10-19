namespace Jellyfin.Plugin.TgJelly.State.Documents;

/// <summary>
/// What a <see cref="RenderModel"/> describes.
/// </summary>
public enum RenderKind
{
    /// <summary>A media group (a movie, a season batch, an album…).</summary>
    Media,

    /// <summary>A digest of many media groups.</summary>
    Digest,

    /// <summary>A security or server event.</summary>
    Alert,
}
