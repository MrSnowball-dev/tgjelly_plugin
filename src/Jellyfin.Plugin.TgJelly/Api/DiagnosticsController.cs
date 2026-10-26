using System.Net.Mime;
using Jellyfin.Plugin.TgJelly.Status;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// Serves the diagnostics report.
/// </summary>
[ApiController]
[Route("TgJelly")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public class DiagnosticsController : ControllerBase
{
    private readonly DiagnosticsReport _report;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiagnosticsController"/> class.
    /// </summary>
    /// <param name="report">The report builder.</param>
    public DiagnosticsController(DiagnosticsReport report) => _report = report;

    /// <summary>
    /// Gets the redacted diagnostics report.
    /// </summary>
    /// <returns>The report text.</returns>
    [HttpGet("Diagnostics")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<DiagnosticsDto> Get() => new DiagnosticsDto { Text = _report.Build() };
}
