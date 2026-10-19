using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.State.Documents;

namespace Jellyfin.Plugin.TgJelly.Bot;

/// <summary>
/// The bot's persisted memory in state.json: each private chat's flow, who manages the server, chats the bot was added
/// to, and recent setup failures. Every method returns copies; caps drop the oldest and count as evictions.
/// </summary>
public sealed class BotMemory
{
    /// <summary>The most flows kept.</summary>
    public const int FlowsCap = 200;

    /// <summary>The most managers kept.</summary>
    public const int ManagersCap = 50;

    /// <summary>The most detected chats kept.</summary>
    public const int DetectedCap = 50;

    /// <summary>The most setup failures kept.</summary>
    public const int FailuresCap = 50;

    /// <summary>How long after its last change a flow is forgotten.</summary>
    public static readonly TimeSpan FlowTtl = TimeSpan.FromHours(24);

    /// <summary>How long a prompt waits for an answer.</summary>
    public static readonly TimeSpan PromptTtl = TimeSpan.FromMinutes(10);

    /// <summary>How long after its last use a manager is remembered.</summary>
    public static readonly TimeSpan ManagerTtl = TimeSpan.FromDays(90);

    /// <summary>How long a setup failure is remembered.</summary>
    public static readonly TimeSpan FailureTtl = TimeSpan.FromDays(7);

    /// <summary>How long a detected chat stays hidden, so the "added" event can finish creating its destination.</summary>
    public static readonly TimeSpan DetectedGrace = TimeSpan.FromSeconds(30);

    private readonly StateStore _state;
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of the <see cref="BotMemory"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="time">The clock.</param>
    public BotMemory(StateStore state, TimeProvider time)
    {
        _state = state;
        _time = time;
    }

    /// <summary>
    /// Gets a chat's flow.
    /// </summary>
    /// <param name="chatId">The private chat ID.</param>
    /// <returns>A copy of the flow; null when there is none or it expired.</returns>
    public FlowState? GetFlow(long chatId)
    {
        var now = _time.GetUtcNow();
        var key = Key(chatId);
        return _state.Read(s => s.Flows.TryGetValue(key, out var f) && f.UpdatedAt + FlowTtl > now ? Copy(f) : null);
    }

    /// <summary>
    /// Saves a chat's flow, stamping it as updated now.
    /// </summary>
    /// <param name="chatId">The private chat ID.</param>
    /// <param name="flow">The flow.</param>
    public void SaveFlow(long chatId, FlowState flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var now = _time.GetUtcNow();
        var copy = Copy(flow);
        copy.UpdatedAt = now;
        var evicted = 0;
        _state.Update(s =>
        {
            s.Flows[Key(chatId)] = copy;
            evicted = EvictOldest(s.Flows, FlowsCap, f => f.UpdatedAt);
        });
        CountEvictions(evicted);
    }

    /// <summary>
    /// Forgets a chat's flow.
    /// </summary>
    /// <param name="chatId">The private chat ID.</param>
    public void ClearFlow(long chatId) => _state.Update(s => s.Flows.Remove(Key(chatId)));

    /// <summary>
    /// Hands out the next request ID for a chat picker button.
    /// </summary>
    /// <returns>An increasing positive number.</returns>
    public int NextRequestId()
    {
        var id = 0;
        _state.Update(s =>
        {
            // Wraps back to 1 rather than going negative.
            s.Telegram.LastRequestId = s.Telegram.LastRequestId >= int.MaxValue || s.Telegram.LastRequestId < 0 ? 1 : s.Telegram.LastRequestId + 1;
            id = s.Telegram.LastRequestId;
        });
        return id;
    }

    /// <summary>
    /// Notes that a manager just used the bot.
    /// </summary>
    /// <param name="chatId">The manager's private chat ID.</param>
    public void TouchManager(long chatId)
    {
        var now = _time.GetUtcNow();
        var evicted = 0;
        _state.Update(s =>
        {
            s.Managers[Key(chatId)] = now;
            evicted = EvictOldest(s.Managers, ManagersCap, t => t);
        });
        CountEvictions(evicted);
    }

