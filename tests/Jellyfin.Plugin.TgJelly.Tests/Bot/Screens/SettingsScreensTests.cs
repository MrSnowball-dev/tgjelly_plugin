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
    public async Task MessageToggle_FlipsOnlyThatOption()
    {
        var before = _h.Settings.Current.Message;
        Assert.True(before.For("media.movie").ShowPoster);
        Assert.False(before.AnnounceUpgrades);

        await Click("1|smt|ShowPoster");
        await Click("1|smt|AnnounceUpgrades");

        var message = _h.Settings.Current.Message;
        Assert.True(message.AnnounceUpgrades);
        Assert.False(message.For("security.lockout").ShowPoster);
        foreach (var key in new[] { "media.movie", "media.episodes", "media.album", "media.musicvideo", "media.book", "media.digest" })
        {
            // Until the screen picks a message (Task 7), a change applies to every media message, as v0.4's one set did.
            var after = message.For(key);
            Assert.False(after.ShowPoster, key);
            Assert.True(after.ShowOverview);
            Assert.True(after.ShowRatingGenres);
            Assert.True(after.ShowOpenInJellyfin);
            Assert.True(after.ShowProviderLinks);
        }

        Assert.Equal(7, Labels(LastEdit()).Length);
    }

    [Fact]
    public async Task UpdateMode_Switches()
    {
        await Click("1|sum|AnyNewer");
        Assert.Equal(UpdateCheckMode.AnyNewer, _h.Settings.Current.UpdateCheck.Mode);
        await Click("1|sum|SameMajor");
        Assert.Equal(UpdateCheckMode.SameMajor, _h.Settings.Current.UpdateCheck.Mode);
        await Click("1|sut");
        Assert.False(_h.Settings.Current.UpdateCheck.Enabled);
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
        Assert.Contains("<b>Update checks</b> — turn release checks on or off, and choose which versions count", text, StringComparison.Ordinal);
        Assert.Contains("<b>Public URL</b> — the address the Open in Jellyfin button uses", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MessageStyle_SaysWhatTappingDoes()
    {
        await Click("1|sm");

        var text = (string)LastEdit().Body["text"]!;
        Assert.EndsWith("\n\nTap a switch to turn it on (✅) or off (⬜).", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>Back</b>", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Updates_ExplainsEachButton()
    {
        await Click("1|su");

        var edit = LastEdit();
        edit.Explains("Check for updates", "Same major version only", "Any newer version");
        var text = (string)edit.Body["text"]!;
        Assert.Contains("<b>Check for updates</b> — look for new releases and tell you here", text, StringComparison.Ordinal);
        Assert.Contains("<b>Same major version only</b> — only versions with the same major number as yours", text, StringComparison.Ordinal);
        Assert.Contains("<b>Any newer version</b> — every newer version", text, StringComparison.Ordinal);
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
