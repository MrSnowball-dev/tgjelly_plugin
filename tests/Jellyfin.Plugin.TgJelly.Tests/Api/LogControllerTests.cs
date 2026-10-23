using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Api;
using Jellyfin.Plugin.TgJelly.Log;
using Jellyfin.Plugin.TgJelly.State;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Api;

public sealed class LogControllerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));
    private readonly EventLog _log;

    public LogControllerTests() => _log = new EventLog(new DataPaths(_dir.Path), _time, NullLogger<EventLog>.Instance);

    private DateTimeOffset Now => _time.GetUtcNow();

    [Fact]
    public async Task LogController_ProblemsOnly_FiltersAndPages()
    {
        await _log.Loaded;
        Add("sent", 1, LogResult.Sent, "d1");
        Add("failed", 2, LogResult.Failed, "d1", telegram: "403 Forbidden: bot was kicked from the supergroup chat");
        Add("skipped", 3, LogResult.Skipped, null);
        Add("late", 4, LogResult.SentLate, "d2");
        Add("waiting", 5, LogResult.Waiting, "d1");
        Add("retrying", 6, LogResult.Waiting, "d1", telegram: "Exception during making request: connection timed out");
        Add("builtin", 7, LogResult.SentWithBuiltIn, "d1", eventKey: "security.login_failed");
        Add("nophoto", 8, LogResult.SentWithoutPhoto, "d1");
        var controller = new LogController(_log);

        var first = controller.Get(problemsOnly: true, limit: 3).Value!;
        Assert.Equal(["nophoto", "builtin", "retrying"], first.Items.Select(i => i.Id));
        Assert.Equal("SentWithoutPhoto", first.Items[0].Result);
        Assert.Equal(Now + TimeSpan.FromMinutes(8), first.LastSentAt);
        Assert.Equal(Now + TimeSpan.FromMinutes(8), first.LastEventAt);
        Assert.True(first.StartedEmpty);

        var older = controller.Get(problemsOnly: true, before: first.Items[^1].At, limit: 3).Value!;
        Assert.Equal(["late", "failed"], older.Items.Select(i => i.Id));
        Assert.Equal("403 Forbidden: bot was kicked from the supergroup chat", older.Items[1].Telegram);

        Assert.Equal(8, controller.Get().Value!.Items.Count);
        Assert.Equal(["late"], controller.Get(destinationId: "d2").Value!.Items.Select(i => i.Id));
        Assert.Equal(["builtin"], controller.Get(eventKey: "security.login_failed").Value!.Items.Select(i => i.Id));
        Assert.Equal(["nophoto"], controller.Get(limit: 0).Value!.Items.Select(i => i.Id));
        Assert.Equal(8, controller.Get(limit: 100_000).Value!.Items.Count);
    }

    [Fact]
    public void LogController_IsAdminOnly()
    {
        var authorize = typeof(LogController).GetCustomAttribute<AuthorizeAttribute>();

        Assert.Equal(Policies.RequiresElevation, authorize?.Policy);
    }

    public void Dispose()
    {
        _log.Dispose();
        _dir.Dispose();
    }

    private void Add(string id, int minutes, LogResult result, string? destinationId, string? telegram = null, string eventKey = "media.movie")
        => _log.Upsert(new LogEntry
        {
            Id = id,
            At = Now + TimeSpan.FromMinutes(minutes),
            UpdatedAt = Now + TimeSpan.FromMinutes(minutes),
            EventKey = eventKey,
            Title = "Arrival",
            DestinationId = destinationId,
            DestinationTitle = destinationId is null ? null : "Chat " + destinationId,
            Result = result,
            Telegram = telegram,
            Tries = telegram is null ? 0 : 1,
        });
}
