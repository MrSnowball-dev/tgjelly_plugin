using System;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Jellyfin.Plugin.TgJelly.Library;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Status;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Status;

public class NoticeBuilderTests
{
    [Fact]
    public void NoProblems_NoNotices()
    {
        using var h = new BotTestHarness();

        Assert.Empty(NewBuilder(h).Active());
        Assert.Empty(NewBuilder(h).Visible());
    }

    [Theory]
    [InlineData("BackupRestored", "backup:")]
    [InlineData("LoadWarning", "load:")]
    [InlineData("PublicUrlFlagged", "publicUrl:")]
    [InlineData("UpdateCheckFailing", "update:")]
    [InlineData("RemovedLibraries", "libraries:")]
    public void EachSource_GivesItsNotice(string kind, string idPrefix)
    {
        using var h = new BotTestHarness();
        var library = new FakeLibrary();
        Raise(h, kind);

        var notice = Assert.Single(NewBuilder(h, library).Active());

        Assert.Equal(kind, notice.Kind);
        Assert.StartsWith(idPrefix, notice.Id, StringComparison.Ordinal);
        Assert.NotEmpty(notice.Text);
    }

    [Fact]
    public void Texts_AndIds_AreAsSpecified()
    {
        using var h = new BotTestHarness();
        h.Time.SetUtcNow(new DateTimeOffset(2026, 10, 20, 12, 30, 0, TimeSpan.Zero));
        var at = h.Time.GetUtcNow();
        h.State.Update(s =>
        {
            s.Status.BackupRestoredAt = at;
            s.Status.PublicUrlFlag = new PublicUrlFlag { Url = "https://jf.example.com", At = at };
        });

        var notices = NewBuilder(h).Active();

        Assert.Equal($"backup:{at.ToUnixTimeSeconds()}", notices[0].Id);
        Assert.Contains("2026-10-20 12:30", notices[0].Text, StringComparison.Ordinal);
        Assert.Equal($"publicUrl:{at.ToUnixTimeSeconds()}", notices[1].Id);
        Assert.Contains("https://jf.example.com", notices[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Dismissed_IsHiddenButStillActive()
    {
        using var h = new BotTestHarness();
        Raise(h, "PublicUrlFlagged");
        var builder = NewBuilder(h);
        var id = builder.Active().Single().Id;

        Assert.True(builder.Dismiss(id));

        Assert.Empty(builder.Visible());
        Assert.Equal(id, builder.Active().Single().Id);
    }

    [Fact]
    public void DismissUnknown_ReturnsFalse_StoresNothing()
    {
        using var h = new BotTestHarness();
        var builder = NewBuilder(h);

        Assert.False(builder.Dismiss("nope:1"));

        Assert.Empty(h.State.Read(s => s.Status.DismissedNotices.ToArray()));
    }

    [Fact]
    public void Recurrence_ShowsAgain()
    {
        using var h = new BotTestHarness();
        var builder = NewBuilder(h);
        var t1 = h.Time.GetUtcNow();
        h.State.Update(s => s.Status.PublicUrlFlag = new PublicUrlFlag { Url = "https://a", At = t1 });
        Assert.True(builder.Dismiss(builder.Active().Single().Id));
        h.State.Update(s => s.Status.PublicUrlFlag = null);
        Assert.Empty(builder.Visible());

        var t2 = t1.AddMinutes(5);
        h.State.Update(s => s.Status.PublicUrlFlag = new PublicUrlFlag { Url = "https://a", At = t2 });

        Assert.Contains(builder.Visible(), n => n.Id == $"publicUrl:{t2.ToUnixTimeSeconds()}");
    }

    [Fact]
    public void ResolvedNotice_PrunesItsDismissal()
    {
        using var h = new BotTestHarness();
        Raise(h, "PublicUrlFlagged");
        var builder = NewBuilder(h);
        builder.Dismiss(builder.Active().Single().Id);
        Assert.Single(h.State.Read(s => s.Status.DismissedNotices.ToArray()));
        h.State.Update(s => s.Status.PublicUrlFlag = null);

        builder.Visible();

        Assert.Empty(h.State.Read(s => s.Status.DismissedNotices.ToArray()));
    }

    [Fact]
    public void DismissCap_Is50_OldestDropped()
    {
        using var h = new BotTestHarness();
        var builder = NewBuilder(h);
        var first = string.Empty;
        for (var i = 0; i < 51; i++)
        {
            var warning = "warning " + i;
            h.State.Update(s => s.Status.LoadWarnings = [.. s.Status.LoadWarnings, warning]);
        }

        foreach (var n in builder.Active())
        {
            first = first.Length == 0 ? n.Id : first;
            Assert.True(builder.Dismiss(n.Id));
        }

        var stored = h.State.Read(s => s.Status.DismissedNotices.ToArray());
        Assert.Equal(50, stored.Length);
        Assert.DoesNotContain(first, stored);
    }

    private static NoticeBuilder NewBuilder(BotTestHarness h, FakeLibrary? library = null) =>
        new(h.State, h.Secrets, h.Settings, library ?? new FakeLibrary(), h.Strings, h.Time);

    private static void Raise(BotTestHarness h, string kind)
    {
        switch (kind)
        {
            case "BackupRestored":
                h.State.Update(s => s.Status.BackupRestoredAt = h.Time.GetUtcNow());
                break;
            case "LoadWarning":
                h.State.Update(s => s.Status.LoadWarnings = ["state.json was quarantined"]);
                break;
            case "PublicUrlFlagged":
                h.State.Update(s => s.Status.PublicUrlFlag = new PublicUrlFlag { Url = "https://jf.example.com", At = h.Time.GetUtcNow() });
                break;
            case "UpdateCheckFailing":
                h.State.Update(s =>
                {
                    s.UpdateCheck.ConsecutiveFailures = 3;
                    s.UpdateCheck.LastError = "GitHub answered 403";
                });
                break;
            case "RemovedLibraries":
                h.AddDestination(-1001);
                h.Settings.Update(null, c => c.Destinations[0].LibraryIds = [Guid.NewGuid().ToString("N")]);
                break;
        }
    }
}
