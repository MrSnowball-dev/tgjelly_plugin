using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Configuration;
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
        Assert.True(before.ShowPoster);
        Assert.False(before.AnnounceUpgrades);

        await Click("1|smt|ShowPoster");
        await Click("1|smt|AnnounceUpgrades");

        var after = _h.Settings.Current.Message;
        Assert.False(after.ShowPoster);
        Assert.True(after.AnnounceUpgrades);
        Assert.True(after.ShowOverview);
        Assert.True(after.ShowRatingGenres);
        Assert.True(after.ShowOpenInJellyfin);
        Assert.True(after.ShowProviderLinks);
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

        Assert.Equal(["🎨 Message style", "🔔 Update checks", "🔗 Public URL", "‹ Back"], Labels(LastEdit()));
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
        var prompt = _h.Api.CallsTo("sendMessage").Last();
        Assert.Contains("force_reply", prompt.Body["reply_markup"]!.ToJsonString(), StringComparison.Ordinal);
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
        Assert.Contains("force_reply", again[0].Body["reply_markup"]!.ToJsonString(), StringComparison.Ordinal);
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

    private async Task Click(string data) => await _rig.Deliver(TelegramFixtures.Update("callback_query", ("data", data), ("message_id", "55")));

    private async Task Text(string text) => await _rig.Deliver(TelegramFixtures.Update("private_text", ("text", System.Text.Json.JsonEncodedText.Encode(text).ToString())));

    private BotApiCall LastEdit() => _h.Api.CallsTo("editMessageText").Last();

    private static string[] Labels(BotApiCall call)
        => call.Body["reply_markup"]!["inline_keyboard"]!.AsArray().SelectMany(r => r!.AsArray()).Select(b => (string)b!["text"]!).ToArray();
}
