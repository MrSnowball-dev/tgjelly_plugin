using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Templates;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Destinations;

public sealed class DestinationTesterTests : IDisposable
{
    private readonly BotTestHarness _h = new();
    private readonly HealthService _health;
    private readonly DestinationTester _tester;

    public DestinationTesterTests()
    {
        _h.Connect();
        var gate = new InstanceGate(_h.Paths, _h.Time, NullLogger<InstanceGate>.Instance);
        _h.Track(gate);
        _health = new HealthService(gate, _h.Connection, new DestinationChecker(_h.Connection, _h.Strings), _h.Settings, _h.State, _h.Time, NullLogger<HealthService>.Instance);
        _h.Track(_health);
        _tester = new DestinationTester(_h.Connection, _health, _h.Strings, NullLogger<DestinationTester>.Instance);
    }

    [Fact]
    public async Task Test_SendsTheGroupsAsItalicText()
    {
        var d = _h.AddDestination(-1001, events: BotTestHarness.AdminEvents);

        var result = await _tester.SendTestAsync(d, TestContext.Current.CancellationToken);

        Assert.True(result.Sent);
        var body = Assert.Single(_h.Api.CallsTo("sendMessage")).Body;
        Assert.Equal("✅ TgJelly test: this chat will receive <i>security alerts, server alerts</i>", (string)body["text"]!);
        Assert.Equal("html", ((string)body["parse_mode"]!).ToLowerInvariant());
    }

