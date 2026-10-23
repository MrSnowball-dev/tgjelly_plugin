using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Templates;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Configuration;

public class MessageSwitchesTests
{
    [Fact]
    public void For_UnknownKey_ReturnsDefaults()
    {
        var options = new MessageOptions();
        options.Change("media.movie", s => s.ShowPoster = false);

        // No entry yet: the key's own defaults.
        Assert.True(options.For("media.episodes").ShowPoster);
        Assert.True(options.For("media.digest").ShowOverview);
        Assert.False(options.For("security.lockout").ShowPoster);
        Assert.False(options.For("server.started").ShowOpenInJellyfin);

        // A key from a newer version: its group's defaults.
        Assert.True(options.For("media.podcast").ShowPoster);
        Assert.False(options.For("server.future").ShowPoster);

        // For returns a copy: changing it changes nothing saved.
        options.For("media.movie").ShowPoster = true;
        Assert.False(options.For("media.movie").ShowPoster);
    }

    [Fact]
    public void Keys_AreTheTemplateKeys()
        => Assert.Equal(TemplateKeys.All, MessageOptions.Keys);

    [Fact]
    public void Applicable_DigestHonoursNoSwitch_AlertsOnlyPoster()
    {
        // A digest is text only (NotificationRenderer) and has no overview, rating or genres fields.
        Assert.Empty(MessageSwitches.Applicable("media.digest"));
        Assert.Equal(MessageSwitches.Names, MessageSwitches.Applicable("media.movie"));
        Assert.Equal(["ShowPoster", "ShowOpenInJellyfin", "ShowProviderLinks"], MessageSwitches.Applicable("media.episodes"));
        Assert.Equal(["ShowPoster", "ShowOpenInJellyfin"], MessageSwitches.Applicable("media.album"));
        Assert.Equal(["ShowPoster", "ShowOverview", "ShowRatingGenres", "ShowOpenInJellyfin"], MessageSwitches.Applicable("media.musicvideo"));
        Assert.Equal(["ShowPoster", "ShowOverview", "ShowRatingGenres", "ShowOpenInJellyfin"], MessageSwitches.Applicable("media.book"));
        foreach (var key in EventCatalog.AllKeys.Where(k => EventCatalog.GroupOf(k) != "media"))
        {
            Assert.Equal(["ShowPoster"], MessageSwitches.Applicable(key));
        }
    }

    [Fact]
    public void Applicable_OverviewAndRating_MatchTheTemplateFields()
    {
        // TemplateModelBuilder empties these fields only where the message has them.
        foreach (var key in TemplateKeys.All.Where(k => TemplateFieldCatalog.IsMedia(k)))
        {
            var fields = TemplateFieldCatalog.For(key).Select(f => f.Name).ToList();
            Assert.Equal(fields.Contains("overview"), MessageSwitches.Applicable(key).Contains("ShowOverview"));
            Assert.Equal(fields.Contains("genres"), MessageSwitches.Applicable(key).Contains("ShowRatingGenres"));
        }
    }

    [Fact]
    public void GetAndSet_ByName()
    {
        var s = new MessageSwitches();
        foreach (var name in MessageSwitches.Names)
        {
            s.Set(name, false);
            Assert.False(s.Get(name));
        }

        Assert.Equal((false, false, false, false, false), (s.ShowPoster, s.ShowOverview, s.ShowRatingGenres, s.ShowOpenInJellyfin, s.ShowProviderLinks));
    }
}
