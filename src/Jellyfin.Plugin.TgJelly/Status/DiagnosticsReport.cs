using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Compat;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Delivery;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.Library;
using Jellyfin.Plugin.TgJelly.Logging;
using Jellyfin.Plugin.TgJelly.Pipeline;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.Templates;

namespace Jellyfin.Plugin.TgJelly.Status;

/// <summary>
/// Builds the plain-text diagnostics report admins paste into GitHub issues (spec §12). It never includes the
/// token, proxy credentials, chat IDs, chat titles or the base-URL override's value, and the whole text goes
/// through <see cref="SecretRedactor"/>.
/// </summary>
public sealed class DiagnosticsReport
{
    private readonly CompatResult _compat;
    private readonly InstanceGate _gate;
    private readonly SettingsStore _settings;
    private readonly StateStore _state;
    private readonly SecretsStore _secrets;
    private readonly BotConnection _connection;
    private readonly BotApiEnvironment _environment;
    private readonly Outbox _outbox;
    private readonly MediaReadiness _readiness;
    private readonly HealthService _health;
    private readonly IServerInfo _server;
    private readonly NoticeBuilder _notices;
    private readonly TemplateFlags _templates;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiagnosticsReport"/> class.
    /// </summary>
    /// <param name="compat">The CompatGuard result.</param>
    /// <param name="gate">The instance gate.</param>
    /// <param name="settings">The settings store.</param>
    /// <param name="state">The state store.</param>
    /// <param name="secrets">The secrets store, only to tell whether a token is set.</param>
    /// <param name="connection">The bot connection.</param>
    /// <param name="environment">The Bot API environment override.</param>
    /// <param name="outbox">The Outbox.</param>
    /// <param name="readiness">Media readiness, for the pending count.</param>
    /// <param name="health">The health service.</param>
    /// <param name="server">The Jellyfin server info.</param>
    /// <param name="templates">The template flags.</param>
    /// <param name="notices">The notice builder.</param>
    public DiagnosticsReport(CompatResult compat, InstanceGate gate, SettingsStore settings, StateStore state, SecretsStore secrets, BotConnection connection, BotApiEnvironment environment, Outbox outbox, MediaReadiness readiness, HealthService health, IServerInfo server, TemplateFlags templates, NoticeBuilder notices)
    {
        _templates = templates;
        _compat = compat;
        _gate = gate;
        _settings = settings;
        _state = state;
        _secrets = secrets;
        _connection = connection;
        _environment = environment;
        _outbox = outbox;
        _readiness = readiness;
        _health = health;
        _server = server;
        _notices = notices;
    }

