using System.Collections.Generic;
using System.Net.Mime;
using Jellyfin.Plugin.TgJelly.Strings;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// Serves the dashboard page's text (spec §11.1), so the page holds no strings of its own.
/// </summary>
[ApiController]
[Route("TgJelly")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public class StringsController : ControllerBase
{
    /// <summary>
    /// Gets the string table of the best-matching embedded language, with English filling missing keys.
    /// </summary>
    /// <param name="lang">The page's language, e.g. <c>pt-BR</c>.</param>
    /// <returns>Every key and its text; the keys are the table's own.</returns>
    [HttpGet("Strings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<Dictionary<string, string>> Get([FromQuery] string? lang)
        => new Dictionary<string, string>(StringTable.LoadEmbedded(StringTable.BestMatch(lang)).Snapshot());
}
