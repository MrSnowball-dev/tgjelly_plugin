using System;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.Strings;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// Detected-chat endpoints (spec §6.5, §11.2): set up a chat the bot was added to outside setup, or dismiss it. The
/// list comes with <c>GET Destinations</c>.
/// </summary>
[ApiController]
[Route("TgJelly")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public class DetectedChatsController : ControllerBase
{
    private readonly SettingsStore _settings;
    private readonly BotConnection _connection;
    private readonly DestinationChecker _checker;
    private readonly DestinationCreator _creator;
    private readonly BotMemory _memory;
    private readonly DestinationDtoMapper _mapper;
    private readonly StringTable _strings;
    private readonly InstanceGate _gate;
    private readonly StateStore _state;
    private readonly ILogger<DetectedChatsController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DetectedChatsController"/> class.
    /// </summary>
    /// <param name="settings">The settings store.</param>
    /// <param name="connection">The bot connection.</param>
    /// <param name="checker">The chat checker.</param>
    /// <param name="creator">The destination creator.</param>
    /// <param name="memory">The bot's memory, which holds the detected chats.</param>
    /// <param name="mapper">Builds the destination DTOs.</param>
    /// <param name="strings">The string table.</param>
    /// <param name="gate">The instance gate; changes wait until this instance owns the data folder.</param>
    /// <param name="state">The state store.</param>
    /// <param name="logger">The logger.</param>
    public DetectedChatsController(SettingsStore settings, BotConnection connection, DestinationChecker checker, DestinationCreator creator, BotMemory memory, DestinationDtoMapper mapper, StringTable strings, InstanceGate gate, StateStore state, ILogger<DetectedChatsController> logger)
    {
        _settings = settings;
        _connection = connection;
        _checker = checker;
        _creator = creator;
        _memory = memory;
        _mapper = mapper;
        _strings = strings;
        _gate = gate;
        _state = state;
        _logger = logger;
    }

    /// <summary>
    /// Sets up a detected chat: checks it once, as in the bot (spec §6.2 step 2), then adds it as a destination. It
    /// stops being a detected chat when it is added.
    /// </summary>
    /// <param name="chatId">The detected chat.</param>
    /// <param name="request">The preset and topic.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Added or AlreadyExists, 400 when the check fails, 404 when the chat isn't detected, or 409 with the current data.</returns>
    [HttpPost("DetectedChats/{chatId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AddDestinationResponse>> SetUp([FromRoute] long chatId, [FromBody] SetUpDetectedChatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (ApiGuard.Block(_gate, _state) is { } blocked)
        {
            return blocked;
        }

        if (!_memory.IsDetected(chatId))
        {
            return NotFound();
        }

        if (_connection.Sender is null)
        {
            return Error("Bot", _strings.Get("destination.notConnected"));
        }

        if (!DestinationsController.TryPreset(request.Preset, out var preset))
        {
            return Error("Preset", "Must be Everything, NewMedia, AdminAlerts or Custom.");
        }

        var threadId = request.ThreadId ?? 0;
        if (threadId < 0)
        {
            return Error("ThreadId", "Must be a topic ID, or omitted.");
        }

        ChatCheckResult check;
        try
        {
            check = await _checker.CheckAsync(chatId, threadId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TgJelly could not check a detected chat");
            return Error("ChatId", DestinationsController.CheckError(_strings, ex));
        }

        if (!check.Passed)
        {
            return Error("ChatId", check.Message ?? string.Empty);
        }

        if (threadId > 0 && !check.IsForum)
        {
            return Error("ThreadId", _strings.Get("destination.notForum"));
        }

        var created = await _creator.CreateAsync(check, threadId, EventCatalog.Expand(preset), preset, request.ExpectedRevision, cancellationToken).ConfigureAwait(false);
        switch (created.Outcome)
        {
            case CreateOutcome.Conflict:
                return Conflict(new ConflictDto<DestinationsDto> { Current = _mapper.List(created.Configuration) });
            case CreateOutcome.Invalid:
                return BadRequest(new ErrorsDto { Errors = created.Errors });
            case CreateOutcome.AlreadyExists:
                return new AddDestinationResponse { Outcome = "AlreadyExists", Destination = _mapper.ToDto(created.Destination!, created.Configuration.Revision) };
            default:
                return new AddDestinationResponse { Outcome = "Added", Destination = _mapper.ToDto(created.Destination!, created.Configuration.Revision), TestSent = created.Test!.Sent, TestError = created.Test.Error };
        }
    }

    /// <summary>
    /// Dismisses a detected chat. One that is already gone is fine.
    /// </summary>
    /// <param name="chatId">The detected chat.</param>
    /// <returns>No content.</returns>
    [HttpDelete("DetectedChats/{chatId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult Dismiss([FromRoute] long chatId)
    {
        if (ApiGuard.Block(_gate, _state) is { } blocked)
        {
            return blocked;
        }

        _memory.RemoveDetected(chatId);
        return NoContent();
    }

    private BadRequestObjectResult Error(string field, string message)
        => BadRequest(new ErrorsDto { Errors = [new SettingsError(field, message)] });
}