    [Fact]
    public async Task Test_WithNoEvents_SaysNothingYet()
    {
        var d = _h.AddDestination(-1001, events: []);

        await _tester.SendTestAsync(d, TestContext.Current.CancellationToken);

        Assert.Contains("nothing yet", (string)Assert.Single(_h.Api.CallsTo("sendMessage")).Body["text"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Test_RechecksFirst_SoAHeldDestinationRecoversAndReceivesIt()
    {
        var d = _h.AddDestination(-1001, threadId: 5);
        _health.MarkProblem(d.Id, HealthStatus.Lost, "kicked");

        var result = await _tester.SendTestAsync(d, TestContext.Current.CancellationToken);

        Assert.True(result.Sent);
        Assert.Equal(HealthStatus.Ok, result.Health.Status);
        Assert.Equal(5, (int)Assert.Single(_h.Api.CallsTo("sendMessage")).Body["message_thread_id"]!);
    }

    [Fact]
    public async Task Test_ToALostChat_ReportsTheProblem_AndSendsNothing()
    {
        var d = _h.AddDestination(-1001);
        _h.Api.SetMember(-1001, FakeBotApi.Member("left"));

        var result = await _tester.SendTestAsync(d, TestContext.Current.CancellationToken);

        Assert.False(result.Sent);
        Assert.Equal(HealthStatus.Lost, result.Health.Status);
        Assert.Equal(_h.Strings.Get("check.lost"), result.Error);
        Assert.Empty(_h.Api.CallsTo("sendMessage"));
    }

    [Fact]
    public async Task SendSample_Poster_SendsPhoto()
    {
        var d = _h.AddDestination(-1001, threadId: 7);
        _h.Settings.Update(null, c => c.Destinations = [.. c.Destinations.Select(x => { x.SilentGroups = ["media"]; return x; })]);
        d = _h.Settings.Current.Destinations.Single();

        var result = await _tester.SendSampleAsync(d, Sample(poster: true), TestContext.Current.CancellationToken);

        Assert.True(result.Sent);
        var body = Assert.Single(_h.Api.CallsTo("sendPhoto")).Body;
        Assert.Equal("file:poster.png", (string)body["0"]!);
        Assert.Equal("<b>cap</b>", (string)body["caption"]!);
        Assert.Equal(7, (int)body["message_thread_id"]!);
        Assert.Equal("true", body["disable_notification"]!.ToString().ToLowerInvariant());
        Assert.Contains("Open", body["reply_markup"]!.ToString(), StringComparison.Ordinal);
        Assert.Empty(_h.Api.CallsTo("sendMessage"));
    }

    [Fact]
    public async Task SendSample_PhotosBlocked_SendsText()
    {
        var d = _h.AddDestination(-1001);
        _h.Api.AddChat(-1001, "group", "Family", permissionsJson: """{"can_send_messages":true,"can_send_photos":false}""");
        _h.Api.SetMember(-1001, """{"status":"member","user":{"id":123456789,"is_bot":true,"first_name":"b"}}""");

        var result = await _tester.SendSampleAsync(d, Sample(poster: true), TestContext.Current.CancellationToken);

        Assert.True(result.Sent);
        Assert.Equal("<b>text</b>", (string)Assert.Single(_h.Api.CallsTo("sendMessage")).Body["text"]!);
        Assert.Empty(_h.Api.CallsTo("sendPhoto"));
        Assert.True(result.Health.PhotosBlocked);
    }

    [Fact]
    public async Task SendSample_FirstPhotoRefusal_MarksPhotosBlockedAndSendsTheTextOnce()
    {
        // The chat check passes, but Telegram refuses the photo itself.
        var d = _h.AddDestination(-1001);
        _h.Api.On("sendPhoto", _ => FakeBotApi.Error(400, "Bad Request: not enough rights to send photos to the chat"));

        var result = await _tester.SendSampleAsync(d, Sample(poster: true), TestContext.Current.CancellationToken);

        Assert.True(result.Sent);
        Assert.Null(result.Error);
        Assert.Single(_h.Api.CallsTo("sendPhoto"));
        var body = Assert.Single(_h.Api.CallsTo("sendMessage")).Body;
        Assert.Equal("<b>text</b>", (string)body["text"]!);
        Assert.Contains("Open", body["reply_markup"]!.ToString(), StringComparison.Ordinal);
        Assert.True(_health.Get(d.Id).PhotosBlocked);
        Assert.True(result.Health.PhotosBlocked);
        Assert.Equal(HealthStatus.Ok, result.Health.Status);
    }

    [Fact]
    public async Task SendSample_PhotoRefusedAndTextFails_ReportsTheTextError()
    {
        var d = _h.AddDestination(-1001);
        _h.Api.On("sendPhoto", _ => FakeBotApi.Error(400, "Bad Request: not enough rights to send photos to the chat"));
        _h.Api.On("sendMessage", _ => FakeBotApi.Error(403, "Forbidden: bot was kicked from the supergroup chat"));

        var result = await _tester.SendSampleAsync(d, Sample(poster: true), TestContext.Current.CancellationToken);

        Assert.False(result.Sent);
        Assert.Single(_h.Api.CallsTo("sendMessage"));
        Assert.Equal(HealthStatus.Lost, result.Health.Status);
    }

    [Fact]
    public async Task SendSample_NoPoster_SendsText_NotSilentForOtherGroups()
    {
        var d = _h.AddDestination(-1001);

        await _tester.SendSampleAsync(d, Sample(poster: false), TestContext.Current.CancellationToken);

        var body = Assert.Single(_h.Api.CallsTo("sendMessage")).Body;
        Assert.Equal("html", ((string)body["parse_mode"]!).ToLowerInvariant());
        Assert.DoesNotContain("disable_notification", body.Select(p => p.Key));
    }

    [Fact]
    public async Task SendSample_NotConnected_Error()
    {
        var d = _h.AddDestination(-1001);
        _h.Connection.Detach();

        var result = await _tester.SendSampleAsync(d, Sample(poster: true), TestContext.Current.CancellationToken);

        Assert.False(result.Sent);
        Assert.Equal(_h.Strings.Get("destination.notConnected"), result.Error);
    }

    [Fact]
    public async Task SendSample_Lost_MarksHealth()
    {
        var d = _h.AddDestination(-1001);
        _h.Api.On("sendPhoto", _ => FakeBotApi.Error(403, "Forbidden: bot was kicked from the supergroup chat"));

        var result = await _tester.SendSampleAsync(d, Sample(poster: true), TestContext.Current.CancellationToken);

        Assert.False(result.Sent);
        Assert.Equal(HealthStatus.Lost, result.Health.Status);
        Assert.Equal(HealthStatus.Lost, _health.Get(d.Id).Status);
    }

    private static SampleMessage Sample(bool poster)
        => new("<b>cap</b>", poster, poster, [[new OutboxButton { Text = "Open in Jellyfin", Url = "https://jf.example.com/web/" }]], 3, 1024, null, "<b>text</b>", "media.movie");

    public void Dispose() => _h.Dispose();
}
