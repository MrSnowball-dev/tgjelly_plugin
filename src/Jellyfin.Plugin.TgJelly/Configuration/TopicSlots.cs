using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// The slots a forum destination can route to their own topics: Media as a group, or each media
/// event on its own once the destination has separate media topics; Security and Server as groups. The digest and the
/// alert summary mix kinds, so they follow the destination's topic.
/// </summary>
public static class TopicSlots
{
    /// <summary>Gets every slot, in the editor's order.</summary>
    public static IReadOnlyList<string> All { get; } =
        ["media", .. EventCatalog.AllKeys.Where(k => EventCatalog.GroupOf(k) == "media"), "security", "server"];
}