    /// <summary>
    /// Lists the managers seen within <see cref="ManagerTtl"/>.
    /// </summary>
    /// <returns>Their private chat IDs, most recent first.</returns>
    public IReadOnlyList<long> RecentManagers()
    {
        var now = _time.GetUtcNow();
        return _state.Read(s => (IReadOnlyList<long>)s.Managers
            .Where(m => m.Value + ManagerTtl > now)
            .OrderByDescending(m => m.Value)
            .Select(m => long.Parse(m.Key, CultureInfo.InvariantCulture))
            .ToList());
    }

    /// <summary>
    /// Forgets a manager.
    /// </summary>
    /// <param name="chatId">The manager's private chat ID.</param>
    public void RemoveManager(long chatId) => _state.Update(s => s.Managers.Remove(Key(chatId)));

    /// <summary>
    /// Records a chat the bot was added to; a chat seen again keeps its first detection time.
    /// </summary>
    /// <param name="chatId">The chat ID.</param>
    /// <param name="title">The chat title.</param>
    /// <param name="kind">The chat type.</param>
    public void RecordDetected(long chatId, string title, ChatKind kind)
    {
        var now = _time.GetUtcNow();
        var evicted = 0;
        _state.Update(s =>
        {
            var existing = s.DetectedChats.FirstOrDefault(d => d.ChatId == chatId);
            if (existing is null)
            {
                s.DetectedChats.Add(new DetectedChat { ChatId = chatId, Title = title, Kind = kind, DetectedAt = now });
            }
            else
            {
                existing.Title = title;
                existing.Kind = kind;
            }

            var over = s.DetectedChats.Count - DetectedCap;
            if (over > 0)
            {
                s.DetectedChats = [.. s.DetectedChats.OrderBy(d => d.DetectedAt).Skip(over)];
                evicted = over;
            }
        });
        CountEvictions(evicted);
    }

    /// <summary>
    /// Forgets a detected chat.
    /// </summary>
    /// <param name="chatId">The chat ID.</param>
    public void RemoveDetected(long chatId) => _state.Update(s => s.DetectedChats.RemoveAll(d => d.ChatId == chatId));

    /// <summary>
    /// Follows a group that became a supergroup: its detected entry and every flow working on it move to the new ID.
    /// </summary>
    /// <param name="oldChatId">The chat ID before the upgrade.</param>
    /// <param name="newChatId">The chat ID after the upgrade.</param>
    public void MigrateChat(long oldChatId, long newChatId)
    {
        var oldKey = Key(oldChatId);
        var newKey = Key(newChatId);
        _state.Update(s =>
        {
            var old = s.DetectedChats.FirstOrDefault(d => d.ChatId == oldChatId);
            if (old is not null)
            {
                var existing = s.DetectedChats.FirstOrDefault(d => d.ChatId == newChatId);
                if (existing is null)
                {
                    old.ChatId = newChatId;
                }
                else
                {
                    // Already seen under the new ID: keep that entry, with the earliest detection time.
                    existing.DetectedAt = existing.DetectedAt < old.DetectedAt ? existing.DetectedAt : old.DetectedAt;
                    s.DetectedChats.Remove(old);
                }
            }

            foreach (var flow in s.Flows.Values)
            {
                if (flow.Params.TryGetValue("chatId", out var id) && string.Equals(id, oldKey, StringComparison.Ordinal))
                {
                    flow.Params["chatId"] = newKey;
                }
            }
        });
    }

    /// <summary>
    /// Lists the detected chats worth offering: older than <see cref="DetectedGrace"/> and not a destination.
    /// </summary>
    /// <param name="destinationChatIds">The chat IDs of the destinations.</param>
    /// <returns>Copies, oldest first.</returns>
    public IReadOnlyList<DetectedChat> VisibleDetected(IReadOnlyCollection<long> destinationChatIds)
    {
        ArgumentNullException.ThrowIfNull(destinationChatIds);
        var now = _time.GetUtcNow();
        return _state.Read(s => (IReadOnlyList<DetectedChat>)s.DetectedChats
            .Where(d => now - d.DetectedAt >= DetectedGrace && !destinationChatIds.Contains(d.ChatId))
            .OrderBy(d => d.DetectedAt)
            .Select(d => new DetectedChat { ChatId = d.ChatId, Title = d.Title, Kind = d.Kind, DetectedAt = d.DetectedAt })
            .ToList());
    }

