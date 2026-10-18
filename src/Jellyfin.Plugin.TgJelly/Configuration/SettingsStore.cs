using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.TgJelly.State;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// The only way TgJelly changes its settings (spec §10.4): under the host's save lock, clone, change, normalize, validate, bump the
/// revision, save, back up to settings.bak.json, then raise <see cref="SettingsChanged"/> once.
/// </summary>
public sealed class SettingsStore : IDisposable
{
    private readonly IConfigurationHost _host;
    private readonly DataPaths _paths;
    private readonly StateStore _state;
    private readonly TimeProvider _time;
    private readonly ILogger<SettingsStore> _logger;
    private readonly Lock _notifyGate = new();
    private long _lastNotifiedRevision = long.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsStore"/> class.
    /// </summary>
    /// <param name="host">Where Jellyfin keeps the settings.</param>
    /// <param name="paths">TgJelly's data paths.</param>
    /// <param name="state">The state store, for the "backup restored" notice.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public SettingsStore(IConfigurationHost host, DataPaths paths, StateStore state, TimeProvider time, ILogger<SettingsStore> logger)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _paths = paths;
        _state = state;
        _time = time;
        _logger = logger;
        _host.ExternalConfigurationSaved += OnExternalConfigurationSaved;
    }

    /// <summary>
    /// Raised after a save with a copy of the new settings. Deliveries never go backwards: a revision older than
    /// one already delivered is skipped, so handlers always end on the newest settings. A throwing handler is
    /// logged and does not affect other handlers or the save.
    /// </summary>
    public event EventHandler<SettingsChangedEventArgs>? SettingsChanged;

    /// <summary>
    /// Gets a copy of the current settings.
    /// </summary>
    public PluginConfiguration Current => _host.RunExclusive(() => Clone(_host.Configuration));

    /// <summary>
    /// Applies a change to a copy of the settings and saves it.
    /// </summary>
    /// <param name="expectedRevision">The revision the caller based its change on, or <c>null</c> to skip the check.</param>
    /// <param name="change">The change, applied to a copy.</param>
    /// <returns>The outcome.</returns>
    public SettingsUpdateResult Update(long? expectedRevision, Action<PluginConfiguration> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return UpdateIf(expectedRevision, c =>
        {
            change(c);
            return true;
        });
    }

    /// <summary>
    /// Applies a change that may decide there is nothing to do (a separate name, not an <c>Update</c> overload, so a
    /// lambda such as <c>c =&gt; c.Message.ShowPoster = false</c> can't silently bind here): when <paramref name="change"/> returns
    /// <c>false</c>, nothing is saved, the revision stays and no event fires (<see cref="SettingsUpdateStatus.Unchanged"/>).
    /// The revision check still runs first.
    /// </summary>
    /// <param name="expectedRevision">The revision the caller based its change on, or <c>null</c> to skip the check.</param>
    /// <param name="change">The change, applied to a copy; returns whether anything should be saved.</param>
    /// <returns>The outcome.</returns>
    public SettingsUpdateResult UpdateIf(long? expectedRevision, Func<PluginConfiguration, bool> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        var result = _host.RunExclusive(() =>
        {
            var current = _host.Configuration;
            if (expectedRevision.HasValue && expectedRevision.Value != current.Revision)
            {
                return new SettingsUpdateResult(SettingsUpdateStatus.Conflict, Clone(current), []);
            }

            var next = Clone(current);
            if (!change(next))
            {
                return new SettingsUpdateResult(SettingsUpdateStatus.Unchanged, Clone(current), []);
            }

            next.Normalize(); // a null string would otherwise be saved and make every later Clone throw
            var errors = SettingsValidator.Validate(next);
            if (errors.Count > 0)
            {
                return new SettingsUpdateResult(SettingsUpdateStatus.Invalid, Clone(current), errors);
            }

            next.Revision = current.Revision + 1;
            _host.SaveFromStore(next);
            TryWriteBackup(next);
            return new SettingsUpdateResult(SettingsUpdateStatus.Saved, Clone(next), []);
        });

        if (result.Status == SettingsUpdateStatus.Saved)
        {
            RaiseSettingsChanged(Clone(result.Configuration));
        }

        return result;
    }

    /// <summary>
    /// Restores settings.bak.json when Jellyfin has replaced the settings with defaults after a load error (F9).
    /// Requires the state store to be loaded.
    /// </summary>
    /// <returns><c>true</c> when the backup was restored.</returns>
    public bool RestoreBackupIfWiped()
    {
        var restored = _host.RunExclusive(() =>
        {
            var current = _host.Configuration;
            if (current.Revision != 0 || current.Destinations.Length != 0 || !File.Exists(_paths.SettingsBackup))
            {
                return null;
            }

            PluginConfiguration? backup;
            try
            {
                backup = JsonSerializer.Deserialize<PluginConfiguration>(File.ReadAllText(_paths.SettingsBackup), JsonDefaults.Options);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "TgJelly settings backup {Path} could not be read", _paths.SettingsBackup);
                return null;
            }

            if (backup is null || backup.Revision <= 0)
            {
                return null;
            }

            backup.Normalize();
            _host.SaveFromStore(backup);
            return Clone(backup);
        });

        if (restored is null)
        {
            return false;
        }

        _state.Update(s => s.Status.BackupRestoredAt = _time.GetUtcNow());
        _logger.LogWarning("TgJelly settings were reset by Jellyfin; restored revision {Revision} from backup", restored.Revision);
        RaiseSettingsChanged(restored);
        return true;
    }

    /// <inheritdoc />
    public void Dispose() => _host.ExternalConfigurationSaved -= OnExternalConfigurationSaved;

    private static PluginConfiguration Clone(PluginConfiguration configuration)
        => JsonSerializer.Deserialize<PluginConfiguration>(JsonSerializer.Serialize(configuration, JsonDefaults.Options), JsonDefaults.Options)!;

    // The backup is a safety net: failing to write it must not turn a successful save into an error.
    private void TryWriteBackup(PluginConfiguration configuration)
    {
        try
        {
            AtomicFile.WriteAllText(_paths.SettingsBackup, JsonSerializer.Serialize(configuration, JsonDefaults.Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "TgJelly could not write its settings backup {Path}", _paths.SettingsBackup);
        }
    }

    /// <summary>
    /// Delivers <see cref="SettingsChanged"/>, skipping revisions older than the last one delivered.
    /// </summary>
    /// <param name="saved">A copy of the saved settings.</param>
    internal void RaiseSettingsChanged(PluginConfiguration saved)
    {
        lock (_notifyGate)
        {
            if (saved.Revision <= _lastNotifiedRevision)
            {
                return;
            }

            _lastNotifiedRevision = saved.Revision;
            var handlers = SettingsChanged;
            if (handlers is null)
            {
                return;
            }

            var args = new SettingsChangedEventArgs(saved);
            foreach (var handler in handlers.GetInvocationList())
            {
                try
                {
                    ((EventHandler<SettingsChangedEventArgs>)handler)(this, args);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "A TgJelly SettingsChanged handler failed");
                }
            }
        }
    }

    private void OnExternalConfigurationSaved(object? sender, ConfigurationSavedEventArgs e)
    {
        try
        {
            // Back up and publish the live object: it is what Jellyfin now has, whatever the event carried.
            var saved = _host.RunExclusive(() =>
            {
                var live = Clone(_host.Configuration);
                TryWriteBackup(live);
                return live;
            });

            RaiseSettingsChanged(saved);
        }
        catch (Exception ex)
        {
            // Never let our handler break Jellyfin's own save.
            _logger.LogError(ex, "TgJelly failed to process an outside settings save");
        }
    }
}
