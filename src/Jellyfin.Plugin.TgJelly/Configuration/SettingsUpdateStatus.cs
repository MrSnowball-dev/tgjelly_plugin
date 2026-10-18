namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// The outcome of <see cref="SettingsStore.Update"/>.
/// </summary>
public enum SettingsUpdateStatus
{
    /// <summary>The change was validated and saved.</summary>
    Saved,

    /// <summary>The caller's expected revision is stale; nothing was saved (HTTP 409).</summary>
    Conflict,

    /// <summary>The change failed validation; nothing was saved (HTTP 400).</summary>
    Invalid,

    /// <summary>The change decided there was nothing to do; nothing was saved and no event fired.</summary>
    Unchanged,
}
