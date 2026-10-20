using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Delivery;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Delivery;

public sealed class OutboxDeliveryTests : IDisposable
{
    private readonly BotTestHarness _h = new();
    private readonly Outbox _outbox;
    private readonly PacingGate _pacing;
    private readonly HealthService _health;
    private readonly OutboxDelivery _delivery;
    private readonly Formatter _formatter;

    public OutboxDeliveryTests()
    {
        _h.Connect();
        _outbox = new Outbox(_h.State, _h.Strings, _h.Time);
        _pacing = new PacingGate(_h.Time);
        var gate = new InstanceGate(_h.Paths, _h.Time, NullLogger<InstanceGate>.Instance);
        _h.Track(gate);
        _health = new HealthService(gate, _h.Connection, new DestinationChecker(_h.Connection, _h.Strings), _h.Settings, _h.State, _h.Time, NullLogger<HealthService>.Instance);
        _h.Track(_health);
        _formatter = new Formatter(_h.Strings);
        _delivery = new OutboxDelivery(_h.Connection, _outbox, _health, _h.Settings, _h.State, _pacing, _formatter, _h.Time, new DestinationMigration(_h.Settings, _outbox, _health), new Jellyfin.Plugin.TgJelly.Templates.TemplateFlags(_h.State, _h.Settings, _h.Time), NullLogger<OutboxDelivery>.Instance)
        {
            Random = () => 0.5,
        };
    }

    private DateTimeOffset Now => _h.Time.GetUtcNow();

    [Fact]
    public async Task Text_IsSentAsHtml_AndRecordedUnderItsSentKey()
    {
        var d = _h.AddDestination(-1001);
        var entry = Entry(d, "<b>hi</b>", sentKey: "g1");

        await Deliver(entry, d);

        var body = Assert.Single(_h.Api.CallsTo("sendMessage")).Body;
        Assert.Equal(("<b>hi</b>", "html"), ((string)body["text"]!, ((string)body["parse_mode"]!).ToLowerInvariant()));
        Assert.Equal(0, _outbox.Count);
        Assert.True(_outbox.TryGetSent(d.Id, "g1", out var sent));
        Assert.Equal(SentKind.Text, sent.Kind);
        Assert.Equal(Now + TimeSpan.FromSeconds(1), _pacing.NextAllowed(-1001, true, Now));
    }

    [Fact]
    public async Task Poster_IsUploaded_ThenItsFileIdIsReusedForTheNextDestination()
    {
        var d1 = _h.AddDestination(-1001);
        var d2 = _h.AddDestination(-1002);
        var poster = Poster();

        await Deliver(Entry(d1, "cap", poster: poster), d1);
        await Deliver(Entry(d2, "cap", poster: poster), d2);

        var calls = _h.Api.CallsTo("sendPhoto").ToArray();
        Assert.Equal("file:poster.jpg", (string)calls[0].Body["0"]!);
        Assert.Equal("file-1", (string)calls[1].Body["photo"]!);
        Assert.Equal("cap", (string)calls[0].Body["caption"]!);
    }

    [Fact]
    public async Task MissingPosterFile_SendsTheTextLayout()
    {
        var d = _h.AddDestination(-1001);

        await Deliver(Entry(d, "text layout", poster: Path.Combine(_h.Dir.Path, "missing.jpg")), d);

        Assert.Empty(_h.Api.CallsTo("sendPhoto"));
        Assert.Equal("text layout", (string)Assert.Single(_h.Api.CallsTo("sendMessage")).Body["text"]!);
    }

    [Fact]
    public async Task Edit_UsesCaptionForPhotosAndTextForTextMessages()
    {
        var d = _h.AddDestination(-1001);
        await Deliver(Entry(d, "cap", poster: Poster(), sentKey: "photo"), d);
        await Deliver(Entry(d, "text", sentKey: "text"), d);

        await Deliver(Edit(d, "photo", "new caption"), d);
        await Deliver(Edit(d, "text", "new text"), d);

        Assert.Equal("new caption", (string)Assert.Single(_h.Api.CallsTo("editMessageCaption")).Body["caption"]!);
        Assert.Equal("new text", (string)Assert.Single(_h.Api.CallsTo("editMessageText")).Body["text"]!);
        Assert.True(_outbox.TryGetSent(d.Id, "photo", out var edited));
        Assert.Equal(Now, edited.LastEditAt);
    }

