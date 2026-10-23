using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Bot.Screens;

public sealed class SettingsScreensTests : IDisposable, IAsyncLifetime
{
    private const long Chat = 42;
    private readonly BotTestHarness _h = new();
    private BotRig _rig = null!;

    public async ValueTask InitializeAsync() => _rig = await BotRig.CreateAsync(_h);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task MessageStyle_SectionThenMessageThenSwitches()
    {
        await Click("1|sm");
        var sections = LastEdit();
        Assert.Equal(["Media", "Security", "Server", "Back"], Labels(sections));
        Assert.Equal(["1|sms|media", "1|sms|security", "1|sms|server", "1|s"], Data(sections));
        sections.Explains("Media", "Security", "Server");

        await Click("1|sms|media");
        var messages = LastEdit();
        Assert.Equal(["Movies", "Episodes", "Albums", "Music videos", "Books", "Digest", "⬜ Announce upgraded files", "Back"], Labels(messages));
        Assert.Equal("1|sm", Data(messages)[^1]);
        Assert.Contains("Announce upgraded files", (string)messages.Body["text"]!, StringComparison.Ordinal);

        await Click("1|smk|media.episodes");
        var switches = LastEdit();
        Assert.Equal(["✅ Poster", "✅ Open in Jellyfin button", "✅ IMDb and TMDb buttons", "Back"], Labels(switches));
        Assert.Equal("1|sms|media", Data(switches)[^1]);
        switches.Explains("Poster", "Open in Jellyfin button", "IMDb and TMDb buttons");

        await Click("1|smt|media.episodes|ShowPoster");

        var message = _h.Settings.Current.Message;
        Assert.False(message.For("media.episodes").ShowPoster);
        Assert.True(message.For("media.episodes").ShowOpenInJellyfin);
        foreach (var other in new[] { "media.movie", "media.album", "media.musicvideo", "media.book", "media.digest" })
        {
            Assert.True(message.For(other).ShowPoster, other);
        }

        Assert.StartsWith("⬜ Poster", Labels(LastEdit())[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task MessageStyle_OnlyTheApplicableSwitchesAreShown()
    {
        await Click("1|smk|media.movie");
        Assert.Equal(["✅ Poster", "✅ Overview", "✅ Rating and genres", "✅ Open in Jellyfin button", "✅ IMDb and TMDb buttons", "Back"], Labels(LastEdit()));
        await Click("1|smk|media.album");
        Assert.Equal(["✅ Poster", "✅ Open in Jellyfin button", "Back"], Labels(LastEdit()));
        await Click("1|smk|media.book");
        Assert.DoesNotContain(Labels(LastEdit()), l => l.Contains("IMDb", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MessageStyle_SecurityShowsOnlyPoster()
    {
        await Click("1|sms|security");
        var messages = LastEdit();
        Assert.Equal(["Failed logins", "Lockouts", "Successful logins", "User created", "User deleted", "Password changed", "Back"], Labels(messages));
        Assert.Equal("1|sm", Data(messages)[^1]);

        await Click("1|smk|security.lockout");
        var switches = LastEdit();
        Assert.Equal(["⬜ Poster", "Back"], Labels(switches));
        Assert.Equal("1|sms|security", Data(switches)[^1]);

        await Click("1|smt|security.lockout|ShowPoster");

        Assert.True(_h.Settings.Current.Message.For("security.lockout").ShowPoster);
        Assert.False(_h.Settings.Current.Message.For("security.login_failed").ShowPoster);
        Assert.Equal(["✅ Poster", "Back"], Labels(LastEdit()));
    }

    [Fact]
    public async Task MessageStyle_TheDigestHasNoSwitches_AndSaysSo()
    {
        await Click("1|smk|media.digest");

        var edit = LastEdit();
        Assert.Equal(["Back"], Labels(edit));
        Assert.Contains("no switches", (string)edit.Body["text"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MessageStyle_ASwitchThatDoesNotApplyIsNotChanged()
    {
        await Click("1|smt|media.album|ShowProviderLinks");
        await Click("1|smt|security.lockout|ShowOverview");
        await Click("1|smk|nonsense");

        Assert.True(_h.Settings.Current.Message.For("media.album").ShowProviderLinks);
        Assert.False(_h.Settings.Current.Message.For("security.lockout").ShowOverview);
        Assert.Equal(["Media", "Security", "Server", "Back"], Labels(LastEdit()));
    }

    [Fact]
    public async Task AnnounceUpgrades_FlipsFromTheMediaList()
    {
        await Click("1|sms|media");
        await Click("1|smu");

        Assert.True(_h.Settings.Current.Message.AnnounceUpgrades);
        Assert.Contains("✅ Announce upgraded files", Labels(LastEdit()));
    }

    [Fact]
    public async Task UpdateToggles_SaveEachKind()
    {
        await Click("1|suk|NewMajor");
        Assert.Equal((true, true, true), Kinds());
        await Click("1|suk|Fixes");
        await Click("1|suk|NewReleases");
        Assert.Equal((false, false, true), Kinds());
        await Click("1|suk|NewMajor");
        Assert.Equal((false, false, false), Kinds());
        await Click("1|sut");
        Assert.False(_h.Settings.Current.UpdateCheck.Enabled);
        await Click("1|suk|Bogus");
        Assert.Equal((false, false, false), Kinds());
    }

    private (bool, bool, bool) Kinds()
    {
        var u = _h.Settings.Current.UpdateCheck;
        return (u.Fixes, u.NewReleases, u.NewMajor);
    }

    [Fact]
    public async Task SettingsMenu_ListsTheThreeSections()
    {
        await Click("1|s");

        Assert.Equal(["Message style", "Update checks", "Public URL", "Back"], Labels(LastEdit()));
    }

    [Fact]
    public async Task PublicUrl_UseSuggested_SavesTheNormalizedSuggestion()
    {
        _rig.Server.Suggestion = "https://jf.example.com/?x=1";

        await Click("1|sp");
        Assert.Contains("verify", (string)LastEdit().Body["text"]!, StringComparison.OrdinalIgnoreCase);
        await Click("1|spu");

        Assert.Equal("https://jf.example.com", _h.Settings.Current.PublicUrl);
    }

    [Fact]
    public async Task PublicUrl_TypeAnother_Valid_Saves()
    {
        await Click("1|spt");
        Assert.Equal("publicUrl", _rig.Memory.GetFlow(Chat)!.Params["input"]);

        await Text(" http://192.168.1.5:8096/ ");

        Assert.Equal("http://192.168.1.5:8096", _h.Settings.Current.PublicUrl);
        Assert.False(_rig.Memory.GetFlow(Chat)!.Params.ContainsKey("input"));
        Assert.Contains("http://192.168.1.5:8096", (string)_h.Api.CallsTo("sendMessage").Last().Body["text"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublicUrl_TypeAnother_NoDotHost_IsRejectedAndAskedAgain()
    {
        await Click("1|spt");
        var sent = _h.Api.CallsTo("sendMessage").Count();

        await Text("http://jellyfin:8096");

        Assert.Equal(string.Empty, _h.Settings.Current.PublicUrl);
        Assert.Equal("publicUrl", _rig.Memory.GetFlow(Chat)!.Params["input"]);
        var again = _h.Api.CallsTo("sendMessage").Skip(sent).ToList();
        Assert.Single(again);
        Assert.Contains("IPv6", (string)again[0].Body["text"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublicUrl_TypeAnother_Ipv6_IsRejected()
    {
        await Click("1|spt");

        await Text("http://[fd00::1]:8096");

        Assert.Equal(string.Empty, _h.Settings.Current.PublicUrl);
    }

    [Fact]
    public async Task PublicUrl_Clear()
    {
        _h.Settings.Update(null, c => c.PublicUrl = "https://jf.example.com");

        await Click("1|spc");

        Assert.Equal(string.Empty, _h.Settings.Current.PublicUrl);
    }

    [Fact]
    public async Task PublicUrl_ChangedInTheBot_ClearsTheFlag()
    {
        foreach (var (click, typed) in new[] { ("1|spu", false), ("1|spc", false), ("1|spt", true) })
        {
            _rig.Server.Suggestion = "https://jf.example.com";
            _h.Settings.Update(null, c => c.PublicUrl = "https://old.example.com");
            _h.State.Update(s => s.Status.PublicUrlFlag = new PublicUrlFlag { Url = "https://old.example.com", At = _h.Time.GetUtcNow() });

            await Click(click);
            if (typed)
            {
                await Text("https://new.example.com");
            }

            Assert.Null(_h.State.Read(s => s.Status.PublicUrlFlag));
        }
    }

    [Fact]
    public async Task TypedUrlWithoutAFlow_IsTreatedAsUnknownText()
    {
        await Text("https://jf.example.com");

        Assert.Equal(string.Empty, _h.Settings.Current.PublicUrl);
        Assert.Contains("Menu", (string)_h.Api.CallsTo("sendMessage").Last().Body["text"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACommandEndsTheUrlPrompt()
    {
        await Click("1|spt");

        await Text("/menu");
        await Text("https://jf.example.com");

        Assert.Equal(string.Empty, _h.Settings.Current.PublicUrl);
    }

    [Fact]
    public async Task TypeAnother_EditsToAnInlinePrompt_WithCancel()
    {
        await Click("1|spt");

        var edit = Assert.Single(_h.Api.CallsTo("editMessageText"));
        Assert.Equal(55, (int)edit.Body["message_id"]!);
        var text = (string)edit.Body["text"]!;
        Assert.Contains("Send the public URL as a message.", text, StringComparison.Ordinal);
        Assert.Contains("must start with http", text, StringComparison.Ordinal);
        Assert.Equal(["1|sp"], Data(edit));
        edit.Explains("Cancel");
        Assert.DoesNotContain(_h.Api.Calls, c => c.Body.ToJsonString().Contains("force_reply", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidUrl_SendsThePromptAgain_AsANewMessage()
    {
        await Click("1|spt");

        await Text("jellyfin");

        var again = _h.Api.CallsTo("sendMessage").Last();
        var text = (string)again.Body["text"]!;
        Assert.StartsWith("not usable", text, StringComparison.Ordinal);
        Assert.Contains("Send the public URL as a message.", text, StringComparison.Ordinal);
        Assert.Equal(["1|sp"], Data(again));
        await Text("https://jf.example.com");
        Assert.Equal("https://jf.example.com", _h.Settings.Current.PublicUrl);
    }

    [Fact]
    public async Task PromptCancel_StopsWaiting()
    {
        await Click("1|spt");
        await Click("1|sp");

        await Text("https://jf.example.com");

        Assert.Equal(string.Empty, _h.Settings.Current.PublicUrl);
        Assert.Contains("Menu", (string)_h.Api.CallsTo("sendMessage").Last().Body["text"]!, StringComparison.Ordinal);
    }

    private static string[] Data(BotApiCall call)
        => call.Body["reply_markup"]!["inline_keyboard"]!.AsArray().SelectMany(r => r!.AsArray()).Select(b => (string)b!["callback_data"]!).ToArray();

    [Fact]
    public async Task SettingsMenu_ExplainsEachButton()
    {
        await Click("1|s");

        var edit = LastEdit();
        edit.Explains("Message style", "Update checks", "Public URL");
        var text = (string)edit.Body["text"]!;
        Assert.Contains("<b>Message style</b> — what notifications show: poster, overview and buttons", text, StringComparison.Ordinal);
        Assert.Contains("<b>Update checks</b> — turn Jellyfin release checks on or off, and choose which kinds count", text, StringComparison.Ordinal);
        Assert.Contains("<b>Public URL</b> — the address the Open in Jellyfin button uses", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MessageStyle_SaysWhatTappingDoes()
    {
        await Click("1|smk|media.movie");

        var text = (string)LastEdit().Body["text"]!;
        Assert.Contains("Tap a switch to turn it on (✅) or off (⬜).", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MessageStyle_EveryButtonHasAnExplanationLine()
    {
        foreach (var (click, labels) in new[]
        {
            ("1|sm", new[] { "Media", "Security", "Server", "Back" }),
            ("1|sms|media", new[] { "Movies", "Episodes", "Albums", "Music videos", "Books", "Digest", "Announce upgraded files", "Back" }),
            ("1|sms|security", new[] { "Failed logins", "Lockouts", "Successful logins", "User created", "User deleted", "Password changed", "Back" }),
            ("1|sms|server", new[] { "Started", "Update available", "Plugin installed", "Plugin updated", "Plugin uninstalled", "Plugin install failed", "Task failed", "Restart pending", "Back" }),
            ("1|smk|media.movie", new[] { "Poster", "Overview", "Rating and genres", "Open in Jellyfin button", "IMDb and TMDb buttons", "Back" }),
            ("1|smk|media.digest", new[] { "Back" }),
        })
        {
            await Click(click);
            var edit = LastEdit();
            edit.Explains(labels);
            var text = (string)edit.Body["text"]!;
            Assert.Equal(labels.Length, text.Split('\n').Count(l => l.StartsWith("<b>", StringComparison.Ordinal) && l.Contains("</b> — ", StringComparison.Ordinal)));
            Assert.Equal(Labels(edit).Length, labels.Length);
        }
    }

    [Fact]
    public async Task UpdatesScreen_SaysJellyfinReleases()
    {
        await Click("1|su");

        var text = (string)LastEdit().Body["text"]!;
        Assert.Contains("Jellyfin releases", text, StringComparison.Ordinal);
        Assert.DoesNotContain("plugin release", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdatesScreen_ThreeTogglesExplained()
    {
        await Click("1|su");

        var edit = LastEdit();
        Assert.Equal(["✅ Check for updates", "✅ Fixes for my version", "✅ New releases", "⬜ New major versions", "Back"], Labels(edit));
        edit.Explains("Check for updates", "Fixes for my version", "New releases", "New major versions");
        var text = (string)edit.Body["text"]!;
        Assert.Contains("<b>Fixes for my version</b> — 10.11.1 to 10.11.2", text, StringComparison.Ordinal);
        Assert.Contains("<b>New releases</b> — 10.11 to 10.12", text, StringComparison.Ordinal);
        Assert.Contains("<b>New major versions</b> — 10.x to 12.0", text, StringComparison.Ordinal);
        Assert.Contains("all three off", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PublicUrl_ExplainsOnlyTheButtonsShown()
    {
        await Click("1|sp");

        var bare = (string)LastEdit().Body["text"]!;
        Assert.Contains("<b>Type another</b> — send me a different address", bare, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>Use the suggested URL</b>", bare, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>Clear</b>", bare, StringComparison.Ordinal);

        _rig.Server.Suggestion = "https://jf.example.com";
        _h.Settings.Update(null, c => c.PublicUrl = "https://old.example.com");
        await Click("1|sp");

        var full = LastEdit();
        full.Explains("Use the suggested URL", "Type another", "Clear");
        var text = (string)full.Body["text"]!;
        Assert.Contains("<b>Use the suggested URL</b> — save the suggested address", text, StringComparison.Ordinal);
        Assert.Contains("<b>Clear</b> — remove the address; posts lose the Open in Jellyfin button", text, StringComparison.Ordinal);
    }

    private async Task Click(string data) => await _rig.Deliver(TelegramFixtures.Update("callback_query", ("data", data), ("message_id", "55")));

    private async Task Text(string text) => await _rig.Deliver(TelegramFixtures.Update("private_text", ("text", System.Text.Json.JsonEncodedText.Encode(text).ToString())));

    private BotApiCall LastEdit() => _h.Api.CallsTo("editMessageText").Last();

    private static string[] Labels(BotApiCall call)
        => call.Body["reply_markup"]!["inline_keyboard"]!.AsArray().SelectMany(r => r!.AsArray()).Select(b => (string)b!["text"]!).ToArray();
}
