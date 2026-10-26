using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Intake;
using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Jellyfin.Plugin.TgJelly.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Updates;

public sealed class UpdateCheckerTests : IDisposable
{
    private readonly BotTestHarness _h = new();
    private readonly FakeReleaseFeed _feed = new();
    private readonly FakeServerInfo _server = new() { Version = new Version(10, 11, 11, 0) };
    private readonly FakeStartup _startup = new();
    private readonly IntakeQueues _queues = new(TimeProvider.System);

    [Fact]
    public async Task NotSubscribed_NeverCalls()
    {
        _h.AddDestination(-1001, events: BotTestHarness.MediaEvents);
        _h.AddDestination(-1002, events: BotTestHarness.AdminEvents);
        Assert.Equal(SettingsUpdateStatus.Saved, _h.Settings.Update(null, c => c.Destinations[1].Enabled = false).Status);
        using var checker = await NewAsync();

        await RunMinutesAsync(checker, 13 * 60);

        Assert.Equal(0, _feed.Calls);
    }

    [Fact]
    public async Task UpdateCheckDisabled_NeverCalls()
    {
        _h.AddDestination(-1001, events: BotTestHarness.AdminEvents);
        Assert.Equal(SettingsUpdateStatus.Saved, _h.Settings.Update(null, c => c.UpdateCheck.Enabled = false).Status);
        using var checker = await NewAsync();

        await RunMinutesAsync(checker, 13 * 60);

        Assert.Equal(0, _feed.Calls);
    }

    [Fact]
    public async Task FirstCheck_5MinAfterSubscription_ThenEvery12h()
    {
        using var checker = await NewAsync();
        await RunMinutesAsync(checker, 60);
        _h.AddDestination(-1001, events: BotTestHarness.AdminEvents);
        await checker.StepAsync(CancellationToken.None);

        await RunMinutesAsync(checker, 4);
        Assert.Equal(0, _feed.Calls);
        await RunMinutesAsync(checker, 1);
        Assert.Equal(1, _feed.Calls);

        await RunMinutesAsync(checker, (12 * 60) - 1);
        Assert.Equal(1, _feed.Calls);
        await RunMinutesAsync(checker, 1);
        Assert.Equal(2, _feed.Calls);
    }

    [Fact]
    public async Task Resubscribing_WaitsForTheInterval_SinceTheLastCheck()
    {
        _h.AddDestination(-1001, events: BotTestHarness.AdminEvents);
        using var checker = await NewAsync();
        await RunMinutesAsync(checker, 5);
        Assert.Equal(1, _feed.Calls);
        Assert.Equal(SettingsUpdateStatus.Saved, _h.Settings.Update(null, c => c.UpdateCheck.Enabled = false).Status);
        await RunMinutesAsync(checker, 60);
        Assert.Equal(SettingsUpdateStatus.Saved, _h.Settings.Update(null, c => c.UpdateCheck.Enabled = true).Status);
        await checker.StepAsync(CancellationToken.None);

        await RunMinutesAsync(checker, (11 * 60) - 1);
        Assert.Equal(1, _feed.Calls);
        await RunMinutesAsync(checker, 1);
        Assert.Equal(2, _feed.Calls);
    }

    [Fact]
    public async Task WaitsForServerStartup()
    {
        _h.AddDestination(-1001, events: BotTestHarness.AdminEvents);
        _startup.HasCompleted = false;
        using var checker = await NewAsync();

        await RunMinutesAsync(checker, 30);
        Assert.Equal(0, _feed.Calls);
        _startup.HasCompleted = true;
        await checker.StepAsync(CancellationToken.None);
        await RunMinutesAsync(checker, 4);
        Assert.Equal(0, _feed.Calls);
        await RunMinutesAsync(checker, 1);

        Assert.Equal(1, _feed.Calls);
    }

    [Fact]
    public async Task Checker_FixesOnly_Ignores10_12()
    {
        Kinds(fixes: true, releases: false, major: false);
        Release("v10.12.0");
        Release("v10.11.12");
        Release("v12.2");
        using var checker = await NewAsync();

        Assert.True(await checker.CheckOnceAsync(CancellationToken.None));

        var e = Assert.Single(_queues.TakeOther(10));
        Assert.Equal(("10.11.12", "10.11.11", "https://github.com/jellyfin/jellyfin/releases/tag/v10.11.12"), (e.Values[EventFields.Version], e.Values[EventFields.Current], e.Values[EventFields.Url]));
    }

    [Fact]
    public async Task Checker_FixesOnly_IgnoresTheOtherKindsOfRelease()
    {
        Kinds(fixes: true, releases: false, major: false);
        _server.Version = new Version(10, 11, 1, 0);
        Release("v10.11.2");
        Release("v10.12.0");

        using var checker = await NewAsync();
        await checker.CheckOnceAsync(CancellationToken.None);

        Assert.Equal("10.11.2", Assert.Single(_queues.TakeOther(10)).Values[EventFields.Version]);
    }

    [Fact]
    public async Task Checker_NewReleasesOnly_Announces10_12_NotFixesOrMajors()
    {
        Kinds(fixes: false, releases: true, major: false);
        Release("v10.11.12");
        Release("v10.12.0");
        Release("v12.2");
        using var checker = await NewAsync();

        await checker.CheckOnceAsync(CancellationToken.None);

        Assert.Equal("10.12.0", Assert.Single(_queues.TakeOther(10)).Values[EventFields.Version]);
    }

