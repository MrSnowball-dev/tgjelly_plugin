using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Intake;
using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Strings;
using Jellyfin.Plugin.TgJelly.Templates;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Telegram.Bot.Extensions;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Rendering;

/// <summary>
/// The alert layouts give the buttons only; the text tests render through <see cref="NotificationRenderer"/>, so they
/// check the built-in templates against v0.2's text.
/// </summary>
public sealed class AlertLayoutsTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 18, 14, 5, 9, TimeSpan.Zero);
    private const string TimeTag = "<tg-time unix=\"1792332309\" format=\"t\">14:05 UTC</tg-time>";
    private readonly BotTestHarness _h = new();
    private readonly AlertLayouts _layouts;
    private readonly NotificationRenderer _renderer;

    public AlertLayoutsTests()
    {
        var formatter = new Formatter(_h.Strings);
        _layouts = new AlertLayouts(formatter, _h.Settings, _h.Logo);
        var time = new FakeTimeProvider(At + TimeSpan.FromHours(1));
        var links = new LinkBuilder(new FakeServerInfo());
        var library = new FakeLibrary();
        var models = new TemplateModelBuilder(library, links, _h.Settings, formatter, time);
        _renderer = new NotificationRenderer(
            new MediaLayouts(formatter, links, library, _h.Settings),
            _layouts,
            new TemplateRenderer(new TemplateEngine(TimeProvider.System), models, new TemplateFlags(_h.State, _h.Settings, time), _h.Settings, _h.Strings, NullLogger<TemplateRenderer>.Instance));
    }

    public void Dispose() => _h.Dispose();

    [Theory]
    [InlineData("security.login_failed")]
    [InlineData("security.lockout")]
    [InlineData("server.started")]
    [InlineData("server.plugin_updated")]
    [InlineData("server.restart_pending")]
    [InlineData("security.something_new")]
    public void Layout_NoTextNoPosterNoButtons(string key)
    {
        var message = _layouts.Render(Alert(key, new() { [EventFields.User] = "alice", [EventFields.Url] = "https://example.com" }));

        Assert.Equal(string.Empty, message.Text);
        Assert.Equal(string.Empty, message.Caption);
        Assert.Null(message.PosterPath);
        Assert.Empty(message.Buttons);
    }

    [Theory]
    [InlineData("security.lockout")]
    [InlineData("server.started")]
    public void Alert_PosterOn_UsesLogoPathAndStableCacheKey(string key)
    {
        _h.Settings.Update(null, c => c.Message.Change(key, s => s.ShowPoster = true));

        var first = _layouts.Render(Alert(key, new()));
        var second = _layouts.Render(Alert(key, new()));

        Assert.Equal(_h.Paths.LogoPath, first.PosterPath);
        Assert.True(File.Exists(first.PosterPath));
        Assert.Matches("^tgjelly-logo:[0-9a-f]{16}$", first.PosterCacheKey);
        Assert.Equal(first.PosterCacheKey, second.PosterCacheKey);
    }

    [Fact]
    public void Alert_PosterOff_NoPoster()
    {
        var message = _layouts.Render(Alert("security.lockout", new()));

        Assert.Null(message.PosterPath);
        Assert.Null(message.PosterCacheKey);
        Assert.False(File.Exists(_h.Paths.LogoPath));
    }

    [Fact]
    public void Alert_PosterOn_UnwritableFolder_FallsBackToTextAndLogsWhy()
    {
        var blocker = new TempDir();
        var file = Path.Combine(blocker.Path, "not-a-folder");
        File.WriteAllText(file, "x");
        var logger = new ListLogger<LogoFile>();
        var layouts = new AlertLayouts(new Formatter(_h.Strings), _h.Settings, new LogoFile(new Jellyfin.Plugin.TgJelly.State.DataPaths(file), logger));
        _h.Settings.Update(null, c => c.Message.Change("server.started", s => s.ShowPoster = true));

        var message = layouts.Render(Alert("server.started", new()));

        Assert.Null(message.PosterPath);
        Assert.Contains(logger.Entries, e => e.Message.Contains("could not write the logo", StringComparison.Ordinal));
        blocker.Dispose();
    }

    [Fact]
    public void Logo_WrittenOnce_RewrittenWhenSizeDiffers()
    {
        var logo = _h.Logo;
        var path = logo.Ensure()!;
        var size = new FileInfo(path).Length;
        Assert.True(size > 0);

        File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        logo.Ensure();
        Assert.Equal(2020, File.GetLastWriteTimeUtc(path).Year);

        File.WriteAllBytes(path, [1, 2, 3]);
        logo.Ensure();
        Assert.Equal(size, new FileInfo(path).Length);
    }

    [Fact]
    public void Layout_UpdateAvailable_ReleaseNotesButtonOnlyWithAUrl()
    {
        var message = _layouts.Render(Alert("server.update_available", new() { [EventFields.Url] = "https://github.com/jellyfin/jellyfin/releases/tag/v12.3.0" }));

        Assert.Equal(string.Empty, message.Text);
        var button = Assert.Single(Assert.Single(message.Buttons));
        Assert.Equal(("Release notes", "https://github.com/jellyfin/jellyfin/releases/tag/v12.3.0"), (button.Text, button.Url));
        Assert.Empty(_layouts.Render(Alert("server.update_available", new() { [EventFields.Url] = string.Empty })).Buttons);
        Assert.Empty(_layouts.Render(Alert("server.update_available", new())).Buttons);
    }

    [Fact]
    public void LoginFailedMany_MatchesGolden()
    {
        var message = Render(Alert("security.login_failed", new()
        {
            [EventFields.User] = "alice", [EventFields.Attempts] = "37", [EventFields.IpCount] = "12", [EventFields.Ip] = "203.0.113.5",
            [EventFields.Client] = "Infuse", [EventFields.Device] = "iPhone", [EventFields.Locked] = "true",
        }));

        Assert.Equal(Golden("login_failed.many.txt"), message.Text);
        Assert.Equal(Golden("login_failed.many.txt"), message.Caption);
        Assert.Null(message.PosterPath);
        Assert.Empty(message.Buttons);
    }

    [Fact]
    public void ServerStarted_MatchesGolden()
        => Assert.Equal(
            Golden("server.started.txt"),
            Render(Alert("server.started", new() { [EventFields.Version] = "12.2.0", [EventFields.Server] = "Home" })).Text);

    [Fact]
    public void PluginUpdated_MatchesGolden()
        => Assert.Equal(
            Golden("plugin_updated.txt"),
            Render(Alert("server.plugin_updated", new()
            {
                [EventFields.Plugin] = "Trakt", [EventFields.Version] = "26.0.0.0", [EventFields.Changelog] = "- Fixed sync for <b>shows</b>\n- Faster & safer",
            })).Text);

    [Fact]
    public void LoginFailed_SingleAttempt()
    {
        var values = new Dictionary<string, string>
        {
            [EventFields.User] = "alice", [EventFields.Attempts] = "1", [EventFields.IpCount] = "1", [EventFields.Ip] = "203.0.113.5",
            [EventFields.Client] = "Infuse", [EventFields.Device] = "iPhone", [EventFields.Locked] = "false",
        };

        var lines = Render(Alert("security.login_failed", values)).Text.Split('\n');
        values[EventFields.Device] = string.Empty;
        var withoutDevice = Render(Alert("security.login_failed", values)).Text.Split('\n');

        Assert.Equal(["🔐 <b>Wrong password for alice</b>", "203.0.113.5 · Infuse · iPhone · " + TimeTag], lines);
        Assert.Equal("203.0.113.5 · Infuse · " + TimeTag, withoutDevice[1]);
    }

    [Theory]
    [InlineData("1", "🔒 <b>bob</b> is locked out after 1 failed attempt")]
    [InlineData("5", "🔒 <b>bob</b> is locked out after 5 failed attempts")]
    public void Lockout_Plural(string attempts, string expected)
        => Assert.Equal(expected, Render(Alert("security.lockout", new() { [EventFields.User] = "bob", [EventFields.Attempts] = attempts })).Text);

    [Fact]
    public void LoginSucceeded_ClientDeviceIpAndTime()
        => Assert.Equal(
            "🔐 <b>alice signed in</b>\nInfuse · iPhone\n🌐 203.0.113.5\n🕒 " + TimeTag,
            Render(Alert("security.login_succeeded", new()
            {
                [EventFields.User] = "alice", [EventFields.Client] = "Infuse", [EventFields.Device] = "iPhone", [EventFields.Ip] = "203.0.113.5",
            })).Text);

    [Theory]
    [InlineData("security.user_created", "🔐 <b>New user: carol</b>")]
    [InlineData("security.user_deleted", "🔐 <b>User deleted: carol</b>")]
    [InlineData("security.password_changed", "🔐 <b>Password changed for carol</b>")]
    public void UserEvents_OneLine(string key, string expected)
        => Assert.Equal(expected, Render(Alert(key, new() { [EventFields.User] = "carol" })).Text);

    [Fact]
    public void UpdateAvailable_HasReleaseNotesButton()
    {
        var message = Render(Alert("server.update_available", new()
        {
            [EventFields.Version] = "12.3.0", [EventFields.Current] = "12.2.0", [EventFields.Url] = "https://github.com/jellyfin/jellyfin/releases/tag/v12.3.0",
        }));

        Assert.Equal("⬆️ Jellyfin <b>12.3.0</b> is available (running 12.2.0)", message.Text);
        var button = Assert.Single(Assert.Single(message.Buttons));
        Assert.Equal(("Release notes", "https://github.com/jellyfin/jellyfin/releases/tag/v12.3.0"), (button.Text, button.Url));
    }

    [Fact]
    public void PluginInstallFailed_QuotesTheError()
        => Assert.Equal(
            "⚙️ <b>Plugin install failed: Trakt 26.0.0.0</b>\n\n<blockquote expandable>Checksum mismatch</blockquote>",
            Render(Alert("server.plugin_install_failed", new()
            {
                [EventFields.Plugin] = "Trakt", [EventFields.Version] = "26.0.0.0", [EventFields.Error] = "Checksum mismatch",
            })).Text);

    [Fact]
    public void TaskFailed_EmptyError_NoBlockquote()
    {
        Assert.Equal("⚙️ <b>Task failed: Scan Media Library</b>", Render(Alert("server.task_failed", new() { [EventFields.Task] = "Scan Media Library", [EventFields.Error] = "" })).Text);
        Assert.Equal("⚙️ <b>Task failed: Scan Media Library</b>", Render(Alert("server.task_failed", new() { [EventFields.Task] = "Scan Media Library" })).Text);
        Assert.Equal(
            "⚙️ <b>Task failed: Scan Media Library</b>\n\n<blockquote expandable>Disk full</blockquote>",
            Render(Alert("server.task_failed", new() { [EventFields.Task] = "Scan Media Library", [EventFields.Error] = "Disk full" })).Text);
    }

    [Fact]
    public void HugeChangelog_IsShortened_FitsIn4096()
    {
        var changelog = string.Join('\n', Enumerable.Range(1, 2000).Select(i => $"- change {i} with <tags> & more"));

        var message = Render(Alert("server.plugin_updated", new()
        {
            [EventFields.Plugin] = "Trakt", [EventFields.Version] = "26.0.0.0", [EventFields.Changelog] = changelog,
        }));

        Assert.StartsWith("⚙️ <b>Plugin updated: Trakt 26.0.0.0</b>\n\n<blockquote expandable>- change 1 with &lt;tags&gt;", message.Text, StringComparison.Ordinal);
        Assert.EndsWith("…</blockquote>", message.Text, StringComparison.Ordinal);
        Assert.InRange(HtmlText.PlainLength(message.Text), 1, 4056);
        Assert.InRange(HtmlText.PlainLength(message.Caption), 1, 984);
    }

    [Fact]
    public void UserControlledValues_AreEscaped()
    {
        var text = Render(Alert("security.login_failed", new()
        {
            [EventFields.User] = "<script>", [EventFields.Attempts] = "1", [EventFields.Ip] = "1.2.3.4", [EventFields.Client] = "a&b", [EventFields.Device] = "<i>",
        })).Text;

        Assert.StartsWith("🔐 <b>Wrong password for &lt;script&gt;</b>\n1.2.3.4 · a&amp;b · &lt;i&gt; · ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RestartPending_ListsReasons()
    {
        var model = Alert("server.restart_pending", new());
        model.Lines = ["Updated Trakt to 26.0.0.0", "Installed <Odd> & Co 1.0"];

        Assert.Equal(
            "⚙️ <b>Jellyfin needs a restart</b>\n\nUpdated Trakt to 26.0.0.0\nInstalled &lt;Odd&gt; &amp; Co 1.0",
            Render(model).Text);
    }

    internal static RenderModel Alert(string key, Dictionary<string, string> values) => new()
    {
        Kind = RenderKind.Alert,
        EventKey = key,
        At = At,
        Values = values,
    };

    private static string Golden(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Rendering", "Golden", name)).TrimEnd('\n');

    private OutboxMessage Render(RenderModel model) => _renderer.Render(model, new Destination { Id = "d1" })!;
}
