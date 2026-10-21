using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.Strings;
using Jellyfin.Plugin.TgJelly.Templates;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Telegram.Bot.Extensions;

namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// Message templates: list, save, reset, validate and test-send (spec §8.5, §11.2; design §5, §6).
/// </summary>
[ApiController]
[Route("TgJelly")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public class TemplatesController : ControllerBase
{
    private const int MaxText = 16384;
    private const int CaptionLimit = 1024;
    private const int MessageLimit = 4096;

    private readonly SettingsStore _settings;
    private readonly StateStore _state;
    private readonly TemplateFlags _flags;
    private readonly TemplateEngine _engine;
    private readonly StringTable _strings;
    private readonly InstanceGate _gate;
    private readonly TemplateSample _sample;
    private readonly DestinationTester _tester;
    private readonly HealthService _health;

    /// <summary>
    /// Initializes a new instance of the <see cref="TemplatesController"/> class.
    /// </summary>
    /// <param name="settings">The settings store.</param>
    /// <param name="state">The state store.</param>
    /// <param name="flags">The send-time template flags.</param>
    /// <param name="engine">The template engine.</param>
    /// <param name="strings">The string table, for labels and warnings.</param>
    /// <param name="gate">The instance gate; changes wait until this instance owns the data folder.</param>
    /// <param name="sample">Builds the fitted sample messages.</param>
    /// <param name="tester">Sends the sample to a destination.</param>
    /// <param name="health">The health service, for the result's health.</param>
    public TemplatesController(SettingsStore settings, StateStore state, TemplateFlags flags, TemplateEngine engine, StringTable strings, InstanceGate gate, TemplateSample sample, DestinationTester tester, HealthService health)
    {
        _health = health;
        _sample = sample;
        _tester = tester;
        _settings = settings;
        _state = state;
        _flags = flags;
        _engine = engine;
        _strings = strings;
        _gate = gate;
    }

    /// <summary>
    /// Lists every event's template with its fields and sample models.
    /// </summary>
    /// <returns>The templates.</returns>
    [HttpGet("Templates")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<TemplatesDto> Get() => ToDto(_settings.Current);

    /// <summary>
    /// Saves one event's template.
    /// </summary>
    /// <param name="eventKey">The event key.</param>
    /// <param name="request">The new values and the expected revision.</param>
    /// <returns>The templates, 404 for an unknown key, 400 for text that is too long, or 409 with the current data.</returns>
    [HttpPut("Templates/{eventKey}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<TemplatesDto> Put(string eventKey, [FromBody] TemplateUpdateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (ApiGuard.Block(_gate, _state) is { } blocked)
        {
            return blocked;
        }

        if (!TemplateKeys.All.Contains(eventKey))
        {
            return NotFound();
        }

        var text = request.Text ?? string.Empty;
        if (text.Length > MaxText)
        {
            return BadRequest(new ErrorsDto { Errors = [new SettingsError("Text", "Must be at most 16384 characters.")] });
        }

        // Text that is the built-in one (or nothing) is no override: the built-in template stays in use.
        var builtIn = BuiltInTemplates.Get(eventKey, _settings.Current.Language) ?? string.Empty;
        var reset = string.IsNullOrWhiteSpace(text) || string.Equals(text.Replace("\r\n", "\n", StringComparison.Ordinal), builtIn, StringComparison.Ordinal);
        return Change(eventKey, request.ExpectedRevision, reset ? null : text);
    }

    /// <summary>
    /// Removes one event's override, so its built-in template is used again.
    /// </summary>
    /// <param name="eventKey">The event key.</param>
    /// <param name="expectedRevision">The revision the caller's data is based on.</param>
    /// <returns>The templates (unchanged when there was no override), 404 for an unknown key, or 409 with the current data.</returns>
    [HttpDelete("Templates/{eventKey}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<TemplatesDto> Delete(string eventKey, [FromQuery] long expectedRevision)
    {
        if (ApiGuard.Block(_gate, _state) is { } blocked)
        {
            return blocked;
        }

        if (!TemplateKeys.All.Contains(eventKey))
        {
            return NotFound();
        }

        var result = Change(eventKey, expectedRevision, null);

        // A reset always clears the flag, also when there was no override to remove; a conflict changes nothing.
        if (result.Result is not ConflictObjectResult and not BadRequestObjectResult)
        {
            _flags.Clear(eventKey);
        }

        return result;
    }

    // Sets an override's text, or removes it when the text is null. A flag clears when the template's text changes (spec §8.5).
    private ActionResult<TemplatesDto> Change(string eventKey, long expectedRevision, string? text)
    {
        var changed = false;
        var result = _settings.UpdateIf(expectedRevision, c =>
        {
            var list = c.Templates.ToList();
            var index = list.FindIndex(t => string.Equals(t.EventKey, eventKey, StringComparison.Ordinal));
            var existing = index >= 0 ? list[index] : null;
            if (string.Equals(existing?.Text, text, StringComparison.Ordinal))
            {
                return false;
            }

            changed = true;
            if (text is null)
            {
                list.RemoveAt(index);
            }
            else if (existing is null)
            {
                list.Add(new TemplateOverride { EventKey = eventKey, Enabled = true, Text = text });
            }
            else
            {
                existing.Text = text;
            }

            c.Templates = [.. list];
            return true;
        });

        if (result.Status == SettingsUpdateStatus.Saved && changed)
        {
            _flags.Clear(eventKey);
        }

        return result.Status switch
        {
            SettingsUpdateStatus.Invalid => BadRequest(new ErrorsDto { Errors = result.Errors }),
            SettingsUpdateStatus.Conflict => Conflict(new ConflictDto<TemplatesDto> { Current = ToDto(result.Configuration) }),
            _ => ToDto(result.Configuration),
        };
    }

    /// <summary>
    /// Checks a template without saving it: parse, strict renders with the full and sparse samples, the HTML whitelist and the length.
    /// </summary>
    /// <param name="request">The event key and text.</param>
    /// <returns>The errors, warnings, preview and fitted sample message, or 404 for an unknown key.</returns>
    [HttpPost("Templates/Validate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<TemplateValidationDto> Validate([FromBody] TemplateValidateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = request.EventKey ?? string.Empty;
        if (!TemplateKeys.All.Contains(key))
        {
            return NotFound();
        }

        return Check(key, request.Text ?? string.Empty).Dto;
    }

    /// <summary>
    /// Sends a template's sample message to a destination, without saving the template.
    /// </summary>
    /// <param name="request">The event key, text and destination.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The result, or 404 for an unknown key or destination. A template with errors is not sent.</returns>
    [HttpPost("Templates/Test")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TestResultDto>> Test([FromBody] TemplateTestRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (ApiGuard.Block(_gate, _state) is { } blocked)
        {
            return blocked;
        }

        var key = request.EventKey ?? string.Empty;
        if (!TemplateKeys.All.Contains(key) || _settings.Current.Destinations.FirstOrDefault(d => d.Id == request.DestinationId) is not { } destination)
        {
            return NotFound();
        }

        var (dto, sample) = Check(key, request.Text ?? string.Empty);
        if (dto.Errors.Count > 0 || sample is null || sample.Error is not null)
        {
            var message = dto.Errors.Count > 0 ? dto.Errors[0].Message : sample?.Error?.Message;
            return new TestResultDto { Sent = false, Error = message, Health = DestinationDtoMapper.ToDto(_health.Get(destination.Id)) };
        }

        var result = await _tester.SendSampleAsync(destination, sample, cancellationToken).ConfigureAwait(false);
        return new TestResultDto { Sent = result.Sent, Error = result.Error, Health = DestinationDtoMapper.ToDto(result.Health) };
    }

    private bool IsEmptyProblem(TemplateError error) => error.Message == _strings.Get("templates.empty");

    private static bool IsEmpty(string? output) => string.IsNullOrWhiteSpace(HtmlText.ToPlain(output ?? string.Empty));

    private static TemplateErrorDto ToDto(TemplateError e) => new() { Message = e.Message, Line = e.Line, Column = e.Column };

    // Sample values for the dashboard's JSON: HTML written as-is is plain text there.
    private static object? Plain(object? value) => value switch
    {
        RawHtml raw => raw.Html,
        IReadOnlyDictionary<string, object?> map => map.ToDictionary(p => p.Key, p => Plain(p.Value), StringComparer.Ordinal),
        IEnumerable<object?> list when value is not string => list.Select(Plain).ToList(),
        _ => value,
    };

    private static IReadOnlyDictionary<string, object?> Plain(IReadOnlyDictionary<string, object?> values)
        => values.ToDictionary(p => p.Key, p => Plain(p.Value), StringComparer.Ordinal);

    private (TemplateValidationDto Dto, SampleMessage? Sample) Check(string key, string text)
    {
        var config = _settings.Current;
        var poster = key != TemplateKeys.Digest && TemplateFieldCatalog.IsMedia(key) && config.Message.ShowPoster;
        var limit = poster ? CaptionLimit : MessageLimit;
        var parse = _engine.Parse(text);
        if (parse.Count > 0)
        {
            return (new TemplateValidationDto { Errors = [.. parse.Select(ToDto)], Limit = limit, Caption = poster, Poster = poster }, null);
        }

        var errors = new List<TemplateErrorDto>();
        var seen = new HashSet<TemplateError>();
        var full = _engine.Render(key, text, TemplateFieldCatalog.SampleFull(key), strict: true);
        var sparse = _engine.Render(key, text, TemplateFieldCatalog.SampleSparse(key), strict: true);
        foreach (var error in new[] { full.Error, sparse.Error })
        {
            if (error is not null && seen.Add(error))
            {
                errors.Add(ToDto(error));
            }
        }

        var warnings = new List<string>();
        var preview = full.Output ?? string.Empty;
        var length = HtmlText.PlainLength(preview);
        if ((full.Ok && IsEmpty(full.Output)) || (sparse.Ok && IsEmpty(sparse.Output)))
        {
            // At send time an empty result keeps the built-in template and flags the override.
            warnings.Add(_strings.Get("templates.emptyWarning"));
        }

        if (full.Ok)
        {
            errors.AddRange(TelegramHtmlValidator.Validate(preview).Select(p => new TemplateErrorDto { Message = p.Describe(_strings) }));
            if (length > MessageLimit)
            {
                errors.Add(new TemplateErrorDto { Message = _strings.Get("templates.tooLong", ("length", length), ("limit", MessageLimit)) });
            }
            else if (poster && length > CaptionLimit)
            {
                warnings.Add(_strings.Get("templates.willShorten"));
            }
        }

        if (errors.Count > 0)
        {
            return (new TemplateValidationDto { Errors = errors, Warnings = warnings, Preview = preview, Length = length, Limit = limit, Caption = poster, Poster = poster }, null);
        }

        var sample = _sample.Build(key, text);
        var dto = new TemplateValidationDto
        {
            // An empty result is only a warning (the built-in template takes over at send time); the test send still refuses it.
            Errors = sample.Error is null || (warnings.Count > 0 && IsEmptyProblem(sample.Error)) ? errors : [ToDto(sample.Error)],
            Warnings = warnings,
            Preview = preview,
            Length = length,
            Limit = sample.Limit,
            Html = sample.Html ?? string.Empty,
            Caption = sample.Caption,
            Poster = sample.Poster,
            Buttons = [.. sample.Buttons.Select(row => (IReadOnlyList<TemplateButtonDto>)[.. row.Select(b => new TemplateButtonDto { Text = b.Text, Url = b.Url })])],
        };
        return (dto, sample);
    }

    private TemplatesDto ToDto(PluginConfiguration c) => new()
    {
        Revision = c.Revision,
        Items = [.. TemplateKeys.All.Select(key =>
        {
            var template = c.Templates.FirstOrDefault(t => string.Equals(t.EventKey, key, StringComparison.Ordinal));
            var builtIn = BuiltInTemplates.Get(key, c.Language) ?? string.Empty;
            var flag = _flags.Current(key);
            return new TemplateItemDto
            {
                EventKey = key,
                Label = _strings.Get(key == TemplateKeys.Digest ? "templates.digestLabel" : "dest.event." + key),
                Media = TemplateFieldCatalog.IsMedia(key) && key != TemplateKeys.Digest,
                Text = template?.Text ?? builtIn,
                BuiltIn = builtIn,
                Edited = template is not null,
                Fields = [.. TemplateFieldCatalog.For(key).Select(f => new TemplateFieldDto { Name = f.Name, Type = f.Type, Description = f.Description, Optional = f.Optional })],
                SampleFull = Plain(TemplateFieldCatalog.SampleFull(key)),
                SampleSparse = Plain(TemplateFieldCatalog.SampleSparse(key)),
                Flag = flag is null ? null : new TemplateFlagDto { Kind = flag.Kind, Error = flag.Error, At = flag.At },
            };
        })],
    };
}