    [Fact]
    public async Task Edit_WithAnUnknownTarget_IsSentAsANewMessage()
    {
        var d = _h.AddDestination(-1001);

        await Deliver(Edit(d, "gone", "fresh"), d);

        Assert.Equal("fresh", (string)Assert.Single(_h.Api.CallsTo("sendMessage")).Body["text"]!);
        Assert.True(_outbox.TryGetSent(d.Id, "gone", out _));
    }

    [Fact]
    public async Task NotModified_CountsAsSuccess()
    {
        var d = _h.AddDestination(-1001);
        await Deliver(Entry(d, "text", sentKey: "t"), d);
        _h.Api.On("editMessageText", _ => FakeBotApi.Error(400, "Bad Request: message is not modified: specified new message content and reply markup are exactly the same as a current content and reply markup of the message"));

        await Deliver(Edit(d, "t", "text"), d);

        Assert.Equal(0, _outbox.Count);
        Assert.Empty(_h.State.LastErrorsSnapshot());
    }

    [Fact]
    public async Task PhotosBlocked_ResendsAsText_AndFlagsTheDestination()
    {
        var d = _h.AddDestination(-1001);
        _h.Api.On("sendPhoto", _ => FakeBotApi.Error(400, "Bad Request: not enough rights to send photos to the chat"));

        await Deliver(Entry(d, "text layout", poster: Poster()), d);

        Assert.Equal("text layout", (string)Assert.Single(_h.Api.CallsTo("sendMessage")).Body["text"]!);
        Assert.True(_health.Get(d.Id).PhotosBlocked);
        Assert.Equal(0, _outbox.Count);
    }

    [Fact]
    public async Task Lost_HoldsTheEntry_MarksHealth_AndLeavesEnabledAlone()
    {
        var d = _h.AddDestination(-1001);
        _h.Api.On("sendMessage", _ => FakeBotApi.Error(403, "Forbidden: bot was kicked from the supergroup chat"));

        await Deliver(Entry(d, "x"), d);

        Assert.Equal(1, _outbox.Count);
        Assert.Equal(HealthStatus.Lost, _health.Get(d.Id).Status);
        Assert.True(_h.Settings.Current.Destinations.Single().Enabled);
    }

    [Fact]
    public async Task Migration_RewritesTheChatId_AndResendsOnce()
    {
        var d = _h.AddDestination(-1001, ChatKind.Group);
        _h.Api.OnSequence(
            "sendMessage",
            FakeBotApi.Error(400, "Bad Request: group chat was upgraded to a supergroup chat", """{"migrate_to_chat_id":-1009}"""),
            FakeBotApi.Ok("""{"message_id":5,"date":1790000000,"chat":{"id":-1009,"type":"supergroup"},"text":"x"}"""));

        await Deliver(Entry(d, "x"), d);

        var migrated = _h.Settings.Current.Destinations.Single();
        Assert.Equal((-1009L, ChatKind.Supergroup), (migrated.ChatId, migrated.Kind));
        Assert.Equal(-1009, (long)_h.Api.CallsTo("sendMessage").ElementAt(1).Body["chat_id"]!);
        Assert.Equal(0, _outbox.Count);
    }

    [Fact]
    public async Task Migration_IntoAnExistingDestination_MergesAndRepoints()
    {
        var old = _h.AddDestination(-1001, ChatKind.Group, preset: PresetKind.NewMedia);
        var existing = _h.AddDestination(-1009, preset: PresetKind.AdminAlerts);
        _health.MarkProblem(old.Id, HealthStatus.Ok, string.Empty);
        _h.Api.OnSequence(
            "sendMessage",
            FakeBotApi.Error(400, "Bad Request: group chat was upgraded to a supergroup chat", """{"migrate_to_chat_id":-1009}"""),
            FakeBotApi.Ok("""{"message_id":5,"date":1790000000,"chat":{"id":-1009,"type":"supergroup"},"text":"x"}"""));
        _outbox.Enqueue(Entry(old, "queued behind"));

        await Deliver(Entry(old, "x"), old);

        var remaining = Assert.Single(_h.Settings.Current.Destinations);
        Assert.Equal(existing.Id, remaining.Id);
        Assert.Equal(PresetKind.Custom, remaining.Preset);
        Assert.Contains("media.movie", remaining.Events);
        Assert.Contains("server.started", remaining.Events);
        Assert.Equal(existing.Id, Assert.Single(_outbox.Snapshot()).DestinationId);
        Assert.False(_h.State.Read(s => s.Health.ContainsKey(old.Id)));
    }