    /// <summary>
    /// Lists the detected chats worth offering, given the configured destinations.
    /// </summary>
    /// <param name="configuration">The current configuration.</param>
    /// <returns>Copies, oldest first.</returns>
    public IReadOnlyList<DetectedChat> VisibleDetected(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return VisibleDetected([.. configuration.Destinations.Select(d => d.ChatId)]);
    }

    /// <summary>
    /// Tells whether a chat is recorded as detected, shown yet or not.
    /// </summary>
    /// <param name="chatId">The chat ID.</param>
    /// <returns>True when it is.</returns>
    public bool IsDetected(long chatId) => _state.Read(s => s.DetectedChats.Any(d => d.ChatId == chatId));

    /// <summary>
    /// Records a setup failure.
    /// </summary>
    /// <param name="kind">What went wrong.</param>
    public void RecordFailure(SetupFailureKind kind)
    {
        var now = _time.GetUtcNow();
        var evicted = 0;
        _state.Update(s =>
        {
            s.SetupFailures.Add(new SetupFailure { Kind = kind, At = now });
            evicted = CapFailures(s);
        });
        CountEvictions(evicted);
    }

    /// <summary>
    /// Tells whether a setup failure happened within <see cref="FailureTtl"/>.
    /// </summary>
    /// <returns>True when one did.</returns>
    public bool HasRecentFailure()
    {
        var now = _time.GetUtcNow();
        return _state.Read(s => s.SetupFailures.Any(f => f.At + FailureTtl > now));
    }

    /// <summary>
    /// Expires old flows, managers and failures, and ends prompts that waited too long.
    /// </summary>
    /// <returns>The chat IDs whose prompt just expired; each has its prompt fields cleared and a
    /// <see cref="SetupFailureKind.PromptExpired"/> failure recorded.</returns>
    public IReadOnlyList<long> Prune()
    {
        var now = _time.GetUtcNow();
        var expired = new List<long>();
        var evicted = 0;
        _state.Update(s =>
        {
            foreach (var (key, flow) in s.Flows)
            {
                if (flow.PromptSentAt is { } sent && sent + PromptTtl <= now)
                {
                    flow.PromptMessageId = null;
                    flow.PromptSentAt = null;
                    flow.PickerRequestIds = [];
                    s.SetupFailures.Add(new SetupFailure { Kind = SetupFailureKind.PromptExpired, At = now });
                    expired.Add(long.Parse(key, CultureInfo.InvariantCulture));
                }
            }

            foreach (var key in s.Flows.Where(f => f.Value.UpdatedAt + FlowTtl <= now).Select(f => f.Key).ToList())
            {
                s.Flows.Remove(key);
            }

            foreach (var key in s.Managers.Where(m => m.Value + ManagerTtl <= now).Select(m => m.Key).ToList())
            {
                s.Managers.Remove(key);
            }

            s.SetupFailures.RemoveAll(f => f.At + FailureTtl <= now);
            evicted = CapFailures(s);
        });
        CountEvictions(evicted);
        return expired;
    }

    private static string Key(long chatId) => chatId.ToString(CultureInfo.InvariantCulture);

    private static FlowState Copy(FlowState f) => new()
    {
        Screen = f.Screen,
        Params = new Dictionary<string, string>(f.Params, StringComparer.Ordinal),
        WizardMessageId = f.WizardMessageId,
        PromptMessageId = f.PromptMessageId,
        CheckingMessageId = f.CheckingMessageId,
        PromptSentAt = f.PromptSentAt,
        PickerRequestIds = [.. f.PickerRequestIds],
        UpdatedAt = f.UpdatedAt,
    };

    private static int CapFailures(StateDocument s)
    {
        var over = s.SetupFailures.Count - FailuresCap;
        if (over <= 0)
        {
            return 0;
        }

        s.SetupFailures = [.. s.SetupFailures.OrderBy(f => f.At).Skip(over)];
        return over;
    }

    private static int EvictOldest<T>(Dictionary<string, T> items, int cap, Func<T, DateTimeOffset> stamp)
    {
        var over = items.Count - cap;
        if (over <= 0)
        {
            return 0;
        }

        foreach (var key in items.OrderBy(kv => stamp(kv.Value)).Take(over).Select(kv => kv.Key).ToList())
        {
            items.Remove(key);
        }

        return over;
    }

    private void CountEvictions(int evicted)
    {
        if (evicted > 0)
        {
            _state.Update(s => s.Status.Counters.Evictions += evicted);
        }
    }
}
