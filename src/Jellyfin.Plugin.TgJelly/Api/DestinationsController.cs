using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot;
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
using Microsoft.Extensions.Logging;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;

namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// Destination endpoints (spec §11.2): list, add by chat ID or topic link (checked first, with the automatic test
/// message), edit, remove, test, check, and the bot's links.
/// </summary>
[ApiController]
[Route("TgJelly")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public class DestinationsController : ControllerBase
{
    private readonly SettingsStore _settings;
    private readonly BotConnection _connection;
    private readonly DestinationChecker _checker;
    private readonly HealthService _health;
    private readonly DestinationTester _tester;
    private readonly Outbox _outbox;
    private readonly StringTable _strings;
    private readonly ILogger<DestinationsController> _logger;
    private readonly InstanceGate _gate;
    private readonly StateStore _state;

    /// <summary>
    /// Initializes a new instance of the <see cref="DestinationsController"/> class.
    /// </summary>
    /// <param name="settings">The settings store.</param>
    /// <param name="connection">The bot connection.</param>
    /// <param name="checker">The chat checker.</param>
    /// <param name="health">The health service.</param>
    /// <param name="tester">The test sender.</param>
    /// <param name="outbox">The Outbox.</param>
    /// <param name="strings">The string table.</param>
    /// <param name="gate">The instance gate; changes wait until this instance owns the data folder.</param>
    /// <param name="state">The state store.</param>
    /// <param name="logger">The logger.</param>
    public DestinationsController(SettingsStore settings, BotConnection connection, DestinationChecker checker, HealthService health, DestinationTester tester, Outbox outbox, StringTable strings, InstanceGate gate, StateStore state, ILogger<DestinationsController> logger)
    {
        _gate = gate;
        _state = state;
        _settings = settings;
        _connection = connection;
        _checker = checker;
        _health = health;
        _tester = tester;
        _outbox = outbox;
        _strings = strings;
        _logger = logger;
    }

    /// <summary>
    /// Lists the destinations with their health.
    /// </summary>
    /// <returns>The destinations.</returns>
    [HttpGet("Destinations")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<DestinationsDto> Get() => List(_settings.Current);

    /// <summary>
    /// Adds a chat by ID or topic link, after checking the bot can post there, and sends the test message.
    /// </summary>
    /// <param name="request">The chat and preset.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Added or AlreadyExists, 400 when the check fails, or 409 with the current data.</returns>
    [HttpPost("Destinations")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AddDestinationResponse>> Add([FromBody] AddDestinationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (ApiGuard.Block(_gate, _state) is { } blocked)
        {
            return blocked;
        }

        if (_connection.Sender is null)
        {
            return Error("Bot", _strings.Get("destination.notConnected"));
        }

        ChatId chat;
        var threadId = 0;
        if (request.ChatId is { } chatId && string.IsNullOrWhiteSpace(request.TopicLink))
        {
            chat = chatId;
        }
        else if (request.ChatId is null && !string.IsNullOrWhiteSpace(request.TopicLink))
        {
            if (!TopicLink.TryParse(request.TopicLink, out var link))
            {
                return Error("TopicLink", _strings.Get("destination.badLink"));
            }

            chat = link.Chat;
            threadId = link.ThreadId;
        }
        else
        {
            return Error("ChatId", "Give a chat ID or a topic link.");
        }

        if (!TryPreset(request.Preset, out var preset))
        {
            return Error("Preset", "Must be Everything, NewMedia, AdminAlerts or Custom.");
        }

        ChatCheckResult check;
        try
        {
            check = await _checker.CheckAsync(chat, threadId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TgJelly could not check a chat to add");
            return Error("ChatId", CheckError(ex));
        }

        if (!check.Passed)
        {
            return Error("ChatId", check.Message ?? string.Empty);
        }

        // A link to a message in a chat without topics looks like a topic link (t.me/c/<chat>/<message>).
        if (threadId > 0 && !check.IsForum)
        {
            return Error("TopicLink", _strings.Get("destination.notForum"));
        }

        // The duplicate rule (spec §6.2): an existing destination for this chat and topic is re-checked, not added.
        if (Find(_settings.Current, check.ChatId, threadId) is { } existing)
        {
            try
            {
                await _health.CheckNowAsync(existing.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "TgJelly could not re-check destination {Destination}", existing.Title);
            }

            return new AddDestinationResponse { Outcome = "AlreadyExists", Destination = ToDto(existing) };
        }

        string? newId = null;
        var result = _settings.UpdateIf(request.ExpectedRevision, c =>
        {
            if (Find(c, check.ChatId, threadId) is not null)
            {
                return false;
            }

            newId = DestinationIds.New(c.Destinations.Select(d => d.Id));
            c.Destinations =
            [
                .. c.Destinations,
                new Destination
                {
                    Id = newId,
                    ChatId = check.ChatId,
                    ThreadId = threadId,
                    Title = check.Title,
                    Kind = check.Kind,
                    Preset = preset,
                    Events = EventCatalog.Expand(preset),
                },
            ];
            return true;
        });

        switch (result.Status)
        {
            case SettingsUpdateStatus.Unchanged:
            case SettingsUpdateStatus.Conflict when Find(result.Configuration, check.ChatId, threadId) is not null:
                // Another request added the same chat between our check and our save.
                return new AddDestinationResponse { Outcome = "AlreadyExists", Destination = ToDto(Find(result.Configuration, check.ChatId, threadId)!) };
            case SettingsUpdateStatus.Conflict:
                return Conflict(new ConflictDto<DestinationsDto> { Current = List(result.Configuration) });
            case SettingsUpdateStatus.Invalid:
                return BadRequest(new ErrorsDto { Errors = result.Errors });
        }

        var added = result.Configuration.Destinations.First(d => d.Id == newId);
        _health.SetChecked(added.Id, check);
        var test = await _tester.SendTestAsync(added, cancellationToken).ConfigureAwait(false);
        return new AddDestinationResponse { Outcome = "Added", Destination = ToDto(added), TestSent = test.Sent, TestError = test.Error };
    }

    /// <summary>
    /// Changes a destination's preset, events, libraries, silent groups or pause state.
    /// </summary>
    /// <param name="id">The destination.</param>
    /// <param name="request">The fields to change.</param>
    /// <returns>The destination, 400, 404 or 409.</returns>
    [HttpPut("Destinations/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<DestinationDto> Update([FromRoute] string id, [FromBody] UpdateDestinationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (ApiGuard.Block(_gate, _state) is { } blocked)
        {
            return blocked;
        }

        PresetKind? preset = null;
        if (request.Preset is not null)
        {
            if (!TryPreset(request.Preset, out var parsed))
            {
                return Error("Preset", "Must be Everything, NewMedia, AdminAlerts or Custom.");
            }

            preset = parsed;
        }

        if (request.Events?.FirstOrDefault(e => !EventCatalog.IsKnown(e)) is { } unknown)
        {
            return Error("Events", "Unknown event: " + unknown);
        }

        if (request.SilentGroups?.FirstOrDefault(g => !EventCatalog.Groups.Contains(g)) is { } unknownGroup)
        {
            return Error("SilentGroups", "Unknown group: " + unknownGroup);
        }

        string[]? libraries = null;
        if (request.LibraryIds is not null)
        {
            var parsed = new List<string>();
            foreach (var library in request.LibraryIds)
            {
                if (!Guid.TryParse(library, out var guid))
                {
                    return Error("LibraryIds", "Not a library ID: " + library);
                }

                parsed.Add(guid.ToString("N"));
            }

            libraries = [.. parsed.Distinct(StringComparer.Ordinal)];
        }

        var result = _settings.UpdateIf(request.ExpectedRevision, c =>
        {
            var d = c.Destinations.FirstOrDefault(x => x.Id == id);
            if (d is null)
            {
                return false;
            }

            if (request.Events is not null)
            {
                d.Preset = PresetKind.Custom;
                d.Events = [.. request.Events.Distinct(StringComparer.Ordinal)];
            }
            else if (preset is { } p)
            {
                d.Preset = p;
                if (p != PresetKind.Custom)
                {
                    d.Events = EventCatalog.Expand(p);
                }
            }

            d.LibraryIds = libraries ?? d.LibraryIds;
            d.SilentGroups = request.SilentGroups is null ? d.SilentGroups : [.. request.SilentGroups.Distinct(StringComparer.Ordinal)];
            d.Enabled = request.Enabled ?? d.Enabled;
            return true;
        });

        return result.Status switch
        {
            SettingsUpdateStatus.Conflict => Conflict(new ConflictDto<DestinationsDto> { Current = List(result.Configuration) }),
            SettingsUpdateStatus.Invalid => BadRequest(new ErrorsDto { Errors = result.Errors }),
            SettingsUpdateStatus.Unchanged => NotFound(),
            _ => ToDto(result.Configuration.Destinations.First(d => d.Id == id)),
        };
    }

    /// <summary>
    /// Removes a destination, optionally making the bot leave the chat.
    /// </summary>
    /// <param name="id">The destination.</param>
    /// <param name="leave">Whether the bot should leave the chat (unless another destination still uses it).</param>
    /// <param name="expectedRevision">The revision the change is based on.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>No content, 404 or 409.</returns>
    [HttpDelete("Destinations/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete([FromRoute] string id, [FromQuery] bool leave, [FromQuery] long expectedRevision, CancellationToken cancellationToken)
    {
        if (ApiGuard.Block(_gate, _state) is { } blocked)
        {
            return blocked;
        }

        Destination? removed = null;
        var result = _settings.UpdateIf(expectedRevision, c =>
        {
            removed = c.Destinations.FirstOrDefault(x => x.Id == id);
            if (removed is null)
            {
                return false;
            }

            c.Destinations = [.. c.Destinations.Where(x => x.Id != id)];
            return true;
        });

        if (result.Status == SettingsUpdateStatus.Conflict)
        {
            return Conflict(new ConflictDto<DestinationsDto> { Current = List(result.Configuration) });
        }

        if (result.Status != SettingsUpdateStatus.Saved || removed is null)
        {
            return NotFound();
        }

        _outbox.RemoveDestination(id);
        _health.Remove(id);
        if (leave && result.Configuration.Destinations.All(d => d.ChatId != removed.ChatId) && _connection.Sender is { } sender)
        {
            try
            {
                await sender.LeaveChatAsync(removed.ChatId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "TgJelly could not leave {Chat}", removed.Title);
            }
        }

        return NoContent();
    }

    /// <summary>
    /// Re-checks a destination and sends the test message, bypassing the hold.
    /// </summary>
    /// <param name="id">The destination.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The result, or 404.</returns>
    [HttpPost("Destinations/{id}/Test")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TestResultDto>> Test([FromRoute] string id, CancellationToken cancellationToken)
    {
        if (ApiGuard.Block(_gate, _state) is { } blocked)
        {
            return blocked;
        }

        if (_settings.Current.Destinations.FirstOrDefault(d => d.Id == id) is not { } destination)
        {
            return NotFound();
        }

        var result = await _tester.SendTestAsync(destination, cancellationToken).ConfigureAwait(false);
        return new TestResultDto { Sent = result.Sent, Error = result.Error, Health = ToDto(result.Health) };
    }

    /// <summary>
    /// Re-checks a destination now.
    /// </summary>
    /// <param name="id">The destination.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The health, or 404.</returns>
    [HttpPost("Destinations/{id}/Check")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CheckResultDto>> Check([FromRoute] string id, CancellationToken cancellationToken)
    {
        if (ApiGuard.Block(_gate, _state) is { } blocked)
        {
            return blocked;
        }

        if (_settings.Current.Destinations.All(d => d.Id != id))
        {
            return NotFound();
        }

        try
        {
            return new CheckResultDto { Health = ToDto(await _health.CheckNowAsync(id, cancellationToken).ConfigureAwait(false)) };
        }
        catch (BotNotConnectedException)
        {
            return new CheckResultDto { Health = ToDto(_health.Get(id)), Error = _strings.Get("destination.notConnected") };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TgJelly could not check destination {Destination}", id);
            return new CheckResultDto { Health = ToDto(_health.Get(id)), Error = _strings.Get("token.unreachable") };
        }
    }

    /// <summary>
    /// Returns the bot's <c>t.me</c> links.
    /// </summary>
    /// <returns>The links.</returns>
    [HttpGet("Links")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<LinksDto> GetLinks()
    {
        var bot = _connection.BotUsername;
        if (bot.Length == 0)
        {
            return new LinksDto();
        }

        var start = "https://t.me/" + bot;
        return new LinksDto
        {
            OpenBot = start + "?start=setup",
            StartAdd = start + "?start=add",
            StartGroup = start + "?startgroup=add",
            StartChannel = start + "?startchannel&admin=post_messages+edit_messages",
        };
    }

    private static bool TryPreset(string? text, out PresetKind preset)
        => Enum.TryParse(text, ignoreCase: true, out preset) && Enum.IsDefined(preset);

    private static Destination? Find(PluginConfiguration c, long chatId, int threadId)
        => c.Destinations.FirstOrDefault(d => d.ChatId == chatId && d.ThreadId == threadId);

    private static HealthDto ToDto(DestinationHealth h)
        => new() { Status = h.Status.ToString(), PhotosBlocked = h.PhotosBlocked, LastError = h.LastError, At = h.At };

    private string CheckError(Exception ex) => ex switch
    {
        BotNotConnectedException => _strings.Get("destination.notConnected"),
        ApiRequestException => _strings.Get("token.failed", ("error", TelegramErrorClassifier.ClassifyDelivery(ex).Description)),
        _ => _strings.Get("token.unreachable"),
    };

    private BadRequestObjectResult Error(string field, string message)
        => BadRequest(new ErrorsDto { Errors = [new SettingsError(field, message)] });

    private DestinationDto ToDto(Destination d) => new()
    {
        Id = d.Id,
        ChatId = d.ChatId,
        ThreadId = d.ThreadId,
        Title = d.Title,
        Kind = d.Kind.ToString(),
        Enabled = d.Enabled,
        Preset = d.Preset.ToString(),
        Events = d.Events,
        LibraryIds = d.LibraryIds,
        SilentGroups = d.SilentGroups,
        Health = ToDto(_health.Get(d.Id)),
    };

    private DestinationsDto List(PluginConfiguration c) => new() { Revision = c.Revision, Items = [.. c.Destinations.Select(ToDto)] };
}
