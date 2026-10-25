using System;

namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// The place Jellyfin keeps TgJelly's settings: implemented by <see cref="Plugin"/>, faked in tests.
/// </summary>
public interface IConfigurationHost
{
    /// <summary>
    /// Raised when the settings were saved by someone other than <see cref="SettingsStore"/>,
    /// e.g. a POST to Jellyfin's generic plugin configuration endpoint.
    /// </summary>
    event EventHandler<ConfigurationSavedEventArgs>? ExternalConfigurationSaved;

    /// <summary>
    /// Gets the live settings object. Callers must not modify it.
    /// </summary>
    PluginConfiguration Configuration { get; }

    /// <summary>
    /// Runs <paramref name="action"/> while holding the host's private save lock. <see cref="SettingsStore"/> saves
    /// through it, and outside saves take the same lock, so two saves never get the same revision.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="action">The work to do under the lock.</param>
    /// <returns>The result of <paramref name="action"/>.</returns>
    T RunExclusive<T>(Func<T> action);

    /// <summary>
    /// Saves settings on behalf of <see cref="SettingsStore"/>. Does not raise <see cref="ExternalConfigurationSaved"/>.
    /// </summary>
    /// <param name="configuration">The settings to save; becomes the live object.</param>
    void SaveFromStore(PluginConfiguration configuration);
}
