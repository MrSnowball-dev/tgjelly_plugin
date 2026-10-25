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
using Jellyfin.Plugin.TgJelly.Pipeline;
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
    private readonly DestinationDtoMapper _mapper;
    private readonly DestinationCreator _creator;

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
    /// <param name="mapper">Builds the destination DTOs.</param>
    /// <param name="creator">The destination creator.</param>
    public DestinationsController(SettingsStore settings, BotConnection connection, DestinationChecker checker, HealthService health, DestinationTester tester, Outbox outbox, StringTable strings, InstanceGate gate, StateStore state, ILogger<DestinationsController> logger, DestinationDtoMapper mapper, DestinationCreator creator)
    {
        _creator = creator;
        _mapper = mapper;
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
    /// <param name="request">The chat.</param>
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

        ChatCheckResult check;
        try
        {
            check = await _checker.CheckAsync(chat, threadId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TgJelly could not check a chat to add");
            return Error("ChatId", CheckError(_strings, ex));
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

        // A new destination starts with every event on; the admin trims it afterwards.
        var created = await _creator.CreateAsync(check, threadId, [.. EventCatalog.AllKeys], request.ExpectedRevision, cancellationToken).ConfigureAwait(false);
        switch (created.Outcome)
        {
            case CreateOutcome.Conflict:
                return Conflict(new ConflictDto<DestinationsDto> { Current = List(created.Configuration) });
            case CreateOutcome.Invalid:
                return BadRequest(new ErrorsDto { Errors = created.Errors });
            case CreateOutcome.AlreadyExists:
                return new AddDestinationResponse { Outcome = "AlreadyExists", Destination = ToDto(created.Destination!, created.Configuration.Revision) };
            default:
                return new AddDestinationResponse { Outcome = "Added", Destination = ToDto(created.Destination!, created.Configuration.Revision), TestSent = created.Test!.Sent, TestError = created.Test.Error };
        }
    }

    /// <summary>
    /// Changes a destination's events, libraries, silent groups or pause state.
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

        if (request.Events?.FirstOrDefault(e => !EventCatalog.IsKnown(e)) is { } unknown)
        {
            return Error("Events", $"Unknown event: {unknown}. Reload the page and pick the events again; it may have been loaded from another version.");
        }

        if (request.SilentGroups?.FirstOrDefault(g => !EventCatalog.Groups.Contains(g)) is { } unknownGroup)
        {
            return Error("SilentGroups", $"Unknown group: {unknownGroup}. Reload the page and pick the groups again.");
        }

        string[]? libraries = null;
        if (request.LibraryIds is not null)
        {
            var parsed = new List<string>();
            foreach (var library in request.LibraryIds)
            {
                if (!Guid.TryParse(library, out var guid))
                {
                    return Error("LibraryIds", $"Not a library ID: {library}. Reload the page and pick the libraries again.");
                }

                parsed.Add(guid.ToString("N"));
            }

            libraries = [.. parsed.Distinct(StringComparer.Ordinal)];
        }

        if (request.TopicRoutes?.FirstOrDefault(r => !TopicSlots.All.Contains(r.Slot, StringComparer.Ordinal)) is { } badSlot)
        {
            return Error("TopicRoutes", $"Unknown topic slot: {badSlot.Slot}. Reload the page and pick the topic again.");
        }

        if (request.TopicRoutes?.FirstOrDefault(r => r.ThreadId is < 0) is not null)
        {
            return Error("TopicRoutes", "A topic ID can't be negative. Reload the page and pick the topic again.");
        }

        var result = _settings.UpdateIf(request.ExpectedRevision, c =>
        {
            var d = c.Destinations.FirstOrDefault(x => x.Id == id);
            if (d is null)
            {
                return false;
            }

            d.SeparateMediaTopics = request.SeparateMediaTopics ?? d.SeparateMediaTopics;
            if (request.TopicRoutes is { } routes)
            {
                d.TopicRoutes = WithRoutes(d.TopicRoutes, routes.Select(r => (r.Slot, r.ThreadId)));
            }

            if (request.Events is not null)
            {
                d.Events = [.. request.Events.Distinct(StringComparer.Ordinal)];
                d.FollowsNewEvents = EventCatalog.HasAll(d.Events);
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
            _ => ToDto(result.Configuration.Destinations.First(d => d.Id == id), result.Configuration.Revision),
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

        return new LinksDto
        {
            OpenBot = BotLinks.OpenBot(bot),
            StartAdd = BotLinks.StartAdd(bot),
            StartGroup = BotLinks.StartGroup(bot),
            StartChannel = BotLinks.StartChannel(bot),
        };
    }

    /// <summary>
    /// Explains why a check could not run: not connected, rejected by Telegram, or unreachable.
    /// </summary>
    /// <param name="strings">The string table.</param>
    /// <param name="ex">What went wrong.</param>
    /// <returns>The message.</returns>
    internal static string CheckError(StringTable strings, Exception ex) => ex switch
    {
        BotNotConnectedException => strings.Get("destination.notConnected"),
        ApiRequestException => strings.Get("token.failed", ("error", TelegramErrorClassifier.ClassifyDelivery(ex).Description)),
        _ => strings.Get("token.unreachable"),
    };

    /// <summary>
    /// Gives a slot of a forum destination its own topic: TgJelly creates one named after the slot (the bot needs
    /// Manage topics), or an existing topic of the same chat is used by its link. The topic is remembered for the chat.
    /// </summary>
    /// <param name="id">The destination.</param>
    /// <param name="request">The slot, and Create or a link.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The destination with the new route, 400 with what to do, 404 or 409.</returns>
    [HttpPost("Destinations/{id}/Topics")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DestinationDto>> AddTopic([FromRoute] string id, [FromBody] TopicRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (ApiGuard.Block(_gate, _state) is { } blocked)
        {
            return blocked;
        }

        if (_settings.Current.Destinations.FirstOrDefault(d => d.Id == id) is not { } destination)
        {
            return NotFound();
        }

        if (!TopicSlots.All.Contains(request.Slot, StringComparer.Ordinal))
        {
            return Error("Slot", $"Unknown topic slot: {request.Slot}. Reload the page and pick the topic again.");
        }

        if (destination.KindKey != "dest.kind.Forum")
        {
            return Error("Slot", _strings.Get("web.topics.notForum"));
        }

        if (_connection.Sender is not { } sender)
        {
            return Error("Bot", _strings.Get("destination.notConnected"));
        }

        int thread;
        var name = string.Empty;
        try
        {
            if (request.Create)
            {
                name = _strings.Get(request.Slot.Contains('.', StringComparison.Ordinal) ? "web.event." + request.Slot : "web.event.group." + request.Slot);
                thread = await sender.CreateForumTopicAsync(destination.ChatId, name, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (!TopicLink.TryParse(request.Link, out var link) || link.ThreadId <= 0)
                {
                    return Error("Link", _strings.Get("web.topics.badLink"));
                }

                var check = await _checker.CheckAsync(link.Chat, link.ThreadId, cancellationToken).ConfigureAwait(false);
                // A chat that couldn't be read at all (ChatId 0) says why, rather than "another chat".
                if (!check.Passed && check.ChatId == 0)
                {
                    return Error("Link", check.Message ?? _strings.Get("web.topics.badLink"));
                }

                if (check.ChatId != destination.ChatId)
                {
                    return Error("Link", _strings.Get("web.topics.otherChat"));
                }

                if (!check.Passed)
                {
                    return Error("Link", check.Message ?? _strings.Get("web.topics.badLink"));
                }

                thread = link.ThreadId;
            }
        }
        catch (BotNotConnectedException)
        {
            return Error("Bot", _strings.Get("destination.notConnected"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var error = TelegramErrorClassifier.ClassifyDelivery(ex);
            _logger.LogWarning("TgJelly could not set up a topic for {Destination}: {Error}", destination.Title, error.Description);
            return Error(request.Create ? "Create" : "Link", _strings.Get(request.Create ? "web.topics.createFailed" : "web.topics.linkFailed", ("error", error.Description)));
        }

        // The topic exists in Telegram now, so it is recorded on the current settings whatever the page's revision: a
        // conflict here would leave a topic nobody can pick, and a retry would create another.
        var result = _settings.UpdateIf(null, c =>
        {
            var d = c.Destinations.FirstOrDefault(x => x.Id == id);
            if (d is null)
            {
                return false;
            }

            if (Array.Find(d.KnownTopics, t => t.ThreadId == thread) is { } known)
            {
                known.Name = name.Length > 0 ? name : known.Name;
            }
            else
            {
                d.KnownTopics = [.. d.KnownTopics, new KnownTopic { ThreadId = thread, Name = name }];
            }

            d.TopicRoutes = WithRoutes(d.TopicRoutes, [(request.Slot, (int?)thread)]);
            if (request.Slot == "media")
            {
                // One topic for all media ends separate topics, as choosing a known topic does.
                d.SeparateMediaTopics = false;
            }

            return true;
        });

        return result.Status switch
        {
            SettingsUpdateStatus.Conflict => Conflict(new ConflictDto<DestinationsDto> { Current = List(result.Configuration) }),
            SettingsUpdateStatus.Invalid => BadRequest(new ErrorsDto { Errors = result.Errors }),
            SettingsUpdateStatus.Unchanged => NotFound(),
            _ => ToDto(result.Configuration.Destinations.First(d => d.Id == id), result.Configuration.Revision),
        };
    }

    /// <summary>The routes with the changes applied: a thread id sets a slot's route, <c>null</c> removes it.</summary>
    private static TopicRoute[] WithRoutes(TopicRoute[] current, IEnumerable<(string Slot, int? ThreadId)> changes)
    {
        var routes = current.ToDictionary(r => r.Slot, r => r.ThreadId, StringComparer.Ordinal);
        foreach (var (slot, thread) in changes)
        {
            if (thread is { } t)
            {
                routes[slot] = t;
            }
            else
            {
                routes.Remove(slot);
            }
        }

        return [.. TopicSlots.All.Where(routes.ContainsKey).Select(slot => new TopicRoute { Slot = slot, ThreadId = routes[slot] })];
    }

    private BadRequestObjectResult Error(string field, string message)
        => BadRequest(new ErrorsDto { Errors = [new SettingsError(field, message)] });

    private DestinationDto ToDto(Destination d, long revision) => _mapper.ToDto(d, revision);

    private DestinationsDto List(PluginConfiguration c) => _mapper.List(c);

    private static HealthDto ToDto(DestinationHealth h) => DestinationDtoMapper.ToDto(h);
}
