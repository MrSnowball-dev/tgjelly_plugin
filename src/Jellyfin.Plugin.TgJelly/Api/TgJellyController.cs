using System.Net.Mime;
using Jellyfin.Plugin.TgJelly.Compat;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.State;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// Admin-only REST API for the dashboard page (spec §11.2). Later plans add endpoints.
/// </summary>
[ApiController]
[Route("TgJelly")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public class TgJellyController : ControllerBase
{
    private readonly CompatResult _compat;
    private readonly InstanceGate _gate;
    private readonly SettingsStore _settings;
    private readonly StateStore _state;
    private readonly SecretsStore _secrets;

    /// <summary>
    /// Initializes a new instance of the <see cref="TgJellyController"/> class.
    /// </summary>
    /// <param name="compat">The CompatGuard result.</param>
    /// <param name="gate">The instance gate.</param>
    /// <param name="settings">The settings store.</param>
    /// <param name="state">The state store.</param>
    /// <param name="secrets">The secrets store.</param>
    public TgJellyController(CompatResult compat, InstanceGate gate, SettingsStore settings, StateStore state, SecretsStore secrets)
    {
        _compat = compat;
        _gate = gate;
        _settings = settings;
        _state = state;
        _secrets = secrets;
    }

    /// <summary>
    /// Gets the plugin status shown at the top of the dashboard page.
    /// </summary>
    /// <returns>The status.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<StatusDto> GetStatus()
    {
        return new StatusDto
        {
            Version = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? string.Empty,
            BuildTarget = _compat.BuildLabel,
            Compatible = _compat.IsCompatible,
            CompatMessage = _compat.Message,
            InstanceOwner = _gate.IsOwner,
            InstanceMessage = _gate.WaitingMessage,
            Revision = _settings.Current.Revision,
            BackupRestoredAt = _state.Read(s => s.Status.BackupRestoredAt),
            LoadWarnings = [.. _state.Read(s => s.Status.LoadWarnings.ToArray()), .. _secrets.LoadWarning is { } w ? new[] { w } : []],
            LastErrors = _state.LastErrorsSnapshot(),
        };
    }
}
