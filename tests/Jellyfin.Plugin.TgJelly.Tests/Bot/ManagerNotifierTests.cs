using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Bot.Screens;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Bot;

public sealed class ManagerNotifierTests : IDisposable
{
    private const string SentOk = "{\"message_id\":1,\"date\":0,\"chat\":{\"id\":1,\"type\":\"private\"}}";
    private readonly BotTestHarness _h = new();
    private readonly BotMemory _memory;
    private readonly HealthService _health;
    private readonly ManagerNotifier _notifier;

    public ManagerNotifierTests()
    {
        _h.Connect();
        _memory = new BotMemory(_h.State, _h.Time);
        _health = new HealthService(
            new Jellyfin.Plugin.TgJelly.State.InstanceGate(_h.Paths, _h.Time, NullLogger<Jellyfin.Plugin.TgJelly.State.InstanceGate>.Instance),
            _h.Connection,
            new DestinationChecker(_h.Connection, _h.Strings),
            _h.Settings,
            _h.State,
            _h.Time,
            NullLogger<HealthService>.Instance);
        _notifier = new ManagerNotifier(_memory, _health, _h.Settings, _h.Connection, _h.State, _h.Strings, NullLogger<ManagerNotifier>.Instance);
    }

    public void Dispose()
    {
        _health.Dispose();
        _h.Dispose();
    }

    [Fact]
    public async Task NewProblem_MessagesEachRecentManagerOnce()
    {
        var d = _h.AddDestination(-1001);
        _memory.TouchManager(11);
        _memory.TouchManager(12);
        _health.MarkProblem(d.Id, HealthStatus.Lost, "Forbidden: bot <was> kicked");

        await _notifier.NotifyAsync(TestContext.Current.CancellationToken);

        var calls = _h.Api.CallsTo("sendMessage").ToList();
        Assert.Equal([11L, 12L], calls.Select(c => (long)c.Body["chat_id"]!).Order());
        var text = (string)calls[0].Body["text"]!;
        Assert.Equal("⚠️ <b>Chat -1001</b>: Forbidden: bot &lt;was&gt; kicked", text);
        Assert.Equal("Html", (string)calls[0].Body["parse_mode"]!);
        var buttons = calls[0].Body["reply_markup"]!["inline_keyboard"]!.AsArray().SelectMany(r => r!.AsArray()).ToList();
        Assert.Equal(["Open", "Check again"], buttons.Select(b => (string)b!["text"]!));
        Assert.Equal([CallbackData.Format("d", d.Id), CallbackData.Format("dc", d.Id)], buttons.Select(b => (string)b!["callback_data"]!));
        Assert.True(_health.Get(d.Id).Notified);
    }

    [Fact]
    public async Task SameProblemAgain_NotRepeated()
    {
        var d = _h.AddDestination(-1001);
        _memory.TouchManager(11);
        _health.MarkProblem(d.Id, HealthStatus.Lost, "kicked");
        await _notifier.NotifyAsync(TestContext.Current.CancellationToken);

        _health.MarkProblem(d.Id, HealthStatus.Lost, "kicked again");
        await _notifier.NotifyAsync(TestContext.Current.CancellationToken);

        Assert.Single(_h.Api.CallsTo("sendMessage"));
    }