    /// <summary>
    /// Builds the report.
    /// </summary>
    /// <returns>Plain <c>key: value</c> lines in sections, with secrets redacted.</returns>
    public string Build()
    {
        var settings = _settings.Current;
        var sb = new StringBuilder();

        Section(sb, "TgJelly");
        Line(sb, "version", typeof(Plugin).Assembly.GetName().Version?.ToString() ?? string.Empty);
        Line(sb, "build target", _compat.BuildLabel);
        Line(sb, "compatible", YesNo(_compat.IsCompatible));
        Line(sb, "instance owner", YesNo(_gate.IsOwner));

        Section(sb, "Server");
        Line(sb, "Jellyfin version", _server.VersionString);
        Line(sb, "OS", RuntimeInformation.OSDescription);
        Line(sb, ".NET runtime", RuntimeInformation.FrameworkDescription);

        Section(sb, "Bot");
        Line(sb, "connection state", _connection.State.ToString());
        Line(sb, "has token", YesNo(!string.IsNullOrEmpty(_secrets.Load().BotToken)));
        var username = _connection.BotUsername.Length > 0 ? _connection.BotUsername : _state.Read(s => s.Telegram.BotUsername);
        Line(sb, "bot username", string.IsNullOrEmpty(username) ? "-" : "@" + username);
        Line(sb, "base URL override", YesNo(_environment.BaseUrl is not null));

        Section(sb, "Proxy");
        Line(sb, "mode", settings.Proxy.Mode.ToString());
        if (settings.Proxy.Url.Length > 0 && !(Uri.TryCreate(settings.Proxy.Url, UriKind.Absolute, out var parsed) && parsed.Host.Length > 0))
        {
            Line(sb, "proxy", "set (unparsed)");
        }
        else if (Uri.TryCreate(settings.Proxy.Url, UriKind.Absolute, out var proxy))
        {
            Line(sb, "scheme", proxy.Scheme);
            Line(sb, "host", proxy.Host);
            Line(sb, "port", proxy.Port.ToString(CultureInfo.InvariantCulture));
        }

        Section(sb, "Settings");
        Line(sb, "revision", settings.Revision.ToString(CultureInfo.InvariantCulture));
        Line(sb, "public URL set", YesNo(settings.PublicUrl.Length > 0));
        Line(sb, "public URL valid", YesNo(PublicUrlRules.IsValid(settings.PublicUrl)));
        var m = settings.Message;
        Line(sb, "message toggles", $"poster {YesNo(m.ShowPoster)}, overview {YesNo(m.ShowOverview)}, rating/genres {YesNo(m.ShowRatingGenres)}, open in Jellyfin {YesNo(m.ShowOpenInJellyfin)}, provider links {YesNo(m.ShowProviderLinks)}, announce upgrades {YesNo(m.AnnounceUpgrades)}");
        var b = settings.Batching;
        Line(sb, "batching", $"quiet window {b.QuietWindowSeconds}s, max hold {b.MaxHoldMinutes}min, release quiet {b.ReleaseQuietSeconds}s, digest threshold {b.DigestThreshold}, late edit {b.LateEditHours}h, ready timeout {b.ReadyTimeoutMinutes}min");
        Line(sb, "update check", settings.UpdateCheck.Enabled ? settings.UpdateCheck.Mode.ToString() : "off");

        Section(sb, "Destinations");
        foreach (var d in settings.Destinations)
        {
            var health = _health.Get(d.Id);
            sb.Append(CultureInfo.InvariantCulture, $"{d.Id}: kind {d.Kind}, preset {d.Preset}, enabled: {YesNo(d.Enabled)}, health: {health.Status}, photos blocked: {YesNo(health.PhotosBlocked)}, topic set: {YesNo(d.ThreadId != 0)}, events: {d.Events.Length}, libraries: {d.LibraryIds.Length}\n");
        }

        Section(sb, "Templates");
        // Every override is the template in use (design §5.1); the settings migration has already dropped empty ones.
        foreach (var template in settings.Templates.OrderBy(t => t.EventKey, StringComparer.Ordinal))
        {
            Line(sb, template.EventKey, _templates.Current(template.EventKey)?.Kind ?? "ok");
        }

        Section(sb, "Counters");
        Line(sb, "outbox", _outbox.Count.ToString(CultureInfo.InvariantCulture));
        Line(sb, "pending media", (_state.IsLoaded ? _readiness.PendingCount : 0).ToString(CultureInfo.InvariantCulture));
        Line(sb, "notices", _notices.Visible().Count.ToString(CultureInfo.InvariantCulture));

        Section(sb, "Last errors");
        var errors = _state.LastErrorsSnapshot();
        var scrub = ScrubberFor(settings.Destinations);
        foreach (var e in errors)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{e.At.UtcDateTime:yyyy-MM-dd HH:mm:ss}Z [{e.Source}] {scrub(e.Message)}\n");
        }

        if (errors.Count == 0)
        {
            sb.Append("none\n");
        }

        Section(sb, "Load warnings");
        var warnings = _state.Read(s => s.Status.LoadWarnings.ToArray()).Concat(_secrets.LoadWarning is { } w ? new[] { w } : []).ToArray();
        foreach (var warning in warnings)
        {
            sb.Append(scrub(warning)).Append('\n');
        }

        if (warnings.Length == 0)
        {
            sb.Append("none\n");
        }

        return SecretRedactor.Redact(sb.ToString());
    }

    /// <summary>Replaces destination titles with <c>&lt;dest:ID&gt;</c> and chat IDs with <c>&lt;chat&gt;</c>.</summary>
    private static Func<string, string> ScrubberFor(Destination[] destinations)
    {
        var titles = destinations.Where(d => d.Title.Length > 0).OrderByDescending(d => d.Title.Length).ToArray();
        var chats = destinations.Select(d => d.ChatId.ToString(CultureInfo.InvariantCulture)).Distinct(StringComparer.Ordinal).OrderByDescending(c => c.Length).ToArray();
        return text =>
        {
            foreach (var d in titles)
            {
                text = text.Replace(d.Title, "<dest:" + d.Id + ">", StringComparison.Ordinal);
            }

            foreach (var chat in chats)
            {
                text = text.Replace(chat, "<chat>", StringComparison.Ordinal);
                if (chat.StartsWith('-'))
                {
                    text = text.Replace(chat[1..], "<chat>", StringComparison.Ordinal);
                }
            }

            return text;
        };
    }

    private static string YesNo(bool value) => value ? "yes" : "no";

    private static void Section(StringBuilder sb, string name)
    {
        if (sb.Length > 0)
        {
            sb.Append('\n');
        }

        sb.Append('[').Append(name).Append("]\n");
    }

    private static void Line(StringBuilder sb, string key, string value) => sb.Append(key).Append(": ").Append(value).Append('\n');
}
