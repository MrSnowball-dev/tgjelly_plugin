namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// Update-check settings (spec §7.6; plan 9 design §7): which kinds of newer Jellyfin release are announced.
/// </summary>
public class UpdateCheckOptions
{
    /// <summary>Gets or sets a value indicating whether the update checker may run.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether a fix for the running version is announced (10.11.1 to 10.11.2).</summary>
    public bool Fixes { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether a new release with the same major number is announced (10.11 to 10.12).</summary>
    public bool NewReleases { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether a new major version is announced (10.x to 12.0).</summary>
    public bool NewMajor { get; set; }
}