    [Fact]
    public async Task ParseError_ResendsAsPlainText()
    {
        var d = _h.AddDestination(-1001);
        _h.Api.OnSequence("sendMessage", FakeBotApi.Error(400, "Bad Request: can't parse entities: Unsupported start tag \"x\" at byte offset 0"), FakeBotApi.Ok("""{"message_id":5,"date":1790000000,"chat":{"id":-1001,"type":"supergroup"},"text":"x"}"""));

        await Deliver(Entry(d, "<b>bold</b> &amp; more"), d);

        var retry = _h.Api.CallsTo("sendMessage").ElementAt(1).Body;
        Assert.Equal("bold & more", (string)retry["text"]!);
        Assert.False(retry.ContainsKey("parse_mode"));
    }

    [Fact]
    public async Task ParseError_WithTemplate_FlagsIt()
    {
        var d = _h.AddDestination(-1001);
        _h.Settings.Update(null, c => c.Templates = [new TemplateOverride { EventKey = "media.movie", Enabled = true, Text = "<b>x" }]);
        _h.Api.OnSequence("sendMessage", FakeBotApi.Error(400, "Bad Request: can't parse entities: Unsupported start tag \"x\" at byte offset 0"), FakeBotApi.Ok("""{"message_id":5,"date":1790000000,"chat":{"id":-1001,"type":"supergroup"},"text":"x"}"""));
        var entry = Entry(d, "<x>");
        entry.Message.TemplateKey = "media.movie";

        await Deliver(entry, d);

        var flag = new Jellyfin.Plugin.TgJelly.Templates.TemplateFlags(_h.State, _h.Settings, _h.Time).Current("media.movie")!;
        Assert.Equal("error", flag.Kind);
        Assert.Contains("can't parse entities", flag.Error, StringComparison.Ordinal);
        Assert.False(_h.Api.CallsTo("sendMessage").ElementAt(1).Body.ContainsKey("parse_mode"));
    }

    [Fact]
    public async Task BadButton_ResendsWithoutButtons_AndFlagsThePublicUrl()
    {
        var d = _h.AddDestination(-1001);
        _h.Settings.Update(null, c => c.PublicUrl = "https://jf.example.com");
        _h.Api.OnSequence("sendMessage", FakeBotApi.Error(400, "Bad Request: BUTTON_URL_INVALID"), FakeBotApi.Ok("""{"message_id":5,"date":1790000000,"chat":{"id":-1001,"type":"supergroup"},"text":"x"}"""));

        await Deliver(Entry(d, "x", buttons: true), d);

        var calls = _h.Api.CallsTo("sendMessage").ToArray();
        Assert.True(calls[0].Body.ContainsKey("reply_markup"));
        Assert.False(calls[1].Body.ContainsKey("reply_markup"));
        Assert.Equal("https://jf.example.com", _h.State.Read(s => s.Status.PublicUrlFlag!.Url));
    }

    [Fact]
    public async Task ASuccessfulSendWithButtons_ClearsThePublicUrlFlag()
    {
        var d = _h.AddDestination(-1001);
        _h.State.Update(s => s.Status.PublicUrlFlag = new PublicUrlFlag { Url = "http://jellyfin", At = Now });

        await Deliver(Entry(d, "x", buttons: true), d);

        Assert.Null(_h.State.Read(s => s.Status.PublicUrlFlag));
    }

    [Fact]
    public async Task TooLong_TruncatesTo90Percent_AndResendsOnlyOnce()
    {
        var d = _h.AddDestination(-1001);
        _h.Api.On("sendMessage", _ => FakeBotApi.Error(400, "Bad Request: message is too long"));

        await Deliver(Entry(d, new string('a', 5000)), d);

        var calls = _h.Api.CallsTo("sendMessage").ToArray();
        Assert.Equal(2, calls.Length);
        Assert.True(((string)calls[1].Body["text"]!).Length <= 3686 + 1);
        Assert.Equal(0, _outbox.Count);
        Assert.Contains(_h.State.LastErrorsSnapshot(), e => e.Source == "outbox");
    }

