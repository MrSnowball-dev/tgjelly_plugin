using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Bot.Screens;

public sealed class FlowEngineTests : IDisposable, IAsyncLifetime
{
    private const long Manager = 42;
    private readonly BotTestHarness _h = new();
    private BotRig _rig = null!;

    public async ValueTask InitializeAsync() => _rig = await BotRig.CreateAsync(_h);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task ProblemFound_QueuesTheManagerNotification()
    {
        var d = _h.AddDestination(-1001);
        _rig.Memory.TouchManager(Manager);

        _rig.Health.MarkProblem(d.Id, HealthStatus.Lost, "kicked");
        Assert.Empty(_h.Api.CallsTo("sendMessage"));
        await _rig.Queue.RunPendingAsync(CancellationToken.None, (name, ex) => throw new InvalidOperationException(name, ex));

        var call = Assert.Single(_h.Api.CallsTo("sendMessage"));
        Assert.Equal(Manager, (long)call.Body["chat_id"]!);
    }

    [Fact]
    public async Task Tick_RetriesUnreportedProblems()
    {
        var d = _h.AddDestination(-1001);
        _rig.Memory.TouchManager(Manager);
        _rig.Health.MarkProblem(d.Id, HealthStatus.Lost, "kicked");

        await _rig.Flow.TickAsync(CancellationToken.None);

        Assert.Single(_h.Api.CallsTo("sendMessage"));
        Assert.True(_rig.Health.Get(d.Id).Notified);
    }

    [Fact]
    public async Task Dispose_StopsListeningForProblems()
    {
        var d = _h.AddDestination(-1001);
        _rig.Flow.Dispose();

        _rig.Health.MarkProblem(d.Id, HealthStatus.Lost, "kicked");
        await _rig.Queue.RunPendingAsync(CancellationToken.None);

        Assert.Empty(_h.Api.CallsTo("sendMessage"));
        Assert.False(_rig.Health.Get(d.Id).Notified);
    }

    [Fact]
    public async Task Menu_SendsTheMainMenu_AndTouchesTheManager()
    {
        _h.AddDestination(-1001);

        await _rig.Deliver(TelegramFixtures.Update("private_text", ("text", "/menu")));

        var call = Assert.Single(_h.Api.CallsTo("sendMessage"));
        Assert.Contains("<tg-emoji emoji-id=\"5875271289605722323\">🏠</tg-emoji> Menu", (string)call.Body["text"]!, StringComparison.Ordinal);
        Assert.Contains("Destinations (1)", ButtonTexts(call), StringComparison.Ordinal);
        Assert.Equal([Manager], _rig.Memory.RecentManagers());
    }

    [Fact]
    public async Task PlainStartWithDestinations_ShowsSetUpMenu()
    {
        _h.AddDestination(-1001);
        _h.AddDestination(-1002);

        await _rig.Deliver(TelegramFixtures.Update("private_command_start"));

        var call = Assert.Single(_h.Api.CallsTo("sendMessage"));
        Assert.Contains("TgJelly is set up: 2 destinations", (string)call.Body["text"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlainStartWithoutDestinations_StartsOnboarding()
    {
        await _rig.Deliver(TelegramFixtures.Update("private_command_start"));

        var call = Assert.Single(_h.Api.CallsTo("sendMessage"));
        Assert.StartsWith("Where should notifications go?", (string)call.Body["text"]!, StringComparison.Ordinal);
        Assert.NotNull(call.Body["reply_markup"]!["inline_keyboard"]);
        Assert.Null(call.Body["reply_markup"]!["keyboard"]);
    }

    [Fact]
    public async Task UnknownStartPayload_ShowsSetUpMenu()
    {
        _h.AddDestination(-1001);

        await _rig.Deliver(TelegramFixtures.Update("private_text", ("text", "/start frobnicate")));

        Assert.Contains("TgJelly is set up: 1 destination", (string)Assert.Single(_h.Api.CallsTo("sendMessage")).Body["text"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownText_ShowsSetUpMenu()
    {
        _h.AddDestination(-1001);

        await _rig.Deliver(TelegramFixtures.Update("private_text", ("text", "what is this")));
        await _rig.Deliver(TelegramFixtures.Update("private_text", ("text", "/frobnicate")));

        Assert.All(_h.Api.CallsTo("sendMessage"), c => Assert.Contains("TgJelly is set up: 1 destination", (string)c.Body["text"]!, StringComparison.Ordinal));
        Assert.Equal(2, _h.Api.CallsTo("sendMessage").Count());
    }

    [Fact]
    public async Task UnparseableCallback_SaysExpired_AndShowsMenu()
    {
        _h.AddDestination(-1001);

        await _rig.Deliver(TelegramFixtures.Update("callback_query", ("data", "0|old|x")));

        var sent = _h.Api.CallsTo("sendMessage").ToList();
        Assert.Equal(2, sent.Count);
        Assert.Equal("That menu expired", (string)sent[0].Body["text"]!);
        Assert.Contains("<tg-emoji emoji-id=\"5875271289605722323\">🏠</tg-emoji> Menu", (string)sent[1].Body["text"]!, StringComparison.Ordinal);
        Assert.Empty(_h.Api.CallsTo("editMessageText"));
    }

    [Fact]
    public async Task MenuCallback_EditsTheMessage()
    {
        await _rig.Deliver(TelegramFixtures.Update("callback_query", ("data", "1|st"), ("message_id", "55")));

        var edit = Assert.Single(_h.Api.CallsTo("editMessageText"));
        Assert.Equal(55, (int)edit.Body["message_id"]!);
        Assert.Empty(_h.Api.CallsTo("sendMessage"));
    }

    [Fact]
    public async Task Help_MentionsTheDashboardPath()
    {
        await _rig.Deliver(TelegramFixtures.Update("private_text", ("text", "/help")));

        Assert.Contains("Dashboard → Plugins → Telegram", (string)Assert.Single(_h.Api.CallsTo("sendMessage")).Body["text"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_ListsHealthAndCounts()
    {
        var ok = _h.AddDestination(-1001);
        var lost = _h.AddDestination(-1002);
        _rig.Health.MarkProblem(lost.Id, HealthStatus.Lost, "kicked");
        _h.State.RecordError("outbox", "sad");

        await _rig.Deliver(TelegramFixtures.Update("private_text", ("text", "/status")));

        var text = (string)Assert.Single(_h.Api.CallsTo("sendMessage")).Body["text"]!;
        Assert.Contains("Connected as @", text, StringComparison.Ordinal);
        Assert.Contains("✅ " + ok.Title, text, StringComparison.Ordinal);
        Assert.Contains("⛔ " + lost.Title, text, StringComparison.Ordinal);
        Assert.Contains("Waiting for metadata: 0", text, StringComparison.Ordinal);
        Assert.Contains("Queued to send: 0", text, StringComparison.Ordinal);
        Assert.Contains("sad", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tick_PrunesTheMemory()
    {
        await _rig.Deliver(TelegramFixtures.Update("private_text", ("text", "/menu")));
        Assert.Equal(1, _h.State.Read(s => s.Managers.Count));

        _h.Time.Advance(TimeSpan.FromDays(91));
        await _rig.Flow.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, _h.State.Read(s => s.Managers.Count));
    }

    private static string ButtonTexts(BotApiCall call)
        => string.Join("|", call.Body["reply_markup"]!["inline_keyboard"]!.AsArray().SelectMany(r => r!.AsArray()).Select(b => (string)b!["text"]!));
}
