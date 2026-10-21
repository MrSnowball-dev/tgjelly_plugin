using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot.Screens;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Library;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Bot.Screens;

public sealed class DestinationScreensTests : IDisposable, IAsyncLifetime
{
    private readonly BotTestHarness _h = new();
    private BotRig _rig = null!;

    public async ValueTask InitializeAsync() => _rig = await BotRig.CreateAsync(_h);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task List_ShowsHealthBadges()
    {
        var ok = _h.AddDestination(-1001);
        var lost = _h.AddDestination(-1002);
        var paused = _h.AddDestination(-1003);
        var pausedLost = _h.AddDestination(-1004);
        _h.Settings.Update(null, c => c.Destinations.First(d => d.Id == paused.Id).Enabled = false);
        _h.Settings.Update(null, c => c.Destinations.First(d => d.Id == pausedLost.Id).Enabled = false);
        _rig.Health.MarkProblem(lost.Id, HealthStatus.Lost, "kicked");
        _rig.Health.MarkProblem(pausedLost.Id, HealthStatus.Lost, "kicked");

        await Click("1|dl|0");

        var labels = Labels(LastEdit());
        Assert.Equal(["✅ " + ok.Title, "⛔ " + lost.Title, "✅⏸ " + paused.Title, "⛔⏸ " + pausedLost.Title, "Back"], labels);
    }

