using System;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Compat;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.Strings;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// Bot token and webhook endpoints.
/// </summary>
[ApiController]
[Route("TgJelly")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public class BotController : ControllerBase
{
    private readonly BotTokenService _tokens;
    private readonly StringTable _strings;
    private readonly InstanceGate _gate;
    private readonly StateStore _state;
    private readonly CompatResult _compat;
    private readonly SecretsStore? _secrets;

    /// <summary>
    /// Initializes a new instance of the <see cref="BotController"/> class.
    /// </summary>
    /// <param name="tokens">The token service.</param>
    /// <param name="strings">The string table.</param>
    /// <param name="gate">The instance gate; changes wait until this instance owns the data folder.</param>
    /// <param name="state">The state store.</param>
    /// <param name="compat">The compatibility result; an incompatible build never talks to Telegram.</param>
    /// <param name="secrets">The secrets, for showing the token to an administrator.</param>
    public BotController(BotTokenService tokens, StringTable strings, InstanceGate gate, StateStore state, CompatResult compat, SecretsStore? secrets = null)
    {
        _tokens = tokens;
        _strings = strings;
        _gate = gate;
        _state = state;
        _compat = compat;
        _secrets = secrets;
    }

    /// <summary>
    /// Shows the bot token to an administrator who asked for it (the Bot token card's Show). Never logged.
    /// </summary>
    /// <returns>The token, or 404 when no bot is connected.</returns>
    [HttpGet("Token")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<TokenDto> GetToken()
    {
        var token = _secrets?.Load().BotToken;
        return string.IsNullOrEmpty(token)
            ? NotFound(new MessageDto { Message = _strings.Get("web.token.none") })
            : new TokenDto { Token = token };
    }

    /// <summary>
    /// Checks and saves a bot token. Every outcome is HTTP 200 with an <c>Outcome</c> field.
    /// </summary>
    /// <param name="request">The token and options.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The outcome.</returns>
    [HttpPost("Token")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<TokenResponse>> PostToken([FromBody] TokenRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (ApiGuard.Block(_gate, _state, _compat) is { } blocked)
        {
            return blocked;
        }

        var result = await _tokens.SaveAsync(request.Token, request.ConfirmSwitch, cancellationToken).ConfigureAwait(false);
        return new TokenResponse { Outcome = result.Outcome.ToString(), Message = result.Message, BotUsername = result.BotUsername, WebhookDeleted = result.WebhookDeleted };
    }

    /// <summary>
    /// Disconnects the bot.
    /// </summary>
    /// <param name="removeDestinations">Also removes every destination, in the same settings change.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>No content.</returns>
    [HttpDelete("Token")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteToken([FromQuery] bool removeDestinations, CancellationToken cancellationToken)
    {
        if (ApiGuard.Block(_gate, _state) is { } blocked)
        {
            return blocked;
        }

        await _tokens.DisconnectAsync(removeDestinations, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Deletes another app's webhook and reconnects (the dashboard's [Take over]).
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>No content, 409 without a token, or 400 when Telegram refused.</returns>
    [HttpPost("Webhook/TakeOver")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> TakeOverWebhook(CancellationToken cancellationToken)
    {
        if (ApiGuard.Block(_gate, _state, _compat) is { } blocked)
        {
            return blocked;
        }

        try
        {
            return await _tokens.TakeOverWebhookAsync(cancellationToken).ConfigureAwait(false)
                ? NoContent()
                : Conflict(new MessageDto { Message = _strings.Get("destination.notConnected") });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return BadRequest(new MessageDto { Message = _strings.Get("token.failed", ("error", TelegramErrorClassifier.ClassifyPoll(ex).Description)) });
        }
    }
}
