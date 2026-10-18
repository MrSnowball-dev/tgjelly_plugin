using System;
using System.Net.Mime;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.State;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// Settings and proxy endpoints (spec §11.2).
/// </summary>
[ApiController]
[Route("TgJelly")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public class SettingsController : ControllerBase
{
    private readonly SettingsStore _settings;
    private readonly SecretsStore _secrets;
    private readonly StateStore _state;
    private readonly ISupervisorControl _supervisor;

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsController"/> class.
    /// </summary>
    /// <param name="settings">The settings store.</param>
    /// <param name="secrets">The secrets store, for proxy credentials.</param>
    /// <param name="state">The state store, for the public URL flag.</param>
    /// <param name="supervisor">Restarts the bot session after a proxy change.</param>
    public SettingsController(SettingsStore settings, SecretsStore secrets, StateStore state, ISupervisorControl supervisor)
    {
        _settings = settings;
        _secrets = secrets;
        _state = state;
        _supervisor = supervisor;
    }

    /// <summary>
    /// Returns the dashboard-editable settings.
    /// </summary>
    /// <returns>The settings.</returns>
    [HttpGet("Settings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<SettingsDto> GetSettings() => ToDto(_settings.Current);

    /// <summary>
    /// Saves the public URL, message toggles, batching and update-check settings.
    /// </summary>
    /// <param name="request">The new values and the expected revision.</param>
    /// <returns>The saved settings, 400 with per-field errors, or 409 with the current data.</returns>
    [HttpPut("Settings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<SettingsDto> PutSettings([FromBody] SettingsUpdateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var publicUrl = (request.PublicUrl ?? string.Empty).Trim();
        var before = _settings.Current.PublicUrl;
        var result = _settings.Update(request.ExpectedRevision, c =>
        {
            c.PublicUrl = publicUrl;
            c.Message = request.Message;
            c.Batching = request.Batching;
            c.UpdateCheck = request.UpdateCheck;
        });

        if (result.Status == SettingsUpdateStatus.Saved && !string.Equals(before, publicUrl, StringComparison.Ordinal))
        {
            // Spec §9.4 row 7: the flag clears when PublicUrl changes.
            _state.Update(s => s.Status.PublicUrlFlag = null);
        }

        return Map(result);
    }

    /// <summary>
    /// Saves the proxy settings; credentials go to secrets.json.
    /// </summary>
    /// <param name="request">The proxy settings and the expected revision.</param>
    /// <returns>The saved settings, 400 with per-field errors, or 409 with the current data.</returns>
    [HttpPut("Proxy")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<SettingsDto> PutProxy([FromBody] ProxyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.TryParse<ProxyMode>(request.Mode, ignoreCase: true, out var mode) || !Enum.IsDefined(mode))
        {
            return BadRequest(new ErrorsDto { Errors = [new SettingsError("Proxy.Mode", "Must be System, None, Http or Socks5.")] });
        }

        var url = (request.Url ?? string.Empty).Trim();
        if (mode is ProxyMode.Http or ProxyMode.Socks5 && !IsProxyUrl(mode, url))
        {
            return BadRequest(new ErrorsDto { Errors = [new SettingsError("Proxy.Url", "Must be a proxy address such as host:port.")] });
        }

        var result = _settings.UpdateIf(request.ExpectedRevision, c =>
        {
            if (c.Proxy.Mode == mode && string.Equals(c.Proxy.Url, url, StringComparison.Ordinal))
            {
                return false;
            }

            c.Proxy.Mode = mode;
            c.Proxy.Url = url;
            return true;
        });
        if (result.Status is SettingsUpdateStatus.Conflict or SettingsUpdateStatus.Invalid)
        {
            return Map(result);
        }

        var secrets = _secrets.Load();
        var updated = secrets with
        {
            ProxyUsername = request.Username ?? secrets.ProxyUsername,
            ProxyPassword = request.Password ?? secrets.ProxyPassword,
        };
        if (updated != secrets)
        {
            _secrets.Save(updated);
        }

        if (result.Status == SettingsUpdateStatus.Saved || updated != secrets)
        {
            _supervisor.Restart();
        }

        return ToDto(_settings.Current);
    }

    private static bool IsProxyUrl(ProxyMode mode, string url)
    {
        if (url.Length == 0)
        {
            return false;
        }

        var withScheme = url.Contains("://", StringComparison.Ordinal) ? url : (mode == ProxyMode.Http ? "http://" : "socks5://") + url;
        return Uri.TryCreate(withScheme, UriKind.Absolute, out var uri) && uri.Host.Length > 0;
    }

    private SettingsDto ToDto(PluginConfiguration c)
    {
        var secrets = _secrets.Load();
        return new SettingsDto
        {
            Revision = c.Revision,
            PublicUrl = c.PublicUrl,
            PublicUrlValid = PublicUrlRules.IsValid(c.PublicUrl),
            Message = c.Message,
            Batching = c.Batching,
            UpdateCheck = c.UpdateCheck,
            ProxyMode = c.Proxy.Mode.ToString(),
            ProxyUrl = c.Proxy.Url,
            HasProxyUsername = secrets.ProxyUsername.Length > 0,
            HasProxyPassword = secrets.ProxyPassword.Length > 0,
        };
    }

    private ActionResult<SettingsDto> Map(SettingsUpdateResult result) => result.Status switch
    {
        SettingsUpdateStatus.Invalid => BadRequest(new ErrorsDto { Errors = result.Errors }),
        SettingsUpdateStatus.Conflict => Conflict(new ConflictDto<SettingsDto> { Current = ToDto(result.Configuration) }),
        _ => ToDto(result.Configuration),
    };
}
