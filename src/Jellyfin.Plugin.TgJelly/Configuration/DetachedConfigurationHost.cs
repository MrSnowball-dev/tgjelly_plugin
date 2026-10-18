using System;
using System.Threading;

namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// Stands in for <see cref="Plugin"/> when Jellyfin could not create the plugin instance (its constructor threw and
/// the plugin is marked Malfunctioned) while TgJelly's services are still registered. Resolving services must then
/// not stop the whole server (F3), so reads return defaults and saves fail inside supervised code.
/// </summary>
public sealed class DetachedConfigurationHost : IConfigurationHost
{
    private readonly Lock _saveGate = new();

    /// <inheritdoc />
    public event EventHandler<ConfigurationSavedEventArgs>? ExternalConfigurationSaved
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public PluginConfiguration Configuration { get; } = new();

    /// <inheritdoc />
    public T RunExclusive<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_saveGate)
        {
            return action();
        }
    }

    /// <inheritdoc />
    public void SaveFromStore(PluginConfiguration configuration)
        => throw new InvalidOperationException("The TgJelly plugin instance is missing, so settings cannot be saved.");
}
