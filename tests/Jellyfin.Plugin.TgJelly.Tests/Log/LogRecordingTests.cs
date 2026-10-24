using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.Library;
using Jellyfin.Plugin.TgJelly.Log;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Log;

public sealed class LogRecordingTests : IDisposable
{
    private static readonly LibraryRef Movies = new(Guid.NewGuid(), "Movies", "movies");
    private static readonly LibraryRef Kids = new(Guid.NewGuid(), "Kids", "movies");
    private static readonly LibraryRef Shows = new(Guid.NewGuid(), "Shows", "tvshows");
    private static readonly Guid BearSeries = Guid.NewGuid();
    private static readonly Guid BearSeason = Guid.NewGuid();
    private readonly BotTestHarness _h = new();
    private readonly EventLog _log;
    private readonly LogRig _rig;

    public LogRecordingTests()
    {
        _log = new EventLog(_h.Paths, _h.Time, NullLogger<EventLog>.Instance);
        _rig = new LogRig(_h, _log);
    }

    private DateTimeOffset Now => _h.Time.GetUtcNow();

    [Fact]
    public void Log_TestMessages_HaveTheirOwnRows_AndAFailedOneIsAProblem()
    {
        var d = _h.AddDestination(-1001);

        _rig.Recorder.Test(d, "test", sent: true, error: null);
        _h.Time.Advance(TimeSpan.FromSeconds(1));
        _rig.Recorder.Test(d, "media.movie", sent: false, error: "403 Forbidden: bot was kicked from the channel chat");

        var rows = _log.Query(new LogQuery()).Items;
        Assert.Equal([LogResult.TestFailed, LogResult.TestSent], rows.Select(r => r.Result));
        Assert.Empty(rows[1].Details);
        Assert.Equal("403 Forbidden: bot was kicked from the channel chat", rows[0].Telegram);
        Assert.Contains("403 Forbidden", rows[0].Details.Single(), StringComparison.Ordinal);
        Assert.NotNull(rows[0].Advice);
        Assert.Equal([rows[0].Id], _log.Query(new LogQuery { ProblemsOnly = true }).Items.Select(r => r.Id));
    }

    [Fact]
    public void Log_LibraryExcluded_SkippedWithLibraryName()
    {
        var kidsOnly = _h.AddDestination(-1001, preset: PresetKind.NewMedia);
        var all = _h.AddDestination(-1002, preset: PresetKind.NewMedia);
        _h.Settings.Update(null, c => c.Destinations[0].LibraryIds = [Kids.Id.ToString("N")]);
        var movie = Movie("Dune: Part Two", Movies);

        Ready(movie);

        var skipped = Single(kidsOnly.Id);
        Assert.Equal((LogResult.Skipped, "Dune: Part Two", "media.movie", kidsOnly.Title), (skipped.Result, skipped.Title, skipped.EventKey, skipped.DestinationTitle));
        Assert.Equal(["This destination doesn't get the Movies library"], skipped.Details);
        Assert.Equal(LogResult.Waiting, Single(all.Id).Result);
    }

    [Fact]
    public void Log_PausedAndEventOff_SkippedPerDestination()
    {
        var paused = _h.AddDestination(-1001, preset: PresetKind.NewMedia);
        var admin = _h.AddDestination(-1002, preset: PresetKind.AdminAlerts);
        _h.Settings.Update(null, c => c.Destinations[0].Enabled = false);

        Ready(Movie("Arrival", Movies));

        Assert.Equal(["This destination is paused"], Single(paused.Id).Details);
        Assert.Equal(["This destination has Movies switched off"], Single(admin.Id).Details);
    }

    [Fact]
    public void Log_NoDestinationWants_DashRow()
    {
        _h.AddDestination(-1001, preset: PresetKind.AdminAlerts);

        Ready(Movie("Arrival", Movies));

        var row = Assert.Single(Rows());
        Assert.Equal((LogResult.Skipped, (string?)null, (string?)null, "Arrival"), (row.Result, row.DestinationId, row.DestinationTitle, row.Title));
        Assert.Equal(["No destination has Movies switched on"], row.Details);
    }

    [Fact]
    public async Task Log_BatchedEpisodes_OneRowWaitingThenSent()
    {
        var d = _h.AddDestination(-1001, preset: PresetKind.NewMedia);
        var series = Guid.NewGuid();
        var season = Guid.NewGuid();
        var episodes = Enumerable.Range(1, 4).Select(n => _rig.Library.Add(
            new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Episode, Name = "E" + n, SeriesId = series, SeriesName = "The Bear", SeasonId = season, SeasonNumber = 4, EpisodeNumber = n },
            Shows)).ToArray();

