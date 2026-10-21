using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;

namespace Jellyfin.Plugin.TgJelly.Templates;

/// <summary>
/// Every key a template can be written for: the event keys and the digest.
/// </summary>
public static class TemplateKeys
{
    /// <summary>The key of the media digest.</summary>
    public const string Digest = "media.digest";

    /// <summary>
    /// Gets every template key: <see cref="EventCatalog.AllKeys"/> followed by <see cref="Digest"/>.
    /// </summary>
    public static IReadOnlyList<string> All { get; } = [.. EventCatalog.AllKeys, Digest];
}