    [Fact]
    public async Task Checker_NewMajorOnly_Announces12()
    {
        Kinds(fixes: false, releases: false, major: true);
        Release("v10.11.12");
        Release("v10.12.0");
        Release("v12.2");
        using var checker = await NewAsync();

        await checker.CheckOnceAsync(CancellationToken.None);

        var e = Assert.Single(_queues.TakeOther(10));
        Assert.Equal(("12.2", "https://github.com/jellyfin/jellyfin/releases/tag/v12.2"), (e.Values[EventFields.Version], e.Values[EventFields.Url]));
    }

    [Fact]
    public async Task Checker_AllOff_AnnouncesNothing()
    {
        Kinds(fixes: false, releases: false, major: false);
        Release("v10.11.12");
        Release("v10.12.0");
        Release("v12.2");
        using var checker = await NewAsync();

        Assert.True(await checker.CheckOnceAsync(CancellationToken.None));

        Assert.Empty(_queues.TakeOther(10));
    }

    [Fact]
    public async Task Checker_Defaults_AnnounceFixesAndNewReleasesButNotMajors()
    {
        Release("v12.2");
        Release("v10.11.12");
        Release("v10.12.0");
        using var checker = await NewAsync();

        await checker.CheckOnceAsync(CancellationToken.None);

        Assert.Equal("10.12.0", Assert.Single(_queues.TakeOther(10)).Values[EventFields.Version]);
    }

    [Fact]
    public async Task Checker_AllThree_AnnouncesTheNewest()
    {
        Kinds(fixes: true, releases: true, major: true);
        Release("v10.11.12");
        Release("v12.2");
        using var checker = await NewAsync();

        await checker.CheckOnceAsync(CancellationToken.None);

        Assert.Equal("12.2", Assert.Single(_queues.TakeOther(10)).Values[EventFields.Version]);
    }

    private void Kinds(bool fixes, bool releases, bool major)
        => Assert.Equal(SettingsUpdateStatus.Saved, _h.Settings.Update(null, c =>
        {
            c.UpdateCheck.Fixes = fixes;
            c.UpdateCheck.NewReleases = releases;
            c.UpdateCheck.NewMajor = major;
        }).Status);

    [Fact]
    public async Task DraftsAndPrereleases_Skipped()
    {
        Release("v10.11.13", draft: true);
        Release("v10.11.14", prerelease: true);
        Release("v10.11.10");
        using var checker = await NewAsync();

        Assert.True(await checker.CheckOnceAsync(CancellationToken.None));

        Assert.Empty(_queues.TakeOther(10));
    }

    [Fact]
    public async Task EachVersion_AnnouncedOnce()
    {
        Release("v10.11.12");
        using var checker = await NewAsync();

        await checker.CheckOnceAsync(CancellationToken.None);
        await checker.CheckOnceAsync(CancellationToken.None);
        Assert.Single(_queues.TakeOther(10));
        Assert.Equal("v10.11.12", _h.State.Read(s => s.UpdateCheck.LastNotifiedVersion));

        Release("v10.11.13");
        await checker.CheckOnceAsync(CancellationToken.None);

        Assert.Equal("10.11.13", Assert.Single(_queues.TakeOther(10)).Values[EventFields.Version]);
    }

    [Fact]
    public async Task ThreeFailuresInARow_ShowFailing()
    {
        using var checker = await NewAsync();
        _feed.Error = new HttpRequestException("403 rate limited bot123456:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi");

        for (var i = 0; i < 3; i++)
        {
            Assert.False(await checker.CheckOnceAsync(CancellationToken.None));
        }

        Assert.Equal(3, _h.State.Read(s => s.UpdateCheck.ConsecutiveFailures));
        var error = _h.State.Read(s => s.UpdateCheck.LastError);
        Assert.Contains("403", error, StringComparison.Ordinal);
        Assert.DoesNotContain("ABCDEFGHIJKLMNOPQRSTUVWXYZ", error, StringComparison.Ordinal);
        Assert.Empty(_queues.TakeOther(10));

        _feed.Error = null;
        Assert.True(await checker.CheckOnceAsync(CancellationToken.None));

        Assert.Equal((0, (string?)null), _h.State.Read(s => (s.UpdateCheck.ConsecutiveFailures, s.UpdateCheck.LastError)));
        Assert.Equal(_h.Time.GetUtcNow(), _h.State.Read(s => s.UpdateCheck.LastCheckedAt));
    }

    [Fact]
    public async Task Timeout_CountsAsAFailure()
    {
        using var checker = await NewAsync();
        _feed.Error = new TaskCanceledException("timed out");

        Assert.False(await checker.CheckOnceAsync(CancellationToken.None));

        Assert.Equal(1, _h.State.Read(s => s.UpdateCheck.ConsecutiveFailures));
    }


    public void Dispose() => _h.Dispose();

    private void Release(string tag, bool draft = false, bool prerelease = false)
        => _feed.Releases.Add(new GitHubRelease(tag, draft, prerelease, "https://github.com/jellyfin/jellyfin/releases/tag/" + tag));

    private async Task<UpdateChecker> NewAsync()
    {
        var gate = await _h.StartOwnedGateAsync();
        var checker = new UpdateChecker(gate, _startup, _h.Settings, _h.State, _server, _feed, _queues, new Formatter(_h.Strings), _h.Time, NullLogger<UpdateChecker>.Instance);
        await checker.StepAsync(CancellationToken.None);
        return checker;
    }

    private async Task RunMinutesAsync(UpdateChecker checker, int minutes)
    {
        for (var i = 0; i < minutes; i++)
        {
            _h.Time.Advance(TimeSpan.FromMinutes(1));
            await checker.StepAsync(CancellationToken.None);
        }
    }
}