    [Fact]
    public async Task RateLimited_PausesTheChat_WithoutCountingAnAttempt()
    {
        var d = _h.AddDestination(-1001);
        _h.Api.On("sendMessage", _ => FakeBotApi.Error(429, "Too Many Requests: retry after 7", """{"retry_after":7}"""));

        await Deliver(Entry(d, "x"), d);

        var queued = Assert.Single(_outbox.Snapshot());
        Assert.Equal((Now + TimeSpan.FromSeconds(7), 0), (queued.NotBefore!.Value, queued.Attempts));
        Assert.Equal(Now + TimeSpan.FromSeconds(7), _pacing.NextAllowed(-1001, true, Now));
    }

    [Fact]
    public async Task Transport_RetriesWithBackoff()
    {
        var d = _h.AddDestination(-1001);
        _h.Api.On("sendMessage", _ => throw new HttpRequestException("network down"));

        await Deliver(Entry(d, "x"), d);

        var queued = Assert.Single(_outbox.Snapshot());
        Assert.Equal((Now + TimeSpan.FromSeconds(2), 1), (queued.NotBefore!.Value, queued.Attempts));
    }

    [Fact]
    public async Task PhotoUploadFailingTwice_FallsBackToText()
    {
        var d = _h.AddDestination(-1001);
        _h.Api.On("sendPhoto", _ => throw new HttpRequestException("upload reset"));
        var entry = Entry(d, "text layout", poster: Poster());
        _outbox.Enqueue(entry);

        await _delivery.DeliverAsync(_outbox.Snapshot().Single(), d, TestContext.Current.CancellationToken);
        await _delivery.DeliverAsync(_outbox.Snapshot().Single(), d, TestContext.Current.CancellationToken);
        Assert.True(_outbox.Snapshot().Single().Message.ForceTextOnly);
        await _delivery.DeliverAsync(_outbox.Snapshot().Single(), d, TestContext.Current.CancellationToken);

        Assert.Equal(2, _h.Api.CallsTo("sendPhoto").Count());
        Assert.Equal("text layout", (string)Assert.Single(_h.Api.CallsTo("sendMessage")).Body["text"]!);
    }

