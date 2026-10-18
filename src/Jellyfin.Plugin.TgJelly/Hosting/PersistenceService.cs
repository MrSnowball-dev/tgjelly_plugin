using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.Hosting;

/// <summary>
/// Waits until this instance owns the data folder, loads the state files and secrets.json, restores a wiped settings
/// backup, then writes due state once per second. Flushes everything on stop (spec §10.4, §12).
/// </summary>
public sealed class PersistenceService : BackgroundService
{
    private readonly InstanceGate _gate;
    private readonly StateStore _state;
    private readonly SettingsStore _settings;
    private readonly SecretsStore _secrets;
    private readonly TimeProvider _time;
    private readonly ILogger<PersistenceService> _logger;
    private bool _restoreChecked;

    /// <summary>
    /// Initializes a new instance of the <see cref="PersistenceService"/> class.
    /// </summary>
    /// <param name="gate">The instance gate.</param>
    /// <param name="state">The state store.</param>
    /// <param name="settings">The settings store.</param>
    /// <param name="secrets">The secrets store.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public PersistenceService(
        InstanceGate gate,
        StateStore state,
        SettingsStore settings,
        SecretsStore secrets,
        TimeProvider time,
        ILogger<PersistenceService> logger)
    {
        _gate = gate;
        _state = state;
        _settings = settings;
        _secrets = secrets;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TgJelly persistence loop did not stop cleanly");
        }

        try
        {
            _state.FlushAll();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TgJelly could not write its state on shutdown");
        }
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Supervisor.RunAsync("persistence", RunAsync, _time, _logger, stoppingToken);

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        await _gate.WaitForOwnershipAsync(stoppingToken).ConfigureAwait(false);
        if (!_state.IsLoaded)
        {
            _state.Load();
        }

        // Tracked separately from IsLoaded, so a failed restore is retried after the supervisor restarts the loop.
        if (!_restoreChecked)
        {
            _settings.RestoreBackupIfWiped();
            _restoreChecked = true;
        }

        // Reads secrets.json once now, so an unreadable file shows up in Status right away.
        _ = _secrets.Load();

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            _state.FlushDue();
        }
    }
}
