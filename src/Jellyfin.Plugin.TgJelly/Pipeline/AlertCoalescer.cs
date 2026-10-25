using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.TgJelly.Intake;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Strings;

namespace Jellyfin.Plugin.TgJelly.Pipeline;

/// <summary>
/// Turns security and server events into messages: failed logins of a user share one message in a sliding
/// window, a lockout is held briefly so it can reply to that message, and repeated or redundant events stay quiet.
/// Windows and markers live in queue.json; held lockouts only in memory, and <see cref="Drain"/> releases them.
/// </summary>
public sealed class AlertCoalescer
{
    /// <summary>The most failed-login windows kept.</summary>
    public const int WindowsCap = 500;

    /// <summary>The most sign-in markers kept.</summary>
    public const int LoginDedupeCap = 2_000;

    /// <summary>The most user-created markers kept.</summary>
    public const int UserCreatedCap = 100;

    /// <summary>The most recent plugin events kept.</summary>
    public const int PluginEventsCap = 50;

    /// <summary>How long after its last attempt a failed-login window ends.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>How long a lockout waits for its failed-login message.</summary>
    public static readonly TimeSpan LockoutHold = TimeSpan.FromSeconds(5);

    /// <summary>How long after a user is created a password change is part of the creation.</summary>
    public static readonly TimeSpan UserCreatedMerge = TimeSpan.FromSeconds(10);

    /// <summary>How long a sign-in from the same device stays quiet.</summary>
    public static readonly TimeSpan LoginDedupe = TimeSpan.FromHours(12);

    /// <summary>How far back plugin events count as reasons for a pending restart.</summary>
    public static readonly TimeSpan RestartReasons = TimeSpan.FromMinutes(10);

    private readonly StateStore _state;
    private readonly StringTable _strings;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly List<(NotificationEvent Event, DateTimeOffset Due)> _held = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="AlertCoalescer"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="strings">The string table (restart reasons).</param>
    /// <param name="time">The clock.</param>
    public AlertCoalescer(StateStore state, StringTable strings, TimeProvider time)
    {
        _state = state;
        _strings = strings;
        _time = time;
    }

    /// <summary>
    /// The sent-message key of a user's failed-login message.
    /// </summary>
    /// <param name="userKey">The upper-cased user name.</param>
    /// <returns>The key.</returns>
    public static string LoginFailedKey(string userKey) => "login_failed:" + userKey;

    /// <summary>
    /// Processes an event.
    /// </summary>
    /// <param name="e">The event.</param>
    /// <returns>The messages it gives; none while it is held or when it is a duplicate.</returns>
    public IReadOnlyList<AlertOutput> Process(NotificationEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        switch (e.EventKey)
        {
            case "security.login_failed":
                return [LoginFailed(e)];
            case "security.lockout":
                lock (_gate)
                {
                    _held.Add((e, e.At + LockoutHold));
                }

                return [];
            case "security.user_created":
                if (Value(e, EventFields.UserId) is { Length: > 0 } created)
                {
                    _state.UpdateQueue(q => q.Dedupe.UserCreated[created] = e.At);
                }

                return [Send(e)];
            case "security.password_changed":
                var merged = _state.ReadQueue(q => q.Dedupe.UserCreated.TryGetValue(Value(e, EventFields.UserId), out var at) && e.At - at <= UserCreatedMerge);
                return merged ? [] : [Send(e)];
            case "security.login_succeeded":
                var key = Value(e, EventFields.UserId) + "|" + Value(e, EventFields.DeviceId);
                var duplicate = false;
                _state.UpdateQueue(q =>
                {
                    duplicate = q.Dedupe.Logins.TryGetValue(key, out var at) && e.At - at < LoginDedupe;
                    if (!duplicate)
                    {
                        q.Dedupe.Logins[key] = e.At;
                    }
                });
                return duplicate ? [] : [Send(e)];
            case "server.plugin_installed" or "server.plugin_updated" or "server.plugin_uninstalled" or "server.plugin_install_failed":
                _state.UpdateQueue(q => q.RecentPluginEvents.Add(new RecentPluginEvent
                {
                    At = e.At, EventKey = e.EventKey, Plugin = Value(e, EventFields.Plugin), Version = Value(e, EventFields.Version),
                }));
                return [Send(e)];
            case "server.restart_pending":
                var lines = _state.ReadQueue(q => q.RecentPluginEvents
                    .Where(p => e.At - p.At <= RestartReasons)
                    .OrderBy(p => p.At)
                    .Select(p => _strings.Get("server.reason." + p.EventKey, ("plugin", p.Plugin), ("version", p.Version)))
                    .ToList());
                var restart = Send(e);
                restart.Model.Lines = lines;
                return [restart];
            default:
                return [Send(e)];
        }
    }