    [Fact]
    public async Task ServerError_Retries_ButAnUnknown400_IsDroppedAndRecorded()
    {
        var d = _h.AddDestination(-1001);
        _h.Api.OnSequence("sendMessage", FakeBotApi.Error(502, "Bad Gateway"), FakeBotApi.Error(400, "Bad Request: something new"));
        _outbox.Enqueue(Entry(d, "x"));

        await _delivery.DeliverAsync(_outbox.Snapshot().Single(), d, TestContext.Current.CancellationToken);
        Assert.Equal(1, _outbox.Snapshot().Single().Attempts);

        await _delivery.DeliverAsync(_outbox.Snapshot().Single(), d, TestContext.Current.CancellationToken);
        Assert.Equal(0, _outbox.Count);
        var error = Assert.Single(_h.State.LastErrorsSnapshot());
        Assert.Contains("something new", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, _h.State.Read(s => s.Status.Counters.DroppedOutbox));
    }

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Unauthorized_KeepsTheEntry_ForARetry()
    {
        var d = _h.AddDestination(-1001);
        _h.Api.On("sendMessage", _ => FakeBotApi.Error(401, "Unauthorized"));
        var entry = Entry(d, "hi");

        await Deliver(entry, d);

        var queued = Assert.Single(_outbox.Snapshot());
        Assert.True(queued.NotBefore > Now);
        Assert.Equal(1, queued.Attempts);
        Assert.Equal(0, _h.State.Read(s => s.Status.Counters.DroppedOutbox));
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task UnreadablePoster_SendsTheTextLayout()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.UserName == "root", "needs Unix permissions as a normal user");
        var d = _h.AddDestination(-1001);
        var poster = Poster();
        File.SetUnixFileMode(poster, UnixFileMode.None);
        try
        {
            await Deliver(Entry(d, "text layout", poster: poster), d);
        }
        finally
        {
            File.SetUnixFileMode(poster, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        Assert.Empty(_h.Api.CallsTo("sendPhoto"));
        Assert.Equal("text layout", (string)Assert.Single(_h.Api.CallsTo("sendMessage")).Body["text"]!);
        Assert.Equal(0, _outbox.Count);
    }

    [Fact]
    public async Task HeldForMoreThan5Minutes_AddsTheDelayedLine_ToTextAndCaption()
    {
        var d = _h.AddDestination(-1001);
        var text = Entry(d, "<b>hi</b>");
        text.QueuedAt = Now - TimeSpan.FromMinutes(6);
        var photo = Entry(d, "cap", poster: Poster());
        photo.QueuedAt = Now - TimeSpan.FromMinutes(6);

        await Deliver(text, d);
        _h.Time.Advance(TimeSpan.FromSeconds(1));
        await Deliver(photo, d);

        var line = "\n\n" + _formatter.DelayedLine(text.QueuedAt, Now - TimeSpan.FromSeconds(1));
        Assert.Equal("<b>hi</b>" + line, (string)Assert.Single(_h.Api.CallsTo("sendMessage")).Body["text"]!);
        Assert.Equal("cap\n\n" + _formatter.DelayedLine(photo.QueuedAt, Now), (string)Assert.Single(_h.Api.CallsTo("sendPhoto")).Body["caption"]!);
    }

    [Fact]
    public async Task Under5Minutes_NoLine()
    {
        var d = _h.AddDestination(-1001);
        var entry = Entry(d, "hi");
        entry.QueuedAt = Now - TimeSpan.FromMinutes(5);

        await Deliver(entry, d);

        Assert.Equal("hi", (string)Assert.Single(_h.Api.CallsTo("sendMessage")).Body["text"]!);
    }

    [Fact]
    public async Task Edit_NeverGetsTheLine()
    {
        var d = _h.AddDestination(-1001);
        var send = Entry(d, "one", sentKey: "g1");
        await Deliver(send, d);
        var edit = Entry(d, "two");
        edit.Op = OutboxOp.Edit;
        edit.TargetKey = "g1";
        edit.QueuedAt = Now - TimeSpan.FromMinutes(10);

        await Deliver(edit, d);

        Assert.Equal("two", (string)Assert.Single(_h.Api.CallsTo("editMessageText")).Body["text"]!);
    }

    [Fact]
    public async Task PlainTextFallback_KeepsTheLineAsText()
    {
        var d = _h.AddDestination(-1001);
        var entry = Entry(d, "<b>hi</b>");
        entry.QueuedAt = Now - TimeSpan.FromMinutes(6);
        entry.Message.PlainText = true;

        await Deliver(entry, d);

        var sent = (string)Assert.Single(_h.Api.CallsTo("sendMessage")).Body["text"]!;
        Assert.StartsWith("hi\n\n⏱ ", sent, StringComparison.Ordinal);
        Assert.EndsWith(" UTC", sent, StringComparison.Ordinal);
    }

    private async Task Deliver(OutboxEntry entry, Destination destination)
    {
        _outbox.Enqueue(entry);
        var queued = _outbox.Snapshot().Single(e => e.EntryId == entry.EntryId);
        await _delivery.DeliverAsync(queued, _h.Settings.Current.Destinations.FirstOrDefault(x => x.Id == destination.Id) ?? destination, TestContext.Current.CancellationToken);
    }

    private string Poster()
    {
        var path = Path.Combine(_h.Dir.Path, "poster.jpg");
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    private OutboxEntry Entry(Destination d, string text, string? poster = null, string? sentKey = null, bool buttons = false) => new()
    {
        EntryId = Guid.NewGuid().ToString("N"),
        DestinationId = d.Id,
        EventKey = "media.movie",
        SentKey = sentKey,
        QueuedAt = Now,
        Message = new OutboxMessage
        {
            Text = text,
            Caption = text,
            PosterPath = poster,
            PosterCacheKey = poster is null ? null : "item1|2026",
            Buttons = buttons ? [[new OutboxButton { Text = "Open", Url = "https://jf.example.com/web" }]] : [],
        },
    };

    private OutboxEntry Edit(Destination d, string targetKey, string text)
    {
        var e = Entry(d, text);
        e.Op = OutboxOp.Edit;
        e.TargetKey = targetKey;
        return e;
    }
}
