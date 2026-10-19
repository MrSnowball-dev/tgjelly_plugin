using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Intake;
using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Strings;
using Microsoft.Extensions.Time.Testing;
using Telegram.Bot.Extensions;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Rendering;

public class AlertLayoutsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 18, 14, 5, 9, TimeSpan.Zero);
    private const string TimeTag = "<tg-time unix=\"1792332309\" format=\"t\">14:05 UTC</tg-time>";
    private readonly AlertLayouts _layouts = new(new Formatter(StringTable.LoadEmbedded()), new FakeTimeProvider(At + TimeSpan.FromHours(1)));

    [Fact]
    public void LoginFailedMany_MatchesGolden()
    {
        var message = _layouts.Render(Alert("security.login_failed", new()
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
            _layouts.Render(Alert("server.started", new() { [EventFields.Version] = "12.2.0", [EventFields.Server] = "Home" })).Text);

    [Fact]
    public void PluginUpdated_MatchesGolden()
        => Assert.Equal(
            Golden("plugin_updated.txt"),
            _layouts.Render(Alert("server.plugin_updated", new()
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

        var lines = _layouts.Render(Alert("security.login_failed", values)).Text.Split('\n');
        values[EventFields.Device] = string.Empty;
        var withoutDevice = _layouts.Render(Alert("security.login_failed", values)).Text.Split('\n');

        Assert.Equal(["🔐 <b>Wrong password for alice</b>", "203.0.113.5 · Infuse · iPhone · " + TimeTag], lines);
        Assert.Equal("203.0.113.5 · Infuse · " + TimeTag, withoutDevice[1]);
    }

    [Theory]
    [InlineData("1", "🔒 <b>bob</b> is locked out after 1 failed attempt")]
    [InlineData("5", "🔒 <b>bob</b> is locked out after 5 failed attempts")]
    public void Lockout_Plural(string attempts, string expected)
        => Assert.Equal(expected, _layouts.Render(Alert("security.lockout", new() { [EventFields.User] = "bob", [EventFields.Attempts] = attempts })).Text);

    [Fact]
    public void LoginSucceeded_ClientDeviceIpAndTime()
        => Assert.Equal(
            "🔐 <b>alice signed in</b>\nInfuse · iPhone\n🌐 203.0.113.5\n🕒 " + TimeTag,
            _layouts.Render(Alert("security.login_succeeded", new()
            {
                [EventFields.User] = "alice", [EventFields.Client] = "Infuse", [EventFields.Device] = "iPhone", [EventFields.Ip] = "203.0.113.5",
            })).Text);

    [Theory]
    [InlineData("security.user_created", "🔐 <b>New user: carol</b>")]
    [InlineData("security.user_deleted", "🔐 <b>User deleted: carol</b>")]
    [InlineData("security.password_changed", "🔐 <b>Password changed for carol</b>")]
    public void UserEvents_OneLine(string key, string expected)
        => Assert.Equal(expected, _layouts.Render(Alert(key, new() { [EventFields.User] = "carol" })).Text);

    [Fact]
    public void UpdateAvailable_HasReleaseNotesButton()
    {
        var message = _layouts.Render(Alert("server.update_available", new()
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
            _layouts.Render(Alert("server.plugin_install_failed", new()
            {
                [EventFields.Plugin] = "Trakt", [EventFields.Version] = "26.0.0.0", [EventFields.Error] = "Checksum mismatch",
            })).Text);

    [Fact]
    public void TaskFailed_EmptyError_NoBlockquote()
    {
        Assert.Equal("⚙️ <b>Task failed: Scan Media Library</b>", _layouts.Render(Alert("server.task_failed", new() { [EventFields.Task] = "Scan Media Library", [EventFields.Error] = "" })).Text);
        Assert.Equal("⚙️ <b>Task failed: Scan Media Library</b>", _layouts.Render(Alert("server.task_failed", new() { [EventFields.Task] = "Scan Media Library" })).Text);
        Assert.Equal(
            "⚙️ <b>Task failed: Scan Media Library</b>\n\n<blockquote expandable>Disk full</blockquote>",
            _layouts.Render(Alert("server.task_failed", new() { [EventFields.Task] = "Scan Media Library", [EventFields.Error] = "Disk full" })).Text);
    }

    [Fact]
    public void HugeChangelog_IsShortened_FitsIn4096()
    {
        var changelog = string.Join('\n', Enumerable.Range(1, 2000).Select(i => $"- change {i} with <tags> & more"));

        var message = _layouts.Render(Alert("server.plugin_updated", new()
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
        var text = _layouts.Render(Alert("security.login_failed", new()
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
            _layouts.Render(model).Text);
    }

    [Fact]
    public void UnknownKey_StillRenders()
        => Assert.Equal("security.something_new", _layouts.Render(Alert("security.something_new", new())).Text);

    internal static RenderModel Alert(string key, Dictionary<string, string> values) => new()
    {
        Kind = RenderKind.Alert,
        EventKey = key,
        At = At,
        Values = values,
    };

    private static string Golden(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Rendering", "Golden", name)).TrimEnd('\n');
}