    /// <summary>
    /// Releases lockouts whose hold has passed, and removes ended windows and expired markers.
    /// </summary>
    /// <returns>The released lockout messages.</returns>
    public IReadOnlyList<AlertOutput> Tick()
    {
        var now = _time.GetUtcNow();
        var outputs = Release(now, all: false);
        var evicted = 0;

        // Most ticks find nothing to do; checking first keeps queue.json from being rewritten while idle.
        if (!_state.ReadQueue(q => q.SecurityWindows.Values.Any(w => now >= w.LastAt + Window)
            || q.Dedupe.Logins.Values.Any(at => now - at >= LoginDedupe)
            || q.Dedupe.UserCreated.Values.Any(at => now - at > UserCreatedMerge)
            || q.RecentPluginEvents.Exists(p => now - p.At > RestartReasons)
            || q.SecurityWindows.Count > WindowsCap
            || q.Dedupe.Logins.Count > LoginDedupeCap
            || q.Dedupe.UserCreated.Count > UserCreatedCap
            || q.RecentPluginEvents.Count > PluginEventsCap))
        {
            return outputs;
        }

        _state.UpdateQueue(q =>
        {
            Expire(q.SecurityWindows, w => now >= w.LastAt + Window);
            Expire(q.Dedupe.Logins, at => now - at >= LoginDedupe);
            Expire(q.Dedupe.UserCreated, at => now - at > UserCreatedMerge);
            q.RecentPluginEvents.RemoveAll(p => now - p.At > RestartReasons);
            evicted += Cap(q.SecurityWindows, WindowsCap, w => w.LastAt);
            evicted += Cap(q.Dedupe.Logins, LoginDedupeCap, at => at);
            evicted += Cap(q.Dedupe.UserCreated, UserCreatedCap, at => at);
            if (q.RecentPluginEvents.Count > PluginEventsCap)
            {
                var extra = q.RecentPluginEvents.Count - PluginEventsCap;
                q.RecentPluginEvents = q.RecentPluginEvents.OrderBy(p => p.At).Skip(extra).ToList();
                evicted += extra;
            }
        });
        if (evicted > 0)
        {
            _state.Update(s => s.Status.Counters.Evictions += evicted);
        }

        return outputs;
    }

    /// <summary>
    /// Releases every held lockout now (graceful stop).
    /// </summary>
    /// <returns>The lockout messages.</returns>
    public IReadOnlyList<AlertOutput> Drain() => Release(_time.GetUtcNow(), all: true);

    private static string Value(NotificationEvent e, string field) => e.Values.TryGetValue(field, out var value) ? value : string.Empty;

    private static string UserKey(NotificationEvent e)
        => Value(e, EventFields.UserKey) is { Length: > 0 } key ? key : Value(e, EventFields.User).ToUpperInvariant();

    private static AlertOutput Send(NotificationEvent e) => new(Model(e.EventKey, e.At, new Dictionary<string, string>(e.Values, StringComparer.Ordinal)), OutboxOp.Send, null, null, null);