        Ready(episodes[0]);
        var waiting = Assert.Single(Rows());
        Assert.Equal((LogResult.Waiting, "The Bear, S04E01"), (waiting.Result, waiting.Title));
        Ready(episodes[1..]);
        waiting = Assert.Single(Rows());
        Assert.Equal((LogResult.Waiting, "The Bear, S04E01–E04", d.Id), (waiting.Result, waiting.Title, waiting.DestinationId));
        Assert.NotEmpty(waiting.Details);

        _rig.RunFor(150);
        Assert.Equal(LogResult.Waiting, Assert.Single(Rows()).Result);
        await _rig.DeliverAllAsync();

        var sent = Assert.Single(Rows());
        Assert.Equal((LogResult.Sent, "The Bear, S04E01–E04", waiting.At), (sent.Result, sent.Title, sent.At));
        Assert.Empty(sent.Details);
        Assert.Single(_h.Api.CallsTo("sendMessage"));
    }

    [Fact]
    public async Task Log_Digest_OneRowForTheWholeRelease()
    {
        _h.AddDestination(-1001, preset: PresetKind.NewMedia);
        _h.Settings.Update(null, c => c.Batching.DigestThreshold = 2);

        Ready(Movie("One", Movies), Movie("Two", Movies), Movie("Three", Movies));
        Assert.Equal(3, Rows().Count);
        _rig.RunFor(200);
        await _rig.DeliverAllAsync();

        var row = Assert.Single(Rows());
        Assert.Equal((LogResult.Sent, "media.digest", "3 new items"), (row.Result, row.EventKey, row.Title));
    }

    [Fact]
    public async Task Log_RetryThenSuccess_OneRowSentLate()
    {
        var d = _h.AddDestination(-1001, preset: PresetKind.AdminAlerts);
        _h.Api.On("sendMessage", _ => throw new HttpRequestException("connection timed out"));

        LoginFailed();
        Assert.Equal(LogResult.Waiting, Assert.Single(Rows()).Result);
        await _rig.DeliverAllAsync();

        var retrying = Assert.Single(Rows());
        Assert.Equal((LogResult.Waiting, 1, d.Id), (retrying.Result, retrying.Tries, retrying.DestinationId));
        Assert.Contains("connection timed out", retrying.Telegram, StringComparison.Ordinal);
        Assert.NotNull(retrying.RetryAt);
        Assert.NotNull(retrying.Advice);
        Assert.Equal(["Couldn't reach Telegram, trying again"], retrying.Details);

        _h.Api.OnSequence("sendMessage", SentOk);
        _h.Time.Advance(TimeSpan.FromSeconds(3));
        await _rig.DeliverAllAsync();

        var sent = Assert.Single(Rows());
        Assert.Equal((LogResult.SentLate, 2, retrying.At), (sent.Result, sent.Tries, sent.At));
        var why = Assert.Single(sent.Details);
        Assert.Contains("connection timed out", why, StringComparison.Ordinal);
        Assert.Contains("try 2", why, StringComparison.Ordinal);
        Assert.Null(sent.RetryAt);
        Assert.Equal(Now, _log.Query(new LogQuery()).LastSentAt);
    }

    [Fact]
    public async Task Log_PacedButNoRetry_IsSent()
    {
        _h.AddDestination(-1001, preset: PresetKind.AdminAlerts);

        LoginFailed();
        _h.Time.Advance(TimeSpan.FromMinutes(3));
        await _rig.DeliverAllAsync();

        var row = Assert.Single(Rows());
        Assert.Equal(LogResult.Sent, row.Result);
        Assert.Empty(row.Details);
    }

    [Fact]
    public async Task Log_LostThenRecovered_SameRowSentLate()
    {
        _h.AddDestination(-1001, preset: PresetKind.AdminAlerts);
        _h.Api.On("sendMessage", _ => FakeBotApi.Error(403, "Forbidden: bot was kicked from the supergroup chat"));
        LoginFailed();
        await _rig.DeliverAllAsync();
        Assert.Equal(LogResult.Failed, Assert.Single(Rows()).Result);

        _h.Api.OnSequence("sendMessage", SentOk);
        _h.Time.Advance(TimeSpan.FromMinutes(10));
        await _rig.DeliverAllAsync();

        var row = Assert.Single(Rows());
        Assert.Equal((LogResult.SentLate, 2), (row.Result, row.Tries));
        Assert.Null(row.HeldUntil);
        Assert.Equal(["Held while the destination had a problem (403 Forbidden: bot was kicked from the supergroup chat), sent 10 min after it was ready, once it was fixed"], row.Details);
    }

    [Fact]
    public async Task Log_QueuedBeforeTheDestinationWasLost_GetsTheHeldReason()
    {
        var d = _h.AddDestination(-1001, preset: PresetKind.AdminAlerts);
        _h.Api.On("sendMessage", _ => FakeBotApi.Error(403, "Forbidden: bot was kicked from the supergroup chat"));
        LoginFailed();
        LoginFailed("bob");
        Assert.All(Rows(), r => Assert.Equal(["Queued for Telegram"], r.Details));

        await _rig.DeliverAsync(e => e.Model!.Values["user"] == "alice");

        Assert.Equal(LogResult.Failed, Assert.Single(Rows(), r => r.Title == "alice").Result);
        var bob = Assert.Single(Rows(), r => r.Title == "bob");
        Assert.Equal(LogResult.Waiting, bob.Result);
        Assert.Equal(["Held: the bot was removed from this chat or blocked. It goes out once the destination works again"], bob.Details);
        Assert.Contains("Add the bot back to " + d.Title, bob.Advice, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Log_Disconnect_SettlesQueuedRowsAsSkipped(bool removeDestinations)
    {
        var d = _h.AddDestination(-1001, preset: PresetKind.AdminAlerts);
        LoginFailed();
        LoginFailed("bob");
        var tokens = new Jellyfin.Plugin.TgJelly.Bot.BotTokenService(new FakeClientFactory(_h.Api), _h.Secrets, _h.State, _h.Settings, new FakeSupervisorControl(), _h.Strings, _h.Time, NullLogger<Jellyfin.Plugin.TgJelly.Bot.BotTokenService>.Instance, _rig.Recorder);

        await tokens.DisconnectAsync(removeDestinations, TestContext.Current.CancellationToken);

        Assert.Equal(0, _rig.Pipeline.Outbox.Count);
        Assert.Equal(2, Rows().Count);
        Assert.All(Rows(), r =>
        {
            Assert.Equal((LogResult.Skipped, d.Id), (r.Result, r.DestinationId));
            Assert.Equal(["The bot was disconnected before this was sent"], r.Details);
            Assert.Null(r.Advice);
        });
    }

    [Fact]
    public void Log_RetriedWithoutARecordedError_CountsTriesFromTheEntry()
    {
        var d = _h.AddDestination(-1001);
        var entry = AnEntry(d);
        entry.Attempts = 2;

        _rig.Recorder.Delivered(entry, d, null);

        var row = Assert.Single(Rows());
        Assert.Equal((LogResult.SentLate, 3), (row.Result, row.Tries));
        Assert.StartsWith("Sent on try 3, ", Assert.Single(row.Details), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Log_HeldWithoutAnError_NoEmptyParentheses()
    {
        var d = _h.AddDestination(-1001, preset: PresetKind.AdminAlerts);
        _rig.Health.MarkProblem(d.Id, Jellyfin.Plugin.TgJelly.State.Documents.HealthStatus.Lost, string.Empty);
        LoginFailed();

        var held = Assert.Single(Rows());
        Assert.Null(held.Telegram);
        Assert.Equal(["Held: the bot was removed from this chat or blocked. It goes out once the destination works again"], held.Details);

        _h.Time.Advance(TimeSpan.FromMinutes(3));
        await _rig.DeliverAllAsync();

        var sent = Assert.Single(Rows());
        Assert.Equal(LogResult.SentLate, sent.Result);
        Assert.Null(sent.Telegram);
        Assert.Equal(["Held while the destination had a problem, sent 3 min after it was ready, once it was fixed"], sent.Details);
    }

    [Fact]
    public void Log_EmptyTelegramAnswer_IsNeverAnEmptyReason()
    {
        var d = _h.AddDestination(-1001);

        _rig.Recorder.Retrying(AnEntry(d), d, Jellyfin.Plugin.TgJelly.Bot.DeliveryErrorKind.Transport, string.Empty, Now + TimeSpan.FromSeconds(2));

        var row = Assert.Single(Rows());
        Assert.Null(row.Telegram);
        Assert.All(row.Details, why => Assert.DoesNotContain("()", why, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Log_LateEditRetries_RowStaysSent()
    {
        _h.AddDestination(-1001, preset: PresetKind.NewMedia);
        var (e1, e2) = (Episode(1), Episode(2));
        Ready(e1);
        _rig.RunFor(150);
        await _rig.DeliverAllAsync();
        Assert.Equal(LogResult.Sent, Assert.Single(Rows()).Result);
        _h.Api.On("editMessageText", _ => throw new HttpRequestException("connection reset"));

        Ready(e2);
        Assert.Equal((LogResult.Sent, "The Bear, S04E01–E02"), (Assert.Single(Rows()).Result, Assert.Single(Rows()).Title));
        await _rig.DeliverAllAsync();

        var row = Assert.Single(Rows());
        Assert.Equal(LogResult.Sent, row.Result);
        var why = Assert.Single(row.Details);
        Assert.StartsWith("Updating this message failed, trying again: ", why, StringComparison.Ordinal);
        Assert.Contains("connection reset", why, StringComparison.Ordinal);
        Assert.Contains(row, _log.Query(new LogQuery { ProblemsOnly = true }).Items);

        _h.Api.OnSequence("editMessageText", SentOk);
        _h.Time.Advance(TimeSpan.FromSeconds(3));
        await _rig.DeliverAllAsync();

        row = Assert.Single(Rows());
        Assert.Equal(LogResult.Sent, row.Result);
        Assert.Empty(row.Details);
        Assert.Null(row.Advice);
    }

    [Fact]
    public async Task Log_LateEditDropped_RowStaysSentWithDetail()
    {
        _h.AddDestination(-1001, preset: PresetKind.NewMedia);
        var (e1, e2) = (Episode(1), Episode(2));
        Ready(e1);
        _rig.RunFor(150);
        await _rig.DeliverAllAsync();
        _h.Api.On("editMessageText", _ => FakeBotApi.Error(400, "Bad Request: wrong parameter action in request"));

        Ready(e2);
        await _rig.DeliverAllAsync();

        Assert.Equal(0, _rig.Pipeline.Outbox.Count);
        var row = Assert.Single(Rows());
        Assert.Equal(LogResult.Sent, row.Result);
        Assert.Equal(["Updating this message failed, so the chat still shows the earlier version: Bad Request: wrong parameter action in request"], row.Details);
        Assert.False(string.IsNullOrWhiteSpace(row.Advice));
    }

    [Fact]
    public void Log_OverTheMediaCap_OldestRowFailedWithReason()
    {
        var d = _h.AddDestination(-1001, preset: PresetKind.NewMedia);
        for (var i = 0; i <= Jellyfin.Plugin.TgJelly.Delivery.Outbox.MediaCap; i++)
        {
            var id = "m" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _log.Upsert(new LogEntry { Id = id, At = Now, UpdatedAt = Now, EventKey = "media.movie", Title = id, DestinationId = d.Id, Result = LogResult.Waiting, Details = ["Queued for Telegram"] });
            _rig.Pipeline.Outbox.Enqueue(new Jellyfin.Plugin.TgJelly.State.Documents.OutboxEntry
            {
                EntryId = id, LogId = id, DestinationId = d.Id, EventKey = "media.movie", QueuedAt = Now + TimeSpan.FromMilliseconds(i),
            });
        }

        var dropped = Assert.Single(Rows(), r => r.Id == "m0");
        Assert.Equal(LogResult.Failed, dropped.Result);
        Assert.Equal(["More than 500 messages were waiting for this destination, so this one, the oldest, was dropped"], dropped.Details);
        Assert.False(string.IsNullOrWhiteSpace(dropped.Advice));
        Assert.Equal(LogResult.Waiting, Assert.Single(Rows(), r => r.Id == "m1").Result);
    }

    [Fact]
    public void Log_DestinationRemovedWithAQueuedMessage_RowSkipped()
    {
        var d = _h.AddDestination(-1001, preset: PresetKind.AdminAlerts);
        LoginFailed();

        _rig.Pipeline.Outbox.RemoveDestination(d.Id);

        var row = Assert.Single(Rows());
        Assert.Equal((LogResult.Skipped, d.Id), (row.Result, row.DestinationId));
        Assert.Equal(["The destination was removed before the message went out"], row.Details);
    }

    [Fact]
    public void Log_QueuedPast24Hours_RowFailedAsExpired()
    {
        _h.AddDestination(-1001, preset: PresetKind.AdminAlerts);
        LoginFailed();
        _h.Time.Advance(TimeSpan.FromHours(25));

        Assert.Equal(1, _rig.Pipeline.Outbox.PurgeExpired(Now));

        var row = Assert.Single(Rows());
        Assert.Equal(LogResult.Failed, row.Result);
        Assert.Equal(["It couldn't be delivered within 24 hours, so it was dropped"], row.Details);
        Assert.Equal(_h.Strings.Get("web.log.advice.expiredPlain"), row.Advice);
    }

    [Fact]
    public void Log_StaleWaitingRow_SweepSaysTgJellyLostTrack()
    {
        _log.Upsert(new LogEntry { Id = "lost", At = Now - TimeSpan.FromHours(31), UpdatedAt = Now - TimeSpan.FromHours(31), EventKey = "media.movie", Title = "Arrival", Result = LogResult.Waiting });

        _rig.Pipeline.Pipeline.RunOnce();

        var row = Assert.Single(Rows());
        Assert.Equal(LogResult.Failed, row.Result);
        Assert.Equal(["TgJelly lost track of this message: it was still waiting after 30 hours, so it was most likely never sent"], row.Details);
    }

    [Fact]
    public async Task Log_BrokenLog_StillQueuesAndSends()
    {
        _h.AddDestination(-1001);
        _log.Broken = true;

        Ready(Movie("Arrival", Movies));
        _rig.RunFor(150);
        LoginFailed();
        await _rig.DeliverAllAsync();

        Assert.Equal(2, _h.Api.CallsTo("sendMessage").Count());
        Assert.Equal(0, _rig.Pipeline.Outbox.Count);
        Assert.Equal(0, _rig.Pipeline.Logger.Count(LogLevel.Error));
        Assert.Empty(_h.State.LastErrorsSnapshot());
    }

    [Fact]
    public void Log_FailedWithATelegramAnswer_SaysTelegramRefusedIt()
    {
        var d = _h.AddDestination(-1001);

        _rig.Recorder.Failed(AnEntry(d), d, new Jellyfin.Plugin.TgJelly.Bot.DeliveryError(Jellyfin.Plugin.TgJelly.Bot.DeliveryErrorKind.Other, "Bad Request: x"), "400 Bad Request: x", fromTelegram: true);

        Assert.Equal(["Telegram refused it: Bad Request: x"], Assert.Single(Rows()).Details);
    }

    [Fact]
    public void Log_FailedWithoutATelegramAnswer_SaysTgJellyCouldntSendIt()
    {
        var d = _h.AddDestination(-1001);

        _rig.Recorder.Failed(AnEntry(d), d, new Jellyfin.Plugin.TgJelly.Bot.DeliveryError(Jellyfin.Plugin.TgJelly.Bot.DeliveryErrorKind.Other, "Object reference not set to an instance of an object."), "Object reference not set to an instance of an object.", fromTelegram: false);

        var row = Assert.Single(Rows());
        Assert.Equal(["TgJelly couldn't send it: Object reference not set to an instance of an object."], row.Details);
        Assert.Contains("Copy diagnostics", row.Advice, StringComparison.Ordinal);
        Assert.DoesNotContain("Telegram refused", row.Advice, StringComparison.Ordinal);
    }

    [Fact]
    public void Log_WaitingForMetadata_DashRowMovesOn()
    {
        var d = _h.AddDestination(-1001, preset: PresetKind.NewMedia);
        var movie = Movie("Arrival", Movies);

        _rig.Pipeline.Added(movie);
        _rig.Pipeline.Pipeline.RunOnce();

        var pending = Assert.Single(Rows());
        Assert.Equal((LogResult.Waiting, (string?)null, "Arrival", "media.movie"), (pending.Result, pending.DestinationId, pending.Title, pending.EventKey));
        Assert.Equal(["Waiting for Jellyfin to finish reading this item's details (up to 15 min)"], pending.Details);

        _rig.Pipeline.Updated(movie);
        _rig.Pipeline.Pipeline.RunOnce();

        var held = Assert.Single(Rows());
        Assert.Equal((LogResult.Waiting, d.Id), (held.Result, held.DestinationId));
    }

    [Fact]
    public async Task Log_Dropped_FailedWithTelegramDescription()
    {
        var d = _h.AddDestination(-1001, preset: PresetKind.AdminAlerts);
        _h.Api.On("sendMessage", _ => FakeBotApi.Error(400, "Bad Request: wrong parameter action in request"));

        LoginFailed();
        await _rig.DeliverAllAsync();

        Assert.Equal(0, _rig.Pipeline.Outbox.Count);
        var row = Assert.Single(Rows());
        Assert.Equal((LogResult.Failed, d.Id, 1), (row.Result, row.DestinationId, row.Tries));
        Assert.Equal("400 Bad Request: wrong parameter action in request", row.Telegram);
        Assert.Equal(["Telegram refused it: Bad Request: wrong parameter action in request"], row.Details);
        Assert.False(string.IsNullOrWhiteSpace(row.Advice));
        Assert.Null(row.RetryAt);
    }

    [Fact]
    public async Task Log_Lost_FailedWithAdviceToAddTheBotBack()
    {
        var d = _h.AddDestination(-1001, preset: PresetKind.AdminAlerts);
        _h.Api.On("sendMessage", _ => FakeBotApi.Error(403, "Forbidden: bot was kicked from the supergroup chat"));

        LoginFailed();
        await _rig.DeliverAllAsync();

        var row = Assert.Single(Rows());
        Assert.Equal(LogResult.Failed, row.Result);
        Assert.Equal("403 Forbidden: bot was kicked from the supergroup chat", row.Telegram);
        Assert.Equal(["Telegram refused it: the bot was removed from the chat or blocked"], row.Details);
        Assert.Contains("Add the bot back to " + d.Title, row.Advice, StringComparison.Ordinal);
        Assert.Contains("Check again", row.Advice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Log_HeldForALostDestination_SaysWhyAndWhatToDo()
    {
        var d = _h.AddDestination(-1001, preset: PresetKind.AdminAlerts);
        _h.Api.On("sendMessage", _ => FakeBotApi.Error(403, "Forbidden: bot was kicked from the supergroup chat"));
        LoginFailed();
        await _rig.DeliverAllAsync();

        _rig.Pipeline.Queues.Publish("security.login_failed", new System.Collections.Generic.Dictionary<string, string> { ["user"] = "bob", ["user_key"] = "BOB", ["ip"] = "2.2.2.2" });
        _rig.Pipeline.Pipeline.RunOnce();

        var held = Assert.Single(Rows(), r => r.Title == "bob");
        Assert.Equal(LogResult.Waiting, held.Result);
        Assert.Equal(["Held: the bot was removed from this chat or blocked. It goes out once the destination works again"], held.Details);
        Assert.Contains("Add the bot back to " + d.Title, held.Advice, StringComparison.Ordinal);
        Assert.Contains(held, _log.Query(new LogQuery { ProblemsOnly = true }).Items);
    }

    [Fact]
    public async Task Log_NeedsPermissions_NamesTheMissingRight()
    {
        _h.AddDestination(-1001, kind: ChatKind.Channel, preset: PresetKind.AdminAlerts);
        _h.Api.On("sendMessage", _ => FakeBotApi.Error(400, "Bad Request: need administrator rights in the channel chat"));

        LoginFailed();
        await _rig.DeliverAllAsync();

        var row = Assert.Single(Rows());
        Assert.Equal(LogResult.Failed, row.Result);
        Assert.Contains("Post messages", row.Advice, StringComparison.Ordinal);
        Assert.Contains("Post messages", Assert.Single(row.Details), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Log_TemplateFallback_SentWithBuiltIn()
    {
        _h.AddDestination(-1001, preset: PresetKind.NewMedia);
        _h.Settings.Update(null, c => c.Templates = [new TemplateOverride { EventKey = "media.movie", Enabled = true, Text = "<b>{{ title }}" }]);

        Ready(Movie("Arrival", Movies));
        _rig.RunFor(150);
        await _rig.DeliverAllAsync();

        var row = Assert.Single(Rows());
        Assert.Equal(LogResult.SentWithBuiltIn, row.Result);
        var why = Assert.Single(row.Details);
        Assert.StartsWith("Your edited template failed (", why, StringComparison.Ordinal);
        Assert.EndsWith("), so the built-in one was used", why, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(row.Advice));
    }

    [Fact]
    public async Task Log_PhotosBlockedAfterARetry_LabelIsMostSerious_WhyListsBoth()
    {
        _h.AddDestination(-1001, preset: PresetKind.NewMedia);
        var photoCalls = 0;
        _h.Api.On("sendPhoto", _ => ++photoCalls == 1 ? throw new HttpRequestException("connection reset") : FakeBotApi.Error(400, "Bad Request: not enough rights to send photos to the chat"));
        var poster = System.IO.Path.Combine(_h.Dir.Path, "poster.jpg");
        System.IO.File.WriteAllBytes(poster, [1, 2, 3]);
        var movie = _rig.Library.Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Movie, Name = "Arrival", Primary = new ImageRef(poster, true, DateTime.UnixEpoch) }, Movies);

        Ready(movie);
        _rig.RunFor(150);
        await _rig.DeliverAllAsync();
        _h.Time.Advance(TimeSpan.FromSeconds(3));
        await _rig.DeliverAllAsync();

        var row = Assert.Single(Rows());
        Assert.Equal(LogResult.SentWithoutPhoto, row.Result);
        Assert.Equal(2, row.Details.Count);
        Assert.Equal("This chat doesn't allow photos, so the text was sent alone", row.Details[0]);
        Assert.StartsWith("Couldn't get through to Telegram for ", row.Details[1], StringComparison.Ordinal);
        Assert.Contains("connection reset", row.Details[1], StringComparison.Ordinal);
        Assert.EndsWith("sent on try 2", row.Details[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Log_TestMessage_NotLogged()
    {
        var d = _h.AddDestination(-1001);
        var tester = new DestinationTester(_h.Connection, _rig.Health, _h.Strings, NullLogger<DestinationTester>.Instance);

        var result = await tester.SendTestAsync(d, TestContext.Current.CancellationToken);

        Assert.True(result.Sent);
        Assert.Empty(Rows());
    }

    [Fact]
    public void Log_RemovedDestination_HeldGroupMakesNoRowAndNoError()
    {
        var d = _h.AddDestination(-1001, preset: PresetKind.NewMedia);
        Ready(Movie("Arrival", Movies));
        Assert.Equal(LogResult.Waiting, Single(d.Id).Result);

        _h.Settings.Update(null, c => c.Destinations = []);
        _rig.RunFor(150);

        var row = Assert.Single(Rows());
        Assert.Equal((LogResult.Skipped, d.Id), (row.Result, row.DestinationId));
        Assert.Equal(["The destination was removed before the message went out"], row.Details);
        Assert.Equal(0, _rig.Pipeline.Logger.Count(LogLevel.Error));
        Assert.Empty(_h.State.LastErrorsSnapshot());
    }

    [Fact]
    public void Log_ItemRemovedBeforeReady_DashRow()
    {
        _h.AddDestination(-1001, preset: PresetKind.NewMedia);
        var movie = Movie("Arrival", Movies);
        _rig.Pipeline.Added(movie);
        _rig.Pipeline.Pipeline.RunOnce();
        _rig.Library.Items.Remove(movie.Id);
        _rig.Pipeline.Updated(movie);
        _rig.Pipeline.Pipeline.RunOnce();

        var row = Assert.Single(Rows());
        Assert.Equal((LogResult.Skipped, (string?)null), (row.Result, row.DestinationId));
        Assert.Equal(["It was removed from Jellyfin before its details were ready, so there was nothing to announce"], row.Details);
    }

    [Fact]
    public async Task Log_HeldFailedRow_GoesOutUntilTheOutboxDropsIt()
    {
        _h.AddDestination(-1001, preset: PresetKind.AdminAlerts);
        _h.Api.On("sendMessage", _ => FakeBotApi.Error(403, "Forbidden: bot was kicked from the supergroup chat"));
        LoginFailed();
        var queuedAt = Assert.Single(_rig.Pipeline.Outbox.Snapshot()).QueuedAt;

        await _rig.DeliverAllAsync();

        // The entry stays queued (held) until the destination works again: the row says until when, not "never again".
        Assert.Equal(1, _rig.Pipeline.Outbox.Count);
        var row = Assert.Single(Rows());
        Assert.Equal(LogResult.Failed, row.Result);
        Assert.Equal(queuedAt + Jellyfin.Plugin.TgJelly.Delivery.Outbox.Ttl, row.HeldUntil);
        Assert.Null(row.RetryAt);
    }

    [Fact]
    public async Task Log_HeldFailedRowExpires_GetsTheExpiredAdvice()
    {
        _h.AddDestination(-1001, preset: PresetKind.AdminAlerts);
        _h.Api.On("sendMessage", _ => FakeBotApi.Error(403, "Forbidden: bot was kicked from the supergroup chat"));
        LoginFailed();
        await _rig.DeliverAllAsync();
        _h.Time.Advance(TimeSpan.FromHours(25));

        Assert.Equal(1, _rig.Pipeline.Outbox.PurgeExpired(Now));

        var row = Assert.Single(Rows());
        Assert.Equal(LogResult.Failed, row.Result);
        Assert.Null(row.HeldUntil);
        Assert.Equal("It couldn't be delivered within 24 hours, so it was dropped", row.Details[0]);
        Assert.Equal(_h.Strings.Get("web.log.advice.expired"), row.Advice);
        Assert.DoesNotContain("wait up to 24 hours", row.Advice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Log_HeldFailedRow_DestinationRemoved_RowSkipped()
    {
        var d = _h.AddDestination(-1001, preset: PresetKind.AdminAlerts);
        _h.Api.On("sendMessage", _ => FakeBotApi.Error(403, "Forbidden: bot was kicked from the supergroup chat"));
        LoginFailed();
        await _rig.DeliverAllAsync();

        _rig.Pipeline.Outbox.RemoveDestination(d.Id);

        var row = Assert.Single(Rows());
        Assert.Equal((LogResult.Skipped, (DateTimeOffset?)null), (row.Result, row.HeldUntil));
        Assert.Equal(["The destination was removed before the message went out"], row.Details);
        Assert.Null(row.Advice);
    }

    [Fact]
    public void Log_ExpiredWithoutATelegramAnswer_AdviceDoesNotPointAtOne()
    {
        // Queued, then the destination was paused: nothing went to Telegram, so there is no answer to point at.
        _h.AddDestination(-1001, preset: PresetKind.AdminAlerts);
        LoginFailed();
        _h.Settings.Update(null, c => c.Destinations[0].Enabled = false);
        _h.Time.Advance(TimeSpan.FromHours(25));

        Assert.Equal(1, _rig.Pipeline.Outbox.PurgeExpired(Now));

        var row = Assert.Single(Rows());
        Assert.Null(row.Telegram);
        Assert.Equal(_h.Strings.Get("web.log.advice.expiredPlain"), row.Advice);
        Assert.DoesNotContain("Telegram's answer", row.Advice, StringComparison.Ordinal);
        Assert.Contains("paused", row.Advice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Log_RateLimited_SaysTelegramAskedToSlowDown_AndCountsNoTry()
    {
        _h.AddDestination(-1001, preset: PresetKind.AdminAlerts);
        _h.Api.OnSequence("sendMessage", FakeBotApi.Error(429, "Too Many Requests: retry after 7", """{"retry_after":7}"""), SentOk);
        LoginFailed();

        await _rig.DeliverAllAsync();

        var waiting = Assert.Single(Rows());
        Assert.Equal((LogResult.Waiting, 0), (waiting.Result, waiting.Tries));
        Assert.Equal(["Telegram asked TgJelly to slow down, waiting before sending"], waiting.Details);

        _h.Time.Advance(TimeSpan.FromSeconds(7));
        await _rig.DeliverAllAsync();

        var sent = Assert.Single(Rows());
        Assert.Equal((LogResult.SentLate, 1), (sent.Result, sent.Tries));
        var why = Assert.Single(sent.Details);
        Assert.StartsWith("Telegram asked TgJelly to slow down (429 Too Many Requests: retry after 7), sent after waiting ", why, StringComparison.Ordinal);
        Assert.DoesNotContain("Couldn't get through", why, StringComparison.Ordinal);
        Assert.Equal(_h.Strings.Get("web.log.advice.RateLimited"), sent.Advice);
    }

    public void Dispose()
    {
        _log.Dispose();
        _h.Dispose();
    }

    private System.Collections.Generic.IReadOnlyList<LogEntry> Rows() => _log.Query(new LogQuery { Limit = 1000 }).Items;

    private LogEntry Single(string destinationId) => Assert.Single(Rows(), r => r.DestinationId == destinationId);

    private MediaItem Movie(string name, LibraryRef library)
        => _rig.Library.Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Movie, Name = name }, library);

    private void Ready(params MediaItem[] items)
    {
        foreach (var item in items)
        {
            _rig.Pipeline.Added(item);
            _rig.Pipeline.Updated(item);
        }

        _rig.Pipeline.Pipeline.RunOnce();
    }

    private const string SentOk = """{"ok":true,"result":{"message_id":5,"date":1790000000,"chat":{"id":-1001,"type":"supergroup"},"text":"x"}}""";

    private Jellyfin.Plugin.TgJelly.State.Documents.OutboxEntry AnEntry(Destination d) => new()
    {
        EntryId = "e1", LogId = "e1", DestinationId = d.Id, EventKey = "security.login_failed", QueuedAt = Now,
    };

    private MediaItem Episode(int number) => _rig.Library.Add(
        new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Episode, Name = "E" + number, SeriesId = BearSeries, SeriesName = "The Bear", SeasonId = BearSeason, SeasonNumber = 4, EpisodeNumber = number },
        Shows);

    private void LoginFailed(string user = "alice")
    {
        _rig.Pipeline.Queues.Publish("security.login_failed", new System.Collections.Generic.Dictionary<string, string> { ["user"] = user, ["user_key"] = user.ToUpperInvariant(), ["ip"] = "1.1.1.1" });
        _rig.Pipeline.Pipeline.RunOnce();
    }
}
