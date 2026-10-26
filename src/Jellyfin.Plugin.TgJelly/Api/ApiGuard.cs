using Jellyfin.Plugin.TgJelly.Compat;
using Jellyfin.Plugin.TgJelly.State;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// The check every endpoint that changes something makes first: until this instance owns the data
/// folder and has loaded its state, the API serves reads only; an incompatible build never talks to Telegram.
/// </summary>
internal static class ApiGuard
{
    /// <summary>
    /// Returns the response that blocks the request, or <c>null</c> when it may go ahead.
    /// </summary>
    /// <param name="gate">The instance gate.</param>
    /// <param name="state">The state store.</param>
    /// <param name="compat">The compatibility result, for endpoints that talk to Telegram; otherwise <c>null</c>.</param>
    /// <returns>A 409 for an incompatible build, a 503 while waiting for the data folder, or <c>null</c>.</returns>
    public static ObjectResult? Block(InstanceGate gate, StateStore state, CompatResult? compat = null)
    {
        if (compat is { IsCompatible: false })
        {
            return new ConflictObjectResult(new MessageDto { Message = compat.Message ?? string.Empty });
        }

        if (!gate.IsOwner || !state.IsLoaded)
        {
            return new ObjectResult(new MessageDto { Message = gate.WaitingMessage ?? InstanceGate.WaitingForPreviousInstance })
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable,
            };
        }

        return null;
    }
}
