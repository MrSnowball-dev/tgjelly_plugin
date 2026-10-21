using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net.Mime;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Compat;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Delivery;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.Library;
using Jellyfin.Plugin.TgJelly.Pipeline;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Status;
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
    private readonly MediaReadiness _readiness;
    private readonly ILibraryAccess _library;
    private readonly NoticeBuilder _notices;

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
    /// <param name="readiness">Media readiness, for the pending count.</param>
    /// <param name="library">The library, for removed-library filters.</param>
    /// <param name="notices">The notice builder.</param>
    public TgJellyController(CompatResult compat, InstanceGate gate, SettingsStore settings, StateStore state, SecretsStore secrets, BotConnection connection, Outbox outbox, HealthService health, StringTable strings, MediaReadiness readiness, ILibraryAccess library, NoticeBuilder notices)
    {
        _notices = notices;
        _readiness = readiness;
        _library = library;
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
            ConnectionState.Connected => _strings.Get("web.status.connected"),
            _ => _strings.Get("connection." + state, ("bot", username)),
        };

        var settings = _settings.Current;
        var existing = _library.GetVirtualFolders().Select(l => l.Id).ToHashSet();
        var updateCheck = _state.Read(s => (s.UpdateCheck.ConsecutiveFailures, s.UpdateCheck.LastError));
        return new StatusDto
        {
            Version = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? string.Empty,
            BuildTarget = _compat.BuildLabel,
            Compatible = _compat.IsCompatible,
            CompatMessage = _compat.Message,
            InstanceOwner = _gate.IsOwner,
            InstanceMessage = _gate.WaitingMessage,
            Revision = settings.Revision,
            BackupRestoredAt = _state.Read(s => s.Status.BackupRestoredAt),
            LoadWarnings = [.. _state.Read(s => s.Status.LoadWarnings.ToArray()), .. _secrets.LoadWarning is { } w ? new[] { w } : []],
            LastErrors = [.. _state.LastErrorsSnapshot().Select(e => new StatusError { At = e.At, Source = e.Source, Message = DestinationRef.WithTitles(e.Message, settings.Destinations) })],
            ConnectionState = state.ToString(),
            ConnectionMessage = message,
            BotUsername = username,
            HasToken = !string.IsNullOrEmpty(_secrets.Load().BotToken),
            Counters = _state.Read(s => new StatusCounters { DroppedIntake = s.Status.Counters.DroppedIntake, DroppedOutbox = s.Status.Counters.DroppedOutbox, Evictions = s.Status.Counters.Evictions }),
            OutboxCount = _outbox.Count,
            UnhealthyDestinations = settings.Destinations.Count(d => !_health.IsDeliverable(d.Id)),
            PendingMedia = _state.IsLoaded ? _readiness.PendingCount : 0,
            UpdateCheckFailing = updateCheck.ConsecutiveFailures >= 3 ? _strings.Get("update.failing", ("reason", updateCheck.LastError)) : null,
            RemovedLibraryFilters = settings.Destinations.Count(d => LibraryFilter.RefersOnlyToRemoved(d, existing)),
            Notices = [.. _notices.Visible().Select(n => new NoticeDto { Id = n.Id, Kind = n.Kind, Text = n.Text })],
        };
    }

    /// <summary>
    /// Dismisses a Status notice. Answers 204 whether or not the notice is still active.
    /// </summary>
    /// <param name="request">The notice ID.</param>
    /// <returns>No content.</returns>
    [HttpPost("Status/Dismiss")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult Dismiss([FromBody, Required] DismissRequest request)
    {
        if (ApiGuard.Block(_gate, _state) is { } blocked)
        {
            return blocked;
        }

        _notices.Dismiss(request.NoticeId);
        return NoContent();
    }
}
