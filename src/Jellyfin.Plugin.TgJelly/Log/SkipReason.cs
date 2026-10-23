namespace Jellyfin.Plugin.TgJelly.Log;

/// <summary>
/// Why a message was not sent to a destination, or to any (design §8.2). Each maps to a <c>web.log.why.*</c> string.
/// </summary>
public enum SkipReason
{
    /// <summary>The destination is paused.</summary>
    Paused,

    /// <summary>The destination has this event switched off.</summary>
    EventOff,

    /// <summary>The destination's library filter leaves the item's library out.</summary>
    LibraryExcluded,

    /// <summary>No destination has this event switched on (a row without a destination).</summary>
    NoDestinationWants,

    /// <summary>The item was removed from Jellyfin before the message went out.</summary>
    ItemGone,

    /// <summary>The item replaces a copy already announced, and announcing upgrades is off.</summary>
    Upgrade,

    /// <summary>The destination was removed while the message was held.</summary>
    DestinationRemoved,
}
