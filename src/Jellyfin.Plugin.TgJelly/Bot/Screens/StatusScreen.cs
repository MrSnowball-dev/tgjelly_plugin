using System;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.TgJelly.Compat;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Delivery;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.Pipeline;
using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Strings;
using Telegram.Bot.Types.ReplyMarkups;

namespace Jellyfin.Plugin.TgJelly.Bot.Screens;

/// <summary>
/// The status screen: the same facts as the dashboard's status, in short form.
/// </summary>
public sealed class StatusScreen
{
    private const int ErrorsShown = 3;
    private const int MaxChars = 4096;
    private const int MoreReserve = 120;
    private const int MaxErrorChars = 150;

    private readonly BotConnection _connection;
    private readonly CompatResult _compat;
    private readonly InstanceGate _gate;
    private readonly SettingsStore _settings;
    private readonly StateStore _state;
    private readonly HealthService _health;
    private readonly MediaReadiness _readiness;
    private readonly Outbox _outbox;
    private readonly StringTable _strings;
    private readonly Formatter _formatter;
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of the <see cref="StatusScreen"/> class.
    /// </summary>
    /// <param name="connection">The bot connection.</param>
    /// <param name="compat">The CompatGuard result.</param>
    /// <param name="gate">The instance gate.</param>
    /// <param name="settings">The settings store.</param>
    /// <param name="state">The state store.</param>
    /// <param name="health">The health service.</param>
    /// <param name="readiness">Media readiness, for the pending count.</param>
    /// <param name="outbox">The Outbox.</param>
    /// <param name="strings">The string table.</param>
    /// <param name="formatter">The formatter.</param>
    /// <param name="time">The clock.</param>
    public StatusScreen(BotConnection connection, CompatResult compat, InstanceGate gate, SettingsStore settings, StateStore state, HealthService health, MediaReadiness readiness, Outbox outbox, StringTable strings, Formatter formatter, TimeProvider time)
    {
        _connection = connection;
        _compat = compat;
        _gate = gate;
        _settings = settings;
        _state = state;
        _health = health;
        _readiness = readiness;
        _outbox = outbox;
        _strings = strings;
        _formatter = formatter;
        _time = time;
    }

    /// <summary>
    /// Builds the screen.
    /// </summary>
    /// <returns>The screen.</returns>
    public Screen Build()
    {
        var html = new StringBuilder("<b>").Append(Icons.Heading(_strings, _formatter, "status.title")).Append("</b>\n\n");
        html.Append(_formatter.Escape(_strings.Get("status.connection", ("state", ConnectionText())))).Append('\n');
        html.Append(_formatter.Escape(_strings.Get("status.compat", ("message", _compat.IsCompatible ? _strings.Get("status.compat.ok") : _compat.Message)))).Append("\n\n");

        // The tail is built first so the destination list can use what is left of Telegram's message limit.
        var tail = new StringBuilder();
        tail.Append(_strings.Get("status.pending", ("count", _formatter.Number(_state.IsLoaded ? _readiness.PendingCount : 0)))).Append('\n');
        tail.Append(_strings.Get("status.outbox", ("count", _formatter.Number(_outbox.Count)))).Append("\n\n");
        tail.Append("<b>").Append(_strings.Get("status.errors")).Append("</b>\n");
        var errors = _state.LastErrorsSnapshot();
        if (errors.Count == 0)
        {
            tail.Append(_strings.Get("status.noErrors"));
        }

        var now = _time.GetUtcNow();
        foreach (var error in errors.TakeLast(ErrorsShown).Reverse())
        {
            tail.Append(_formatter.Time(error.At, now)).Append(' ').Append(_formatter.Escape(error.Source)).Append(": ").Append(_formatter.Escape(Cut(DestinationRef.WithTitles(error.Message, _settings.Current.Destinations)))).Append('\n');
        }

        html.Append("<b>").Append(_strings.Get("status.destinations")).Append("</b>\n");
        var destinations = _settings.Current.Destinations;
        if (destinations.Length == 0)
        {
            html.Append(_strings.Get("status.noDestinations")).Append('\n');
        }

        var budget = MaxChars - html.Length - tail.Length - MoreReserve;
        var shown = 0;
        foreach (var destination in destinations)
        {
            var health = _health.Get(destination.Id);
            var badge = !destination.Enabled ? "⏸" : health.Status == HealthStatus.Ok ? "✅" : "⛔";
            var line = new StringBuilder(badge).Append(' ').Append(_formatter.Escape(destination.Title));
            if (health.Status != HealthStatus.Ok)
            {
                line.Append(": ").Append(_strings.Get("status.health." + health.Status));
            }

            line.Append('\n');
            if (line.Length > budget)
            {
                break;
            }

            budget -= line.Length;
            html.Append(line);
            shown++;
        }

        if (shown < destinations.Length)
        {
            html.Append(_formatter.Escape(_strings.Get("status.moreDestinations", ("count", _formatter.Number(destinations.Length - shown))))).Append('\n');
        }

        html.Append('\n').Append(tail);

        var back = Buttons.Callback(_strings, "menu.back", CallbackData.Format("m"));
        return new Screen(html.ToString().TrimEnd(), new InlineKeyboardMarkup(back));
    }

    private static string Cut(string text)
        => text.Length <= MaxErrorChars ? text : string.Concat(text.AsSpan(0, char.IsHighSurrogate(text[MaxErrorChars - 1]) ? MaxErrorChars - 1 : MaxErrorChars), "…");

    private string ConnectionText()
    {
        var state = !_compat.IsCompatible ? ConnectionState.Incompatible : !_gate.IsOwner ? ConnectionState.WaitingForInstance : _connection.State;
        var username = _connection.BotUsername.Length > 0 ? _connection.BotUsername : _state.Read(s => s.Telegram.BotUsername);
        return state switch
        {
            ConnectionState.Incompatible => _compat.Message ?? string.Empty,
            ConnectionState.WaitingForInstance => _gate.WaitingMessage ?? string.Empty,
            _ => _strings.Get("connection." + state, ("bot", username)),
        };
    }
}
