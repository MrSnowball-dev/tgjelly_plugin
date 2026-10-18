namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// Global message toggles (spec §8.4).
/// </summary>
public class MessageOptions
{
    /// <summary>Gets or sets a value indicating whether posters are sent.</summary>
    public bool ShowPoster { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether overviews are shown.</summary>
    public bool ShowOverview { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether rating and genres are shown.</summary>
    public bool ShowRatingGenres { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the "Open in Jellyfin" button is shown.</summary>
    public bool ShowOpenInJellyfin { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether IMDb/TMDb buttons are shown.</summary>
    public bool ShowProviderLinks { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether quality upgrades are announced.</summary>
    public bool AnnounceUpgrades { get; set; }
}
