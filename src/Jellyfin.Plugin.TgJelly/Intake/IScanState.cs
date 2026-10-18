namespace Jellyfin.Plugin.TgJelly.Intake;

/// <summary>
/// Whether a full library scan is running (F14); the Aggregator reads it only through this.
/// </summary>
public interface IScanState
{
    /// <summary>Gets a value indicating whether a full library validation is running.</summary>
    bool IsScanRunning { get; }
}
