using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Compat;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Hosting;
using Jellyfin.Plugin.TgJelly.Intake;
using Jellyfin.Plugin.TgJelly.Library;
using Jellyfin.Plugin.TgJelly.Logging;
using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.Updates;

/// <summary>
/// Announces new Jellyfin releases (spec §7.6). It only reads GitHub while some enabled destination subscribes to
/// <c>server.update_available</c>: first 5 minutes after the subscription appears (or after startup), then every
/// 12 hours. Failures are counted for Status and never sent to Telegram.
/// </summary>
public sealed class UpdateChecker : BackgroundService
{
    /// <summary>How long after a subscription appears the first check runs.</summary>
    public static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(5);

    /// <summary>The time between checks.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(12);

    private const string EventKey = "server.update_available";
    private static readonly TimeSpan Poll = TimeSpan.FromMinutes(1);

    private readonly InstanceGate _gate;
    private readonly CompatResult _compat;
    private readonly IServerStartup _startup;
    private readonly SettingsStore _settings;
    private readonly StateStore _state;
    private readonly IServerInfo _server;
    private readonly IReleaseFeed _feed;
    private readonly IntakeQueues _queues;
    private readonly Formatter _formatter;
    private readonly TimeProvider _time;
    private readonly ILogger<UpdateChecker> _logger;
    private DateTimeOffset? _next;

    /// <summary>
    /// Initializes a new instance of the <see cref="UpdateChecker"/> class.
    /// </summary>
    /// <param name="gate">The instance gate.</param>
    /// <param name="compat">The compatibility result; an incompatible build never checks.</param>
    /// <param name="startup">Whether Jellyfin finished starting.</param>
    /// <param name="settings">The settings (update check options and destinations).</param>
    /// <param name="state">The state store.</param>
    /// <param name="server">The running server's version.</param>
    /// <param name="feed">The release feed.</param>
    /// <param name="queues">The intake queues the announcement is published to.</param>
    /// <param name="formatter">The formatter (the running version).</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public UpdateChecker(
        InstanceGate gate,
        CompatResult compat,
        IServerStartup startup,
        SettingsStore settings,
        StateStore state,
        IServerInfo server,
        IReleaseFeed feed,
        IntakeQueues queues,
        Formatter formatter,
        TimeProvider time,
        ILogger<UpdateChecker> logger)
    {
        _gate = gate;
        _compat = compat;
        _startup = startup;
        _settings = settings;
        _state = state;
        _server = server;
        _feed = feed;
        _queues = queues;
        _formatter = formatter;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Re-evaluates the subscription and runs a check when one is due (the loop calls this every minute).
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task that completes when the step is done.</returns>
    internal async Task StepAsync(CancellationToken ct)
    {
        if (!_state.IsLoaded || !_startup.HasCompleted)
        {
            return;
        }

        var settings = _settings.Current;
        var subscribed = settings.UpdateCheck.Enabled
            && settings.Destinations.Any(d => d.Enabled && d.Events.Contains(EventKey, StringComparer.Ordinal));
        if (!subscribed)
        {
            _next = null;
            return;
        }

        var now = _time.GetUtcNow();
        if (_next is null)
        {
            var first = now + FirstDelay;
            var last = _state.Read(s => s.UpdateCheck.LastCheckedAt);
            _next = last is { } at && at + Interval > first ? at + Interval : first;
        }

        if (now >= _next)
        {
            await CheckOnceAsync(ct).ConfigureAwait(false);
            _next = now + Interval;
        }
    }

    /// <summary>
    /// Reads the releases and announces the newest one the mode allows, once.
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns><c>true</c> when the releases could be read.</returns>
    internal async Task<bool> CheckOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<GitHubRelease> releases;
        try
        {
            releases = await _feed.GetReleasesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            var error = SecretRedactor.Redact(ex.Message);
            _state.Update(s =>
            {
                s.UpdateCheck.ConsecutiveFailures++;
                s.UpdateCheck.LastError = error;
            });
            _logger.LogWarning("The Jellyfin update check failed: {Error}", error);
            return false;
        }

        var current = ReleaseVersion.Normalize(_server.Version);
        var kinds = _settings.Current.UpdateCheck;
        GitHubRelease? newest = null;
        Version? newestVersion = null;
        foreach (var release in releases)
        {
            if (!release.Draft && !release.Prerelease
                && ReleaseVersion.TryParse(release.TagName, out var version)
                && version > current
                && Qualifies(kinds, current, version)
                && (newestVersion is null || version > newestVersion))
            {
                newest = release;
                newestVersion = version;
            }
        }

        var notified = _state.Read(s => s.UpdateCheck.LastNotifiedVersion);
        var announce = newest is not null
            && (!ReleaseVersion.TryParse(notified, out var last) || newestVersion! > last);
        if (announce)
        {
            var tag = newest!.TagName.Trim();
            _queues.Publish(EventKey, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [EventFields.Version] = tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag,
                [EventFields.Current] = _formatter.Version(_server.Version),
                [EventFields.Url] = newest.HtmlUrl,
            });
        }

        var now = _time.GetUtcNow();
        _state.Update(s =>
        {
            s.UpdateCheck.LastCheckedAt = now;
            s.UpdateCheck.ConsecutiveFailures = 0;
            s.UpdateCheck.LastError = null;
            if (announce)
            {
                s.UpdateCheck.LastNotifiedVersion = newest!.TagName;
            }
        });
        return true;
    }

    /// <summary>
    /// Returns whether a newer release is of a kind the settings announce. With the running version a.b.c and the
    /// candidate x.y.z: a fix is the same a.b and a higher z, a new release the same a and a higher b, a new major a
    /// higher x.
    /// </summary>
    /// <param name="kinds">The enabled kinds.</param>
    /// <param name="current">The running version.</param>
    /// <param name="candidate">The candidate, already known to be newer.</param>
    /// <returns><c>true</c> when any enabled kind matches.</returns>
    internal static bool Qualifies(UpdateCheckOptions kinds, Version current, Version candidate)
        => (kinds.NewMajor && candidate.Major > current.Major)
            || (kinds.NewReleases && candidate.Major == current.Major && candidate.Minor > current.Minor)
            || (kinds.Fixes && candidate.Major == current.Major && candidate.Minor == current.Minor && candidate.Build > current.Build);

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Supervisor.RunAsync("updates", RunAsync, _time, _logger, stoppingToken);

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        await _gate.WaitForOwnershipAsync(stoppingToken).ConfigureAwait(false);
        if (!_compat.IsCompatible)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await StepAsync(stoppingToken).ConfigureAwait(false);
            await Task.Delay(Poll, _time, stoppingToken).ConfigureAwait(false);
        }
    }
}