    private static RenderModel Model(string eventKey, DateTimeOffset at, Dictionary<string, string> values)
        => new() { Kind = RenderKind.Alert, EventKey = eventKey, At = at, Values = values };

    private static RenderModel WindowModel(SecurityWindow w) => Model("security.login_failed", w.LastAt, new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [EventFields.User] = w.User,
        [EventFields.Attempts] = w.Attempts.ToString(CultureInfo.InvariantCulture),
        [EventFields.IpCount] = w.Ips.Count.ToString(CultureInfo.InvariantCulture),
        [EventFields.Ip] = w.LastIp,
        [EventFields.Client] = w.Client,
        [EventFields.Device] = w.Device,
        [EventFields.Locked] = w.Locked ? "true" : "false",
    });

    private static void Expire<T>(Dictionary<string, T> entries, Func<T, bool> expired)
    {
        foreach (var key in entries.Where(kv => expired(kv.Value)).Select(kv => kv.Key).ToList())
        {
            entries.Remove(key);
        }
    }

    private static int Cap<T>(Dictionary<string, T> entries, int cap, Func<T, DateTimeOffset> age)
    {
        var extra = entries.Count - cap;
        if (extra <= 0)
        {
            return 0;
        }

        foreach (var key in entries.OrderBy(kv => age(kv.Value)).Take(extra).Select(kv => kv.Key).ToList())
        {
            entries.Remove(key);
        }

        return extra;
    }

    private AlertOutput LoginFailed(NotificationEvent e)
    {
        var userKey = UserKey(e);
        var sentKey = LoginFailedKey(userKey);
        RenderModel? model = null;
        var edit = false;
        _state.UpdateQueue(
            q =>
            {
                edit = q.SecurityWindows.TryGetValue(userKey, out var window) && e.At < window.LastAt + Window;
                if (!edit)
                {
                    window = new SecurityWindow { FirstAt = e.At };
                    q.SecurityWindows[userKey] = window;
                }

                var ip = Value(e, EventFields.Ip);
                window!.User = Value(e, EventFields.User);
                window.LastAt = e.At;
                window.Attempts++;
                if (ip.Length > 0 && !window.Ips.Contains(ip, StringComparer.Ordinal))
                {
                    window.Ips.Add(ip);
                }

                window.LastIp = ip;
                window.Client = Value(e, EventFields.Client);
                window.Device = Value(e, EventFields.Device);
                model = WindowModel(window);
            },
            urgent: true);
        return edit ? new AlertOutput(model!, OutboxOp.Edit, sentKey, sentKey, null) : new AlertOutput(model!, OutboxOp.Send, sentKey, null, null);
    }

    private List<AlertOutput> Release(DateTimeOffset now, bool all)
    {
        List<NotificationEvent> due;
        lock (_gate)
        {
            due = _held.Where(h => all || h.Due <= now).Select(h => h.Event).ToList();
            _held.RemoveAll(h => all || h.Due <= now);
        }

        var outputs = new List<AlertOutput>();
        foreach (var e in due)
        {
            var userKey = UserKey(e);
            var sentKey = LoginFailedKey(userKey);
            RenderModel? locked = null;
            var attempts = Value(e, EventFields.Attempts);
            _state.UpdateQueue(
                q =>
                {
                    if (q.SecurityWindows.TryGetValue(userKey, out var window) && now < window.LastAt + Window)
                    {
                        window.Locked = true;
                        attempts = window.Attempts.ToString(CultureInfo.InvariantCulture);
                        locked = WindowModel(window);
                    }
                },
                urgent: true);
            var values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [EventFields.User] = Value(e, EventFields.User),
                [EventFields.Attempts] = attempts,
            };
            outputs.Add(new AlertOutput(Model(e.EventKey, e.At, values), OutboxOp.Send, null, null, locked is null ? null : sentKey));
            if (locked is not null)
            {
                outputs.Add(new AlertOutput(locked, OutboxOp.Edit, sentKey, sentKey, null));
            }
        }

        return outputs;
    }
}
