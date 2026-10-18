using System.Linq;
using System.Net.Mime;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Compat;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Delivery;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Strings;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// Admin-only REST API for the dashboard page (spec §11.2). Later plans add endpoints.
/// </summary>
[ApiController]
[Route("TgJelly")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public class TgJellyController : ControllerBase
{
    private readonly CompatResult _compat;
    private readonly InstanceGate _gate;
    private readonly SettingsStore _settings;
    private readonly StateStore _state;
    private readonly SecretsStore _secrets;
    private readonly BotConnection _connection;
    private readonly Outbox _outbox;
    private readonly HealthService _health;
    private readonly StringTable _strings;

    /// <summary>
    /// Initializes a new instance of the <see cref="TgJellyController"/> class.
    /// </summary>
    /// <param name="compat">The CompatGuard result.</param>
    /// <param name="gate">The instance gate.</param>
    /// <param name="settings">The settings store.</param>
    /// <param name="state">The state store.</param>
    /// <param name="secrets">The secrets store.</param>
    /// <param name="connection">The bot connection.</param>
    /// <param name="outbox">The Outbox.</param>
    /// <param name="health">The health service.</param>
    /// <param name="strings">The string table.</param>
    public TgJellyController(CompatResult compat, InstanceGate gate, SettingsStore settings, StateStore state, SecretsStore secrets, BotConnection connection, Outbox outbox, HealthService health, StringTable strings)
    {
        _compat = compat;
        _gate = gate;
        _settings = settings;
        _state = state;
        _secrets = secrets;
        _connection = connection;
        _outbox = outbox;
        _health = health;
        _strings = strings;
    }

    /// <summary>
    /// Gets the plugin status shown at the top of the dashboard page.
    /// </summary>
    /// <returns>The status.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<StatusDto> GetStatus()
    {
        var state = !_compat.IsCompatible ? ConnectionState.Incompatible : !_gate.IsOwner ? ConnectionState.WaitingForInstance : _connection.State;
        var username = _connection.BotUsername.Length > 0 ? _connection.BotUsername : _state.Read(s => s.Telegram.BotUsername);
        var message = state switch
        {
            ConnectionState.Incompatible => _compat.Message,
            ConnectionState.WaitingForInstance => _gate.WaitingMessage,
            _ => _strings.Get("connection." + state, ("bot", username)),
        };

        return new StatusDto
        {
            Version = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? string.Empty,
            BuildTarget = _compat.BuildLabel,
            Compatible = _compat.IsCompatible,
            CompatMessage = _compat.Message,
            InstanceOwner = _gate.IsOwner,
            InstanceMessage = _gate.WaitingMessage,
            Revision = _settings.Current.Revision,
            BackupRestoredAt = _state.Read(s => s.Status.BackupRestoredAt),
            LoadWarnings = [.. _state.Read(s => s.Status.LoadWarnings.ToArray()), .. _secrets.LoadWarning is { } w ? new[] { w } : []],
            LastErrors = _state.LastErrorsSnapshot(),
            ConnectionState = state.ToString(),
            ConnectionMessage = message,
            BotUsername = username,
            HasToken = !string.IsNullOrEmpty(_secrets.Load().BotToken),
            Counters = _state.Read(s => new StatusCounters { DroppedIntake = s.Status.Counters.DroppedIntake, DroppedOutbox = s.Status.Counters.DroppedOutbox, Evictions = s.Status.Counters.Evictions }),
            OutboxCount = _outbox.Count,
            UnhealthyDestinations = _settings.Current.Destinations.Count(d => !_health.IsDeliverable(d.Id)),
        };
    }
}