    [Fact]
    public async Task HostileTitles_AreEscaped_AndCut()
    {
        var d = _h.AddDestination(-1001);
        var title = "<b>A&B</b>" + string.Concat(Enumerable.Repeat("😀", 60));
        _h.Settings.Update(null, c => c.Destinations.First(x => x.Id == d.Id).Title = title);

        await Click("1|dl|0");

        var edit = LastEdit();
        Assert.Contains("&lt;b&gt;A&amp;B&lt;/b&gt;", (string)edit.Body["text"]!, StringComparison.Ordinal);
        var label = Labels(edit)[0];
        Assert.Equal(40, label.EnumerateRunes().Count());
        Assert.EndsWith("…", label, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThirtyDestinations_PageAtEight()
    {
        for (var i = 1; i <= 30; i++)
        {
            _h.AddDestination(-1000 - i);
        }

        await Click("1|dl|0");
        Assert.Equal(["Next ›"], NavLabels(LastEdit()));
        await Click("1|dl|3");

        var last = LastEdit();
        Assert.Equal(6 + 1 + 1, Labels(last).Length);
        Assert.Equal(["‹ Prev"], NavLabels(last));
        Assert.DoesNotContain("Next ›", Labels(last));
    }

    [Fact]
    public async Task DestinationScreen_ShowsTheFacts_AndTheButtons()
    {
        var d = _h.AddDestination(-1001);

        await Click("1|d|" + d.Id);

        var edit = LastEdit();
        var text = (string)edit.Body["text"]!;
        Assert.Contains(d.Title, text, StringComparison.Ordinal);
        Assert.Contains("OK", text, StringComparison.Ordinal);
        Assert.Contains("Everything", text, StringComparison.Ordinal);
        Assert.Equal(["Events", "Libraries", "Silent groups", "Send test", "Check again", "Pause", "Remove", "Back"], Labels(edit));
    }

    [Fact]
    public async Task Detail_ButtonsCarryTheirIcons()
    {
        var d = _h.AddDestination(-1001);

        await Click("1|d|" + d.Id);

        Assert.Equal(
            [Icons.Bell, Icons.Folder, Icons.BellOff, Icons.Send, Icons.Refresh, Icons.Block, Icons.Trash, Icons.Back],
            IconIds(LastEdit()));
        Assert.StartsWith("<b>" + d.Title, (string)LastEdit().Body["text"]!, StringComparison.Ordinal);

        await Click("1|de|" + d.Id);
        Assert.StartsWith("<b><tg-emoji emoji-id=\"" + Icons.Bell + "\">🔔</tg-emoji> Events</b>", (string)LastEdit().Body["text"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Switches_HaveNoIcons()
    {
        var d = _h.AddDestination(-1001);
        _rig.Library.VirtualFolders.Add(new LibraryRef(Guid.NewGuid(), "Movies", "movies"));

        await Click("1|deg|" + d.Id + "|media");
        var events = IconIds(LastEdit());
        await Click("1|dlb|" + d.Id + "|0");
        var libraries = IconIds(LastEdit());

        // On the event screen only Back has an icon; on the library screen it is All libraries, the library switch, Back.
        Assert.All(events[..^1], Assert.Null);
        Assert.Equal(Icons.Back, events[^1]);
        Assert.Equal([Icons.Layers, null, Icons.Back], libraries);
    }

    [Fact]
    public async Task EventGroups_CarryTheirIcons_AndTheGroupHeadingShowsOne()
    {
        var d = ThreeEvents();

        await Click("1|de|" + d.Id);
        Assert.Equal([Icons.Video, Icons.Lock, Icons.Server, Icons.Back], IconIds(LastEdit()));

        await Click("1|deg|" + d.Id + "|server");
        Assert.Contains("<b><tg-emoji emoji-id=\"" + Icons.Server + "\">🖥</tg-emoji> Server</b>", (string)LastEdit().Body["text"]!, StringComparison.Ordinal);
    }

    private Destination ThreeEvents()
    {
        var d = _h.AddDestination(-1001);
        _h.Settings.Update(null, c => c.Destinations.First(x => x.Id == d.Id).Events = ["media.movie", "media.episodes", "server.started"]);
        return d;
    }

    [Fact]
    public async Task Events_ShowsOneButtonPerGroup_WithCounts()
    {
        var d = ThreeEvents();

        await Click("1|de|" + d.Id);

        var edit = LastEdit();
        Assert.Equal(["Media 2/5", "Security 0/6", "Server 1/8", "Back"], Labels(edit));
        Assert.Equal(["1|deg|" + d.Id + "|media", "1|deg|" + d.Id + "|security", "1|deg|" + d.Id + "|server", "1|d|" + d.Id], Data(edit));
        var text = (string)edit.Body["text"]!;
        Assert.Contains("Pick a group to switch its events.", text, StringComparison.Ordinal);
        edit.Explains("Media", "Security", "Server");
        Assert.Contains("Media</b> — new movies, episodes, albums, music videos and books", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EventGroup_ShowsOnlyItsSwitches_AndBackToTheGroups()
    {
        var d = ThreeEvents();

        await Click("1|deg|" + d.Id + "|security");

        var edit = LastEdit();
        var labels = Labels(edit);
        Assert.Equal(7, labels.Length);
        Assert.All(labels.Take(6), l => Assert.StartsWith("▫️ ", l, StringComparison.Ordinal));
        Assert.Equal(EventCatalog.AllKeys.Where(k => k.StartsWith("security.", StringComparison.Ordinal)).Select(k => "1|det|" + d.Id + "|" + k), Data(edit).Take(6));
        Assert.Equal("1|de|" + d.Id, Data(edit)[^1]);
    }

    [Fact]
    public async Task EventSwitch_StaysOnItsGroup_AndRecomputesThePreset()
    {
        var d = ThreeEvents();

        await Click("1|det|" + d.Id + "|server.task_failed");

        var edit = LastEdit();
        Assert.Equal(9, Labels(edit).Length);
        Assert.StartsWith("✅ ", Labels(edit)[Data(edit).ToList().IndexOf("1|det|" + d.Id + "|server.task_failed")], StringComparison.Ordinal);
        var saved = _h.Settings.Current.Destinations.Single();
        Assert.Contains("server.task_failed", saved.Events);
        Assert.Equal(PresetKind.Custom, saved.Preset);
    }

    [Fact]
    public async Task OldPagedEventsButton_OpensTheGroups()
    {
        var d = ThreeEvents();

        await Click("1|de|" + d.Id + "|1");

        Assert.Equal(["Media 2/5", "Security 0/6", "Server 1/8", "Back"], Labels(LastEdit()));
    }

    [Fact]
    public async Task UnknownGroup_OpensTheGroups()
    {
        var d = ThreeEvents();

        await Click("1|deg|" + d.Id + "|nope");

        Assert.Equal(["Media 2/5", "Security 0/6", "Server 1/8", "Back"], Labels(LastEdit()));
    }

    [Fact]
    public async Task ToggleEvent_AppliesToCurrentSettings()
    {
        var d = _h.AddDestination(-1001);
        await Click("1|de|" + d.Id + "|0");

        // The dashboard saves a change while the screen is open.
        _h.Settings.Update(null, c => c.Destinations.First(x => x.Id == d.Id).SilentGroups = ["security"]);
        await Click("1|det|" + d.Id + "|media.movie");

        var saved = _h.Settings.Current.Destinations.Single();
        Assert.Equal(["security"], saved.SilentGroups);
        Assert.DoesNotContain("media.movie", saved.Events);
        Assert.Equal(PresetKind.Custom, saved.Preset);
        Assert.Contains("▫️", Labels(LastEdit()).First(l => l.Contains("Movies", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToggleEvent_BackToAPreset_SetsThatPreset()
    {
        var d = _h.AddDestination(-1001, preset: PresetKind.NewMedia);

        await Click("1|det|" + d.Id + "|server.started");
        Assert.Equal(PresetKind.Custom, _h.Settings.Current.Destinations.Single().Preset);
        await Click("1|det|" + d.Id + "|server.started");

        Assert.Equal(PresetKind.NewMedia, _h.Settings.Current.Destinations.Single().Preset);
    }

    [Fact]
    public async Task Libraries_ShowRemovedIds_AndAllLibraries()
    {
        var d = _h.AddDestination(-1001);
        var movies = new LibraryRef(Guid.NewGuid(), "Movies", "movies");
        var shows = new LibraryRef(Guid.NewGuid(), "Shows", "tvshows");
        var gone = Guid.NewGuid().ToString("N");
        _rig.Library.VirtualFolders.AddRange([movies, shows]);
        _h.Settings.Update(null, c => c.Destinations.First(x => x.Id == d.Id).LibraryIds = [movies.Id.ToString("N"), gone]);

        await Click("1|dlb|" + d.Id + "|0");

        var edit = LastEdit();
        Assert.Equal(["▫️ All libraries", "✅ Movies", "▫️ Shows", "Back"], Labels(edit));
        Assert.Contains("(removed)", (string)edit.Body["text"]!, StringComparison.Ordinal);

        await Click("1|dlt|" + d.Id + "|" + shows.Id.ToString("N"));
        Assert.Equal([movies.Id.ToString("N"), gone, shows.Id.ToString("N")], _h.Settings.Current.Destinations.Single().LibraryIds);

        await Click("1|dlt|" + d.Id + "|all");
        Assert.Empty(_h.Settings.Current.Destinations.Single().LibraryIds);
        Assert.Equal("✅ All libraries", Labels(LastEdit())[0]);
    }

    [Fact]
    public async Task SilentGroups_Toggle()
    {
        var d = _h.AddDestination(-1001);

        await Click("1|dsgt|" + d.Id + "|security");

        Assert.Equal(["security"], _h.Settings.Current.Destinations.Single().SilentGroups);
        Assert.Contains(Labels(LastEdit()), l => l.StartsWith("🔕", StringComparison.Ordinal) && l.Contains("security", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pause_FlipsEnabled()
    {
        var d = _h.AddDestination(-1001);

        await Click("1|dp|" + d.Id);
        Assert.False(_h.Settings.Current.Destinations.Single().Enabled);
        Assert.Contains("Resume", Labels(LastEdit()));

        await Click("1|dp|" + d.Id);
        Assert.True(_h.Settings.Current.Destinations.Single().Enabled);
    }

    [Fact]
    public async Task StaleButton_ForARemovedDestination_DoesNotBumpTheRevision()
    {
        _h.AddDestination(-1001);
        var revision = _h.Settings.Current.Revision;

        await Click("1|dp|zzzzzzzz");

        Assert.Equal(revision, _h.Settings.Current.Revision);
    }

    [Fact]
    public async Task Remove_AsksAboutLeaving_ThenLeavesWhenAsked()
    {
        var d = _h.AddDestination(-1001);
        var other = _h.AddDestination(-1002);

        await Click("1|dr|" + d.Id);
        var ask = LastEdit();
        Assert.Contains("Also make the bot leave the chat?", (string)ask.Body["text"]!, StringComparison.Ordinal);
        Assert.Equal(["Remove", "Remove and leave", "Cancel"], Labels(ask));
        Assert.Equal(2, _h.Settings.Current.Destinations.Length);

        await Click("1|drx|" + d.Id + "|1");

        Assert.Equal([other.Id], _h.Settings.Current.Destinations.Select(x => x.Id));
        var leave = Assert.Single(_h.Api.CallsTo("leaveChat"));
        Assert.Equal(-1001, (long)leave.Body["chat_id"]!);

        await Click("1|drx|" + other.Id + "|0");
        Assert.Empty(_h.Settings.Current.Destinations);
        Assert.Single(_h.Api.CallsTo("leaveChat"));
    }

    [Fact]
    public async Task Remove_IgnoresALeaveFailure()
    {
        var d = _h.AddDestination(-1001);
        _h.Api.On("leaveChat", _ => Fakes.FakeBotApi.Error(403, "Forbidden: bot was kicked"));

        await Click("1|drx|" + d.Id + "|1");

        Assert.Empty(_h.Settings.Current.Destinations);
        Assert.Contains("Destinations</b>", (string)LastEdit().Body["text"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemovedDestination_SaysGone()
    {
        await Click("1|d|zzzzzzzz");
        var edit = LastEdit();
        Assert.Contains("This destination was removed.", (string)edit.Body["text"]!, StringComparison.Ordinal);
        Assert.Equal(["Back"], Labels(edit));

        await Click("1|dp|zzzzzzzz");
        Assert.Contains("This destination was removed.", (string)LastEdit().Body["text"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendTest_ShowsTheResult()
    {
        var d = _h.AddDestination(-1001);

        await Click("1|dt|" + d.Id);
        Assert.Contains("✅", (string)LastEdit().Body["text"]!, StringComparison.Ordinal);
        Assert.Contains("TgJelly test", (string)Assert.Single(_h.Api.CallsTo("sendMessage")).Body["text"]!, StringComparison.Ordinal);

        _h.Api.SetMember(-1001, FakeBotApi.Member("left"));
        await Click("1|dt|" + d.Id);
        var text = (string)LastEdit().Body["text"]!;
        Assert.Contains("❌", text, StringComparison.Ordinal);
        Assert.Contains("Lost", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAgain_ShowsTheNewBadge()
    {
        var d = _h.AddDestination(-1001);
        _rig.Health.MarkProblem(d.Id, HealthStatus.Lost, "kicked");
        Assert.Contains("Lost", await ClickText("1|d|" + d.Id), StringComparison.Ordinal);

        var text = await ClickText("1|dc|" + d.Id);

        Assert.Contains("OK", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Lost", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestCommand_ListsDestinations()
    {
        var a = _h.AddDestination(-1001);
        var b = _h.AddDestination(-1002);

        await _rig.Deliver(TelegramFixtures.Update("private_text", ("text", "/test")));

        var call = Assert.Single(_h.Api.CallsTo("sendMessage"));
        var data = call.Body["reply_markup"]!["inline_keyboard"]!.AsArray().SelectMany(r => r!.AsArray()).Select(x => (string)x!["callback_data"]!).ToArray();
        Assert.Equal(["1|dt|" + a.Id, "1|dt|" + b.Id, "1|m"], data);
    }

    private async Task Click(string data) => await _rig.Deliver(TelegramFixtures.Update("callback_query", ("data", data), ("message_id", "55")));

    private async Task<string> ClickText(string data)
    {
        await Click(data);
        return (string)LastEdit().Body["text"]!;
    }

    private BotApiCall LastEdit() => _h.Api.CallsTo("editMessageText").Last();

    private static string[] Labels(BotApiCall call)
        => call.Body["reply_markup"]!["inline_keyboard"]!.AsArray().SelectMany(r => r!.AsArray()).Select(b => (string)b!["text"]!).ToArray();

    private static string?[] IconIds(BotApiCall call)
        => [.. call.Body["reply_markup"]!["inline_keyboard"]!.AsArray().SelectMany(row => row!.AsArray()).Select(b => (string?)b!["icon_custom_emoji_id"])];

    private static string[] Data(BotApiCall call)
        => call.Body["reply_markup"]!["inline_keyboard"]!.AsArray().SelectMany(r => r!.AsArray()).Select(b => (string)b!["callback_data"]!).ToArray();

    private static string[] NavLabels(BotApiCall call)
        => Labels(call).Where(l => l is "‹ Prev" or "Next ›").ToArray();
}
