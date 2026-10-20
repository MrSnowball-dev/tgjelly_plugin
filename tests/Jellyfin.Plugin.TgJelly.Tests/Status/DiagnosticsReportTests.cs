using System;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Compat;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Delivery;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.Intake;
using Jellyfin.Plugin.TgJelly.Pipeline;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Status;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Status;

public class DiagnosticsReportTests
{
    private const string Token = "123456:ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    [Fact]
    public async System.Threading.Tasks.Task Diagnostics_RedactsTokenInLastErrors()
    {
        using var h = new BotTestHarness();
        h.State.Update(s => s.Status.LastErrors.Add(new StatusError { At = h.Time.GetUtcNow(), Source = "outbox", Message = "failed for " + Token }));

        var text = (await NewReportAsync(h)).Build();

        Assert.Contains("<token>", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, text, StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task Diagnostics_NeverContainsProxyPassword()
    {
        using var h = new BotTestHarness();
        h.Settings.Update(null, c => c.Proxy = new ProxySettings { Mode = ProxyMode.Socks5, Url = "socks5://proxy.example.com:1080" });
        h.Secrets.Save(new BotSecrets { BotToken = Token, ProxyUsername = "proxyuser", ProxyPassword = "hunter2hunter2" });

        var text = (await NewReportAsync(h)).Build();

        Assert.DoesNotContain("hunter2hunter2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("proxyuser", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, text, StringComparison.Ordinal);
        Assert.Contains("proxy.example.com", text, StringComparison.Ordinal);
        Assert.Contains("1080", text, StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task Diagnostics_OmitsChatIdsAndTitles()
    {
        using var h = new BotTestHarness();
        var d = h.AddDestination(-1001234567890);
        h.Settings.Update(null, c => c.Destinations[0].Title = "Secret Group");

        var text = (await NewReportAsync(h)).Build();

        Assert.DoesNotContain("1001234567890", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret Group", text, StringComparison.Ordinal);
        Assert.Contains(d.Id + ":", text, StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task Diagnostics_ListsEachDestinationHealth()
    {
        using var h = new BotTestHarness();
        var d1 = h.AddDestination(-100111, threadId: 5).Id;
        var d2 = h.AddDestination(-100222, ChatKind.Channel).Id;
        h.Settings.Update(null, c =>
        {
            c.Destinations[0].Events = ["a", "b"];
            c.Destinations[0].LibraryIds = ["x"];
            c.Destinations[1].Enabled = false;
        });
        var (report, health) = await NewReportWithHealthAsync(h);
        health.MarkProblem(d2, HealthStatus.Lost, "gone");
        health.SetPhotosBlocked(d1);

        var lines = report.Build().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

        var line1 = Assert.Single(lines, l => l.StartsWith(d1 + ":", StringComparison.Ordinal));
        var line2 = Assert.Single(lines, l => l.StartsWith(d2 + ":", StringComparison.Ordinal));
        Assert.Contains("photos blocked: yes", line1, StringComparison.Ordinal);
        Assert.Contains("topic set: yes", line1, StringComparison.Ordinal);
        Assert.Contains("events: 2", line1, StringComparison.Ordinal);
        Assert.Contains("libraries: 1", line1, StringComparison.Ordinal);
        Assert.Contains("enabled: no", line2, StringComparison.Ordinal);
        Assert.Contains("health: Lost", line2, StringComparison.Ordinal);
        Assert.Contains("topic set: no", line2, StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task Diagnostics_ScrubsTitlesAndChatIdsFromErrors()
    {
        using var h = new BotTestHarness();
        var d = h.AddDestination(-1001234567890);
        h.Settings.Update(null, c => c.Destinations[0].Title = "Secret Group");
        h.State.Update(s =>
        {
            s.Status.LastErrors.Add(new StatusError { At = h.Time.GetUtcNow(), Source = "outbox", Message = "Secret Group: Forbidden: bot was kicked" });
            s.Status.LastErrors.Add(new StatusError { At = h.Time.GetUtcNow(), Source = "bot", Message = "chat -1001234567890 not found (1001234567890)" });
            s.Status.LoadWarnings.Add("warning for Secret Group");
        });

        var text = (await NewReportAsync(h)).Build();

        Assert.DoesNotContain("Secret Group", text, StringComparison.Ordinal);
        Assert.DoesNotContain("1001234567890", text, StringComparison.Ordinal);
        Assert.Contains("<dest:" + d.Id + ">", text, StringComparison.Ordinal);
        Assert.Contains("<chat>", text, StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task Diagnostics_ReportsBaseUrlOverrideOnlyAsYesOrNo()
    {
        using var h = new BotTestHarness();

        var text = (await NewReportAsync(h, baseUrl: "http://secret-host.internal:8081")).Build();

        Assert.Contains("base URL override: yes", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-host", text, StringComparison.Ordinal);
    }

    private static async System.Threading.Tasks.Task<DiagnosticsReport> NewReportAsync(BotTestHarness h, string? baseUrl = null)
        => (await NewReportWithHealthAsync(h, baseUrl)).Report;

    private static async System.Threading.Tasks.Task<(DiagnosticsReport Report, HealthService Health)> NewReportWithHealthAsync(BotTestHarness h, string? baseUrl = null)
    {
        var gate = await h.StartOwnedGateAsync();
        var library = new FakeLibrary();
        var health = new HealthService(gate, h.Connection, new DestinationChecker(h.Connection, h.Strings), h.Settings, h.State, h.Time, NullLogger<HealthService>.Instance);
        var compat = CompatGuard.Evaluate(new Version(BuildInfo.TargetJellyfinMajor, 0, 0, 0));
        var readiness = new MediaReadiness(h.State, h.Settings, library, new PendingIdSet(), h.Time, NullLogger<MediaReadiness>.Instance);
        var report = new DiagnosticsReport(
            compat, gate, h.Settings, h.State, h.Secrets, h.Connection, BotApiEnvironment.FromValue(baseUrl),
            new Outbox(h.State, h.Strings, h.Time), readiness, health, new FakeServerInfo(),
            new NoticeBuilder(h.State, h.Secrets, h.Settings, library, h.Strings, h.Time));
        return (report, health);
    }
}
