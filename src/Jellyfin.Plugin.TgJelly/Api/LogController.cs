using System;
using System.Linq;
using System.Net.Mime;
using Jellyfin.Plugin.TgJelly.Log;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// Serves the Log: read-only, filtered and paged.
/// </summary>
[ApiController]
[Route("TgJelly")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public class LogController : ControllerBase
{
    /// <summary>The most rows one request returns.</summary>
    public const int MaxLimit = 500;

    private readonly EventLog _log;

    /// <summary>
    /// Initializes a new instance of the <see cref="LogController"/> class.
    /// </summary>
    /// <param name="log">The event log.</param>
    public LogController(EventLog log) => _log = log;

    /// <summary>
    /// Returns Log rows, newest first.
    /// </summary>
    /// <param name="problemsOnly">Only failed and attention rows, and rows waiting after a failed try.</param>
    /// <param name="destinationId">Only this destination's rows.</param>
    /// <param name="eventKey">Only this event's rows.</param>
    /// <param name="before">The <c>Next</c> value of the previous page, for the next, older page.</param>
    /// <param name="limit">The most rows, 1 to 500.</param>
    /// <returns>The page, or 400 when <paramref name="before"/> isn't a <c>Next</c> value.</returns>
    [HttpGet("Log")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<LogDto> Get(
        [FromQuery] bool problemsOnly = false,
        [FromQuery] string? destinationId = null,
        [FromQuery] string? eventKey = null,
        [FromQuery] string? before = null,
        [FromQuery] int limit = 100)
    {
        if (!string.IsNullOrEmpty(before) && !EventLog.TryParseCursor(before, out _, out _))
        {
            return BadRequest(new MessageDto { Message = "The before parameter must be the Next value of an earlier Log page; leave it out for the newest rows." });
        }

        var page = _log.Query(new LogQuery
        {
            ProblemsOnly = problemsOnly,
            DestinationId = string.IsNullOrEmpty(destinationId) ? null : destinationId,
            EventKey = string.IsNullOrEmpty(eventKey) ? null : eventKey,
            Before = string.IsNullOrEmpty(before) ? null : before,
            Limit = Math.Clamp(limit, 1, MaxLimit),
        });
        return new LogDto
        {
            Items = [.. page.Items.Select(r => new LogEntryDto
            {
                Id = r.Id,
                At = r.At,
                UpdatedAt = r.UpdatedAt,
                EventKey = r.EventKey,
                Title = r.Title,
                DestinationId = r.DestinationId,
                DestinationTitle = r.DestinationTitle,
                Result = r.Result.ToString(),
                Details = r.Details,
                Telegram = r.Telegram,
                Tries = r.Tries,
                RetryAt = r.RetryAt,
                HeldUntil = r.HeldUntil,
                Advice = r.Advice,
            })],
            LastSentAt = page.LastSentAt,
            LastEventAt = page.LastEventAt,
            StartedEmpty = page.StartedEmpty,
            StartedUnreadable = page.StartedUnreadable,
            Next = page.Next,
        };
    }
}
