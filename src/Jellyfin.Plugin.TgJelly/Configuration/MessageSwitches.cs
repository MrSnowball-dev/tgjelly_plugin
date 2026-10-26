using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// What one message shows. A new instance has the media defaults, all on;
/// <see cref="DefaultFor"/> gives each message's own defaults.
/// </summary>
public class MessageSwitches
{
    private static readonly string[] AllNames =
    [
        nameof(ShowPoster), nameof(ShowOverview), nameof(ShowRatingGenres), nameof(ShowOpenInJellyfin), nameof(ShowProviderLinks),
    ];

    private static readonly string[] PosterOnly = [nameof(ShowPoster)];

    // What each message's rendering honours today: MediaLayouts sends the poster and the Open button for every media
    // group, and the IMDb/TMDb row only for a movie or a series; TemplateModelBuilder empties overview, rating and genres
    // only where the message has those fields (not episodes or albums). A digest is text only (NotificationRenderer),
    // and its fields have none of these, so no switch changes it.
    private static readonly Dictionary<string, string[]> MediaApplicable = new(StringComparer.Ordinal)
    {
        ["media.movie"] = AllNames,
        ["media.episodes"] = [nameof(ShowPoster), nameof(ShowOpenInJellyfin), nameof(ShowProviderLinks)],
        ["media.album"] = [nameof(ShowPoster), nameof(ShowOpenInJellyfin)],
        ["media.musicvideo"] = [nameof(ShowPoster), nameof(ShowOverview), nameof(ShowRatingGenres), nameof(ShowOpenInJellyfin)],
        ["media.book"] = [nameof(ShowPoster), nameof(ShowOverview), nameof(ShowRatingGenres), nameof(ShowOpenInJellyfin)],
        [MessageOptions.DigestKey] = [],
    };

    /// <summary>Gets every switch name, in the order shown.</summary>
    public static IReadOnlyList<string> Names => AllNames;

    /// <summary>Gets or sets a value indicating whether the message is sent with a poster.</summary>
    public bool ShowPoster { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the overview is shown.</summary>
    public bool ShowOverview { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the rating and genres are shown.</summary>
    public bool ShowRatingGenres { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the "Open in Jellyfin" button is shown.</summary>
    public bool ShowOpenInJellyfin { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the IMDb/TMDb buttons are shown.</summary>
    public bool ShowProviderLinks { get; set; } = true;

    /// <summary>
    /// Returns the switches that change a message, in the order shown; the others are hidden for it.
    /// </summary>
    /// <param name="eventKey">The event key, or <see cref="MessageOptions.DigestKey"/>.</param>
    /// <returns>The switch names: none for the digest, only <see cref="ShowPoster"/> for Security and Server.</returns>
    public static IReadOnlyList<string> Applicable(string eventKey)
    {
        ArgumentNullException.ThrowIfNull(eventKey);
        return MediaApplicable.TryGetValue(eventKey, out var names) ? names : EventCatalog.GroupOf(eventKey) == "media" ? [] : PosterOnly;
    }

    /// <summary>
    /// Returns the switches a message has until someone changes them: media messages show everything, Security and
    /// Server messages nothing (no poster).
    /// </summary>
    /// <param name="eventKey">The event key.</param>
    /// <returns>A new instance.</returns>
    public static MessageSwitches DefaultFor(string eventKey)
    {
        ArgumentNullException.ThrowIfNull(eventKey);
        var on = EventCatalog.GroupOf(eventKey) == "media";
        return new MessageSwitches { ShowPoster = on, ShowOverview = on, ShowRatingGenres = on, ShowOpenInJellyfin = on, ShowProviderLinks = on };
    }

    /// <summary>
    /// Returns a switch by name.
    /// </summary>
    /// <param name="name">One of <see cref="Names"/>.</param>
    /// <returns>Its value.</returns>
    public bool Get(string name) => name switch
    {
        nameof(ShowPoster) => ShowPoster,
        nameof(ShowOverview) => ShowOverview,
        nameof(ShowRatingGenres) => ShowRatingGenres,
        nameof(ShowOpenInJellyfin) => ShowOpenInJellyfin,
        nameof(ShowProviderLinks) => ShowProviderLinks,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Not a message switch."),
    };

    /// <summary>
    /// Sets a switch by name.
    /// </summary>
    /// <param name="name">One of <see cref="Names"/>.</param>
    /// <param name="value">The new value.</param>
    public void Set(string name, bool value)
    {
        switch (name)
        {
            case nameof(ShowPoster):
                ShowPoster = value;
                break;
            case nameof(ShowOverview):
                ShowOverview = value;
                break;
            case nameof(ShowRatingGenres):
                ShowRatingGenres = value;
                break;
            case nameof(ShowOpenInJellyfin):
                ShowOpenInJellyfin = value;
                break;
            case nameof(ShowProviderLinks):
                ShowProviderLinks = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(name), name, "Not a message switch.");
        }
    }

    /// <summary>
    /// Returns a copy.
    /// </summary>
    /// <returns>A new instance with the same values.</returns>
    public MessageSwitches Copy() => (MessageSwitches)MemberwiseClone();
}
