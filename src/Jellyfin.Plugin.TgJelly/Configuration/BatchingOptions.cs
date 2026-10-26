namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// New-media batching values (ranges in <see cref="SettingsValidator"/>).
/// </summary>
public class BatchingOptions
{
    /// <summary>Gets or sets the quiet window that closes a group, in seconds (5–3600).</summary>
    public int QuietWindowSeconds { get; set; } = 120;

    /// <summary>Gets or sets the maximum time a group or buffer is held, in minutes (1–240).</summary>
    public int MaxHoldMinutes { get; set; } = 30;

    /// <summary>Gets or sets the quiet time that releases a buffer, in seconds (5–600).</summary>
    public int ReleaseQuietSeconds { get; set; } = 20;

    /// <summary>Gets or sets how many groups turn a release into a digest (2–100).</summary>
    public int DigestThreshold { get; set; } = 10;

    /// <summary>Gets or sets how long late items edit the sent message, in hours (0–24; 0 = never).</summary>
    public int LateEditHours { get; set; } = 6;

    /// <summary>Gets or sets how long an item waits for metadata, in minutes (1–120).</summary>
    public int ReadyTimeoutMinutes { get; set; } = 15;
}
