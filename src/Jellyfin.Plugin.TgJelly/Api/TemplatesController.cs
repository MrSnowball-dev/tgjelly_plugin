using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using Jellyfin.Plugin.TgJelly.Configuration;
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
/// Custom message templates: list, save and validate (spec §8.5, §11.2).
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

    /// <summary>
    /// Initializes a new instance of the <see cref="TemplatesController"/> class.
    /// </summary>
    /// <param name="settings">The settings store.</param>
    /// <param name="state">The state store.</param>
    /// <param name="flags">The send-time template flags.</param>
    /// <param name="engine">The template engine.</param>
    /// <param name="strings">The string table, for labels and warnings.</param>
    /// <param name="gate">The instance gate; changes wait until this instance owns the data folder.</param>
    public TemplatesController(SettingsStore settings, StateStore state, TemplateFlags flags, TemplateEngine engine, StringTable strings, InstanceGate gate)
    {
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

        if (!EventCatalog.IsKnown(eventKey))
        {
            return NotFound();
        }

        var text = request.Text ?? string.Empty;
        if (text.Length > MaxText)
        {
            return BadRequest(new ErrorsDto { Errors = [new SettingsError("Text", "Must be at most 16384 characters.")] });
        }

        var changed = false;
        var result = _settings.UpdateIf(request.ExpectedRevision, c =>
        {
            var list = c.Templates.ToList();
            var index = list.FindIndex(t => string.Equals(t.EventKey, eventKey, StringComparison.Ordinal));
            var existing = index >= 0 ? list[index] : null;
            if ((existing?.Enabled ?? false) == request.Enabled && string.Equals(existing?.Text ?? string.Empty, text, StringComparison.Ordinal))
            {
                return false;
            }

            changed = true;
            var remove = !request.Enabled && text.Length == 0;
            if (existing is null)
            {
                if (!remove)
                {
                    list.Add(new TemplateOverride { EventKey = eventKey, Enabled = request.Enabled, Text = text });
                }
            }
            else if (remove)
            {
                list.RemoveAt(index);
            }
            else
            {
                existing.Enabled = request.Enabled;
                existing.Text = text;
            }

            c.Templates = [.. list];
            return true;
        });

        if (result.Status == SettingsUpdateStatus.Saved && changed)
        {
            // Spec §8.5: a flag clears when the template's text or Enabled changes.
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
    /// <returns>The errors, warnings and preview, or 404 for an unknown key.</returns>
    [HttpPost("Templates/Validate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<TemplateValidationDto> Validate([FromBody] TemplateValidateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = request.EventKey ?? string.Empty;
        if (!EventCatalog.IsKnown(key))
        {
            return NotFound();
        }

        var text = request.Text ?? string.Empty;
        var limit = TemplateFieldCatalog.IsMedia(key) ? CaptionLimit : MessageLimit;
        var parse = _engine.Parse(text);
        if (parse.Count > 0)
        {
            return new TemplateValidationDto { Errors = [.. parse.Select(ToDto)], Limit = limit };
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
            // At send time an empty result keeps the default layout and flags the template.
            warnings.Add(_strings.Get("templates.emptyWarning"));
        }

        if (full.Ok)
        {
            errors.AddRange(TelegramHtmlValidator.Validate(preview).Select(p => new TemplateErrorDto { Message = p.Describe(_strings) }));
            if (length > MessageLimit)
            {
                errors.Add(new TemplateErrorDto { Message = _strings.Get("templates.tooLong", ("length", length), ("limit", MessageLimit)) });
            }
            else if (length > limit)
            {
                warnings.Add(_strings.Get("templates.willShorten"));
            }
        }

        return new TemplateValidationDto { Errors = errors, Warnings = warnings, Preview = preview, Length = length, Limit = limit };
    }

    private static bool IsEmpty(string? output) => string.IsNullOrWhiteSpace(HtmlText.ToPlain(output ?? string.Empty));

    private static TemplateErrorDto ToDto(TemplateError e) => new() { Message = e.Message, Line = e.Line, Column = e.Column };

    private TemplatesDto ToDto(PluginConfiguration c) => new()
    {
        Revision = c.Revision,
        Items = [.. EventCatalog.AllKeys.Select(key =>
        {
            var template = c.Templates.FirstOrDefault(t => string.Equals(t.EventKey, key, StringComparison.Ordinal));
            var flag = _flags.Current(key);
            return new TemplateItemDto
            {
                EventKey = key,
                Label = _strings.Get("dest.event." + key),
                Media = TemplateFieldCatalog.IsMedia(key),
                Enabled = template?.Enabled ?? false,
                Text = template?.Text ?? string.Empty,
                Fields = [.. TemplateFieldCatalog.For(key).Select(f => new TemplateFieldDto { Name = f.Name, Type = f.Type, Description = f.Description, Optional = f.Optional })],
                SampleFull = TemplateFieldCatalog.SampleFull(key),
                SampleSparse = TemplateFieldCatalog.SampleSparse(key),
                Flag = flag is null ? null : new TemplateFlagDto { Kind = flag.Kind, Error = flag.Error, At = flag.At },
            };
        })],
    };
}
