using System;
using System.Threading;
using Jellyfin.Plugin.TgJelly.Configuration;

namespace Jellyfin.Plugin.TgJelly.Tests.Fakes;

public sealed class FakeConfigurationHost : IConfigurationHost
{
    public event EventHandler<ConfigurationSavedEventArgs>? ExternalConfigurationSaved;

    public PluginConfiguration Configuration { get; private set; } = new();

    private readonly Lock _saveGate = new();

    public int Saves { get; private set; }

    /// <summary>Gets or sets an exception every read throws (a broken host).</summary>
    public Exception? Fault { get; set; }

    public T RunExclusive<T>(Func<T> action)
    {
        lock (_saveGate)
        {
            if (Fault is { } fault)
            {
                throw fault;
            }

            return action();
        }
    }

    public void SaveFromStore(PluginConfiguration configuration)
    {
        Configuration = configuration;
        Saves++;
    }

    /// <summary>Simulates Jellyfin's generic configuration endpoint saving settings.</summary>
    public void SaveFromOutside(PluginConfiguration configuration)
    {
        Configuration = configuration;
        ExternalConfigurationSaved?.Invoke(this, new ConfigurationSavedEventArgs(configuration));
    }
}
