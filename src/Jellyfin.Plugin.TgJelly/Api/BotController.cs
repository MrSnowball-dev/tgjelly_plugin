using System;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Strings;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// Bot token and webhook endpoints (spec §6.1, §11.2).
/// </summary>
[ApiController]
[Route("TgJelly")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public class BotController : ControllerBase
{
    private readonly BotTokenService _tokens;
    private readonly StringTable _strings;

    /// <summary>
    /// Initializes a new instance of the <see cref="BotController"/> class.
    /// </summary>
    /// <param name="tokens">The token service.</param>
    /// <param name="strings">The string table.</param>
    public BotController(BotTokenService tokens, StringTable strings)
    {
        _tokens = tokens;
        _strings = strings;
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
        var result = await _tokens.SaveAsync(request.Token, request.SetProfile, request.ConfirmSwitch, cancellationToken).ConfigureAwait(false);
        return new TokenResponse { Outcome = result.Outcome.ToString(), Message = result.Message, BotUsername = result.BotUsername, WebhookDeleted = result.WebhookDeleted };
    }

    /// <summary>
    /// Disconnects the bot (spec §10.4).
    /// </summary>
    /// <returns>No content.</returns>
    [HttpDelete("Token")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult DeleteToken()
    {
        _tokens.Disconnect();
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
