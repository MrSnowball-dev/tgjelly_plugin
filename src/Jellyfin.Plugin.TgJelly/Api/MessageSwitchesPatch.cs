using System;
using Jellyfin.Plugin.TgJelly.Configuration;

namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// Some of a message's <see cref="MessageSwitches"/>: a switch that is <c>null</c> (not sent) keeps its value.
/// </summary>
public sealed class MessageSwitchesPatch
{
    /// <summary>Gets or sets <see cref="MessageSwitches.ShowPoster"/>, or <c>null</c> to keep it.</summary>
    public bool? ShowPoster { get; set; }

    /// <summary>Gets or sets <see cref="MessageSwitches.ShowOverview"/>, or <c>null</c> to keep it.</summary>
    public bool? ShowOverview { get; set; }

    /// <summary>Gets or sets <see cref="MessageSwitches.ShowRatingGenres"/>, or <c>null</c> to keep it.</summary>
    public bool? ShowRatingGenres { get; set; }

    /// <summary>Gets or sets <see cref="MessageSwitches.ShowOpenInJellyfin"/>, or <c>null</c> to keep it.</summary>
    public bool? ShowOpenInJellyfin { get; set; }

    /// <summary>Gets or sets <see cref="MessageSwitches.ShowProviderLinks"/>, or <c>null</c> to keep it.</summary>
    public bool? ShowProviderLinks { get; set; }

    /// <summary>Gets a value indicating whether at least one switch was sent.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasAny => ShowPoster is not null || ShowOverview is not null || ShowRatingGenres is not null
        || ShowOpenInJellyfin is not null || ShowProviderLinks is not null;

    /// <summary>
    /// Sets the switches that were sent.
    /// </summary>
    /// <param name="switches">The message's switches.</param>
    public void ApplyTo(MessageSwitches switches)
    {
        ArgumentNullException.ThrowIfNull(switches);
        switches.ShowPoster = ShowPoster ?? switches.ShowPoster;
        switches.ShowOverview = ShowOverview ?? switches.ShowOverview;
        switches.ShowRatingGenres = ShowRatingGenres ?? switches.ShowRatingGenres;
        switches.ShowOpenInJellyfin = ShowOpenInJellyfin ?? switches.ShowOpenInJellyfin;
        switches.ShowProviderLinks = ShowProviderLinks ?? switches.ShowProviderLinks;
    }
}
