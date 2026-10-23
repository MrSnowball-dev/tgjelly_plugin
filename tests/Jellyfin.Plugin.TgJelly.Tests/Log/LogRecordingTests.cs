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

        _h.Api.OnSequence("sendMessage", FakeBotApi.Ok("""{"message_id":5,"date":1790000000,"chat":{"id":-1001,"type":"supergroup"},"text":"x"}"""));
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
    public async Task Log_QueuedLongerThanAMinute_SentLate()
    {
        _h.AddDestination(-1001, preset: PresetKind.AdminAlerts);

        LoginFailed();
        _h.Time.Advance(TimeSpan.FromMinutes(3));
        await _rig.DeliverAllAsync();

        var row = Assert.Single(Rows());
        Assert.Equal(LogResult.SentLate, row.Result);
        Assert.Equal(["Sent 3 min after it was ready"], row.Details);
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
    public async Task Log_PhotosBlockedAndLate_LabelIsMostSerious_WhyListsBoth()
    {
        _h.AddDestination(-1001, preset: PresetKind.NewMedia);
        _h.Api.On("sendPhoto", _ => FakeBotApi.Error(400, "Bad Request: not enough rights to send photos to the chat"));
        var poster = System.IO.Path.Combine(_h.Dir.Path, "poster.jpg");
        System.IO.File.WriteAllBytes(poster, [1, 2, 3]);
        var movie = _rig.Library.Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Movie, Name = "Arrival", Primary = new ImageRef(poster, true, DateTime.UnixEpoch) }, Movies);

        Ready(movie);
        _rig.RunFor(150);
        _h.Time.Advance(TimeSpan.FromMinutes(2));
        await _rig.DeliverAllAsync();

        var row = Assert.Single(Rows());
        Assert.Equal(LogResult.SentWithoutPhoto, row.Result);
        Assert.Equal(["This chat doesn't allow photos, so the text was sent alone", "Sent 2 min after it was ready"], row.Details);
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

    private void LoginFailed()
    {
        _rig.Pipeline.Queues.Publish("security.login_failed", new System.Collections.Generic.Dictionary<string, string> { ["user"] = "alice", ["user_key"] = "ALICE", ["ip"] = "1.1.1.1" });
        _rig.Pipeline.Pipeline.RunOnce();
    }
}