    [Fact]
    public async Task NewKindOfProblem_IsReportedAgain()
    {
        var d = _h.AddDestination(-1001);
        _memory.TouchManager(11);
        _health.MarkProblem(d.Id, HealthStatus.Lost, "kicked");
        await _notifier.NotifyAsync(TestContext.Current.CancellationToken);

        _health.MarkProblem(d.Id, HealthStatus.NeedsPermissions, "no rights");
        await _notifier.NotifyAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, _h.Api.CallsTo("sendMessage").Count());
    }

    [Fact]
    public async Task NoManagers_StillMarksNotified()
    {
        var d = _h.AddDestination(-1001);
        _health.MarkProblem(d.Id, HealthStatus.TopicMissing, "gone");

        await _notifier.NotifyAsync(TestContext.Current.CancellationToken);

        Assert.Empty(_h.Api.CallsTo("sendMessage"));
        Assert.True(_health.Get(d.Id).Notified);
    }

    [Fact]
    public async Task EmptyError_FallsBackToTheStatusText()
    {
        var d = _h.AddDestination(-1001);
        _memory.TouchManager(11);
        _health.MarkProblem(d.Id, HealthStatus.NeedsPermissions, string.Empty);

        await _notifier.NotifyAsync(TestContext.Current.CancellationToken);

        Assert.Equal("⚠️ <b>Chat -1001</b>: ⛔ Needs permissions", (string)_h.Api.CallsTo("sendMessage").Single().Body["text"]!);
    }

    [Fact]
    public async Task BlockedManager_IsRemoved_AndOthersStillGetTheMessage()
    {
        var d = _h.AddDestination(-1001);
        _memory.TouchManager(11);
        _memory.TouchManager(12);
        _h.Api.On("sendMessage", call => (long)call.Body["chat_id"]! == 11
            ? FakeBotApi.Error(403, "Forbidden: bot was blocked by the user")
            : FakeBotApi.Ok(SentOk));
        _health.MarkProblem(d.Id, HealthStatus.Lost, "kicked");

        await _notifier.NotifyAsync(TestContext.Current.CancellationToken);

        Assert.Equal([12L], _memory.RecentManagers());
        Assert.True(_health.Get(d.Id).Notified);
    }

    [Fact]
    public async Task TransportError_RetriesNextTick()
    {
        var d = _h.AddDestination(-1001);
        _memory.TouchManager(11);
        _h.Api.On("sendMessage", _ => FakeBotApi.Error(502, "Bad Gateway"));
        _health.MarkProblem(d.Id, HealthStatus.Lost, "kicked");

        await _notifier.NotifyAsync(TestContext.Current.CancellationToken);

        Assert.False(_health.Get(d.Id).Notified);
        Assert.Equal([11L], _memory.RecentManagers());

        _h.Api.On("sendMessage", _ => FakeBotApi.Ok(SentOk));
        await _notifier.NotifyAsync(TestContext.Current.CancellationToken);

        Assert.True(_health.Get(d.Id).Notified);
    }

    [Fact]
    public async Task PermanentlyFailingManager_DoesNotCauseRepeats()
    {
        var d = _h.AddDestination(-1001);
        _memory.TouchManager(11);
        _memory.TouchManager(12);
        _h.Api.On("sendMessage", call => (long)call.Body["chat_id"]! == 11
            ? FakeBotApi.Error(400, "Bad Request: chat not found")
            : FakeBotApi.Ok(SentOk));
        _health.MarkProblem(d.Id, HealthStatus.Lost, "kicked");

        for (var i = 0; i < 3; i++)
        {
            await _notifier.NotifyAsync(TestContext.Current.CancellationToken);
        }

        Assert.Single(_h.Api.CallsTo("sendMessage"), c => (long)c.Body["chat_id"]! == 12);
        Assert.True(_health.Get(d.Id).Notified);
        Assert.Equal([12L], _memory.RecentManagers());
    }

    [Fact]
    public async Task OtherPermanentError_GivesUpWithoutRemovingTheManager()
    {
        var d = _h.AddDestination(-1001);
        _memory.TouchManager(11);
        _h.Api.On("sendMessage", _ => FakeBotApi.Error(400, "Bad Request: something odd"));
        _health.MarkProblem(d.Id, HealthStatus.Lost, "kicked");

        await _notifier.NotifyAsync(TestContext.Current.CancellationToken);

        Assert.True(_health.Get(d.Id).Notified);
        Assert.Equal([11L], _memory.RecentManagers());
    }

    [Fact]
    public async Task TransientFailureOfOne_RetriesOnlyThatManager()
    {
        var d = _h.AddDestination(-1001);
        _memory.TouchManager(11);
        _memory.TouchManager(12);
        var failed = false;
        _h.Api.On("sendMessage", call =>
        {
            if ((long)call.Body["chat_id"]! == 11 && !failed)
            {
                failed = true;
                return FakeBotApi.Error(502, "Bad Gateway");
            }

            return FakeBotApi.Ok(SentOk);
        });
        _health.MarkProblem(d.Id, HealthStatus.Lost, "kicked");

        await _notifier.NotifyAsync(TestContext.Current.CancellationToken);
        Assert.False(_health.Get(d.Id).Notified);
        await _notifier.NotifyAsync(TestContext.Current.CancellationToken);

        Assert.True(_health.Get(d.Id).Notified);
        Assert.Single(_h.Api.CallsTo("sendMessage"), c => (long)c.Body["chat_id"]! == 12);
        Assert.Equal(2, _h.Api.CallsTo("sendMessage").Count(c => (long)c.Body["chat_id"]! == 11));
    }

    [Fact]
    public async Task OverlongError_IsCut()
    {
        var d = _h.AddDestination(-1001);
        _memory.TouchManager(11);
        _health.MarkProblem(d.Id, HealthStatus.Lost, new string('x', 299) + "😀😀😀");

        await _notifier.NotifyAsync(TestContext.Current.CancellationToken);

        Assert.Equal("⚠️ <b>Chat -1001</b>: " + new string('x', 299) + "😀…", (string)_h.Api.CallsTo("sendMessage").Single().Body["text"]!);
    }

    [Fact]
    public async Task Disconnected_LeavesNotifiedFalse()
    {
        var d = _h.AddDestination(-1001);
        _memory.TouchManager(11);
        _h.Connection.Detach();
        _health.MarkProblem(d.Id, HealthStatus.Lost, "kicked");

        await _notifier.NotifyAsync(TestContext.Current.CancellationToken);

        Assert.False(_health.Get(d.Id).Notified);
    }

    [Fact]
    public async Task Recovery_ResetsNotified()
    {
        var d = _h.AddDestination(-1001);
        _health.MarkProblem(d.Id, HealthStatus.Lost, "kicked");
        await _notifier.NotifyAsync(TestContext.Current.CancellationToken);

        _health.SetChecked(d.Id, new ChatCheckResult(true, HealthStatus.Ok, null, -1001, "Chat", ChatKind.Supergroup, false, false));

        Assert.False(_health.Get(d.Id).Notified);
    }

    [Fact]
    public void ProblemFound_IsRaisedOnlyForANewProblemStatus()
    {
        var d = _h.AddDestination(-1001);
        var found = new System.Collections.Generic.List<string>();
        _health.ProblemFound += (_, id) => found.Add(id);

        _health.MarkProblem(d.Id, HealthStatus.Lost, "a");
        _health.MarkProblem(d.Id, HealthStatus.Lost, "b");
        _health.MarkProblem(d.Id, HealthStatus.TopicMissing, "c");

        Assert.Equal([d.Id, d.Id], found);
    }

    [Fact]
    public void ProblemFound_SubscriberException_DoesNotEscape()
    {
        var d = _h.AddDestination(-1001);
        _health.ProblemFound += (_, _) => throw new InvalidOperationException("boom");

        _health.MarkProblem(d.Id, HealthStatus.Lost, "a");

        Assert.Equal(HealthStatus.Lost, _health.Get(d.Id).Status);
    }
}
