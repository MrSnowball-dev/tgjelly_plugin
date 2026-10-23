using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Serialization;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Configuration;

/// <summary>
/// Plan 9 (design §6.2): v0.4's one set of message switches becomes one set per message, and nothing a user sees changes.
/// </summary>
public sealed class MessageOptionsMigrationTests : IDisposable
{
    private static readonly XmlSerializer Serializer = new(typeof(PluginConfiguration));

    private static readonly string[] MediaKeys = ["media.movie", "media.episodes", "media.album", "media.musicvideo", "media.book", "media.digest"];

    private readonly BotTestHarness _h = new();

    [Fact]
    public void Migration_CopiesGlobalTogglesToEveryMediaMessage()
    {
        // A v0.4 settings file: poster off, overview on, and the rest mixed.
        _h.Host.SaveFromStore(Load(V04Xml(poster: false, overview: true, ratingGenres: false, open: true, providers: false, upgrades: true)));

        var loaded = _h.Settings.Current;

        foreach (var key in MediaKeys)
        {
            var s = loaded.Message.For(key);
            Assert.Equal((false, true, false, true, false), (s.ShowPoster, s.ShowOverview, s.ShowRatingGenres, s.ShowOpenInJellyfin, s.ShowProviderLinks));
        }

        foreach (var key in EventCatalog.AllKeys.Where(k => !k.StartsWith("media.", StringComparison.Ordinal)))
        {
            Assert.False(loaded.Message.For(key).ShowPoster, key);
        }

        Assert.True(loaded.Message.AnnounceUpgrades);

        // The first save writes the per-message map, and keeps everything else.
        Assert.Equal(SettingsUpdateStatus.Saved, _h.Settings.Update(null, c => c.Batching.DigestThreshold = 12).Status);
        var xml = Serialize(_h.Host.Configuration);
        var reloaded = Load(xml);

        Assert.Equal(MessageOptions.Keys, reloaded.Message.PerMessage.Select(e => e.EventKey));
        foreach (var key in MediaKeys)
        {
            var s = reloaded.Message.PerMessage.Single(e => e.EventKey == key).Switches;
            Assert.Equal((false, true, false, true, false), (s.ShowPoster, s.ShowOverview, s.ShowRatingGenres, s.ShowOpenInJellyfin, s.ShowProviderLinks));
        }

        Assert.All(reloaded.Message.PerMessage.Where(e => !MediaKeys.Contains(e.EventKey)), e => Assert.False(e.Switches.ShowPoster));
        Assert.True(reloaded.Message.AnnounceUpgrades);
        Assert.Equal(12, reloaded.Batching.DigestThreshold);
        Assert.Equal(60, reloaded.Batching.QuietWindowSeconds);
        Assert.Equal("https://jf.example.com", reloaded.PublicUrl);
        Assert.Equal("abcd1234", Assert.Single(reloaded.Destinations).Id);
        Assert.Equal("{{ title }}", Assert.Single(reloaded.Templates).Text);

        // The v0.4 switches were read once: they are not written back.
        var message = xml[xml.IndexOf("<Message>", StringComparison.Ordinal)..xml.IndexOf("<PerMessage>", StringComparison.Ordinal)];
        Assert.DoesNotContain("ShowPoster", message, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowOverview", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Migration_SecurityAndServerPosterOff()
    {
        var c = Load(V04Xml(poster: true, overview: true, ratingGenres: true, open: true, providers: true, upgrades: false));

        c.Normalize();

        foreach (var key in MessageOptions.Keys)
        {
            var s = c.Message.For(key);
            var media = MediaKeys.Contains(key);
            Assert.Equal((media, media), (s.ShowPoster, s.ShowOverview));
            Assert.Contains(c.Message.PerMessage, e => e.EventKey == key);
        }
    }

    [Fact]
    public void Migration_Idempotent()
    {
        var c = Load(V04Xml(poster: false, overview: true, ratingGenres: true, open: true, providers: true, upgrades: false));
        c.Normalize();
        var first = Serialize(c);

        c.Normalize();
        c.MigrateMessageOptions();

        Assert.Equal(first, Serialize(c));

        // A later change is not undone by the v0.4 values: they were read once.
        c.Message.Change("media.movie", s => s.ShowPoster = true);
        c.Normalize();
        Assert.True(c.Message.For("media.movie").ShowPoster);
        Assert.False(c.Message.For("media.episodes").ShowPoster);
    }

    [Fact]
    public void Migration_NewInstall_KeepsDefaults()
    {
        var c = Load("<?xml version=\"1.0\"?><PluginConfiguration><Revision>1</Revision></PluginConfiguration>");

        c.Normalize();

        Assert.Empty(c.Message.PerMessage);
        Assert.True(c.Message.For("media.movie").ShowPoster);
        Assert.False(c.Message.For("server.started").ShowPoster);
    }

    [Fact]
    public void PerMessage_RoundTripsThroughXml()
    {
        var c = new PluginConfiguration();
        c.Message.Change("media.album", s => s.ShowPoster = false);
        c.Message.Change("security.lockout", s => s.ShowPoster = true);

        var copy = Load(Serialize(c));

        Assert.False(copy.Message.For("media.album").ShowPoster);
        Assert.True(copy.Message.For("media.album").ShowOverview);
        Assert.True(copy.Message.For("security.lockout").ShowPoster);
        Assert.True(copy.Message.For("media.movie").ShowPoster);
    }

    public void Dispose() => _h.Dispose();

    private static string V04Xml(bool poster, bool overview, bool ratingGenres, bool open, bool providers, bool upgrades)
    {
        static string B(bool b) => b ? "true" : "false";
        return "<?xml version=\"1.0\"?><PluginConfiguration><SchemaVersion>1</SchemaVersion><Revision>4</Revision>"
            + "<PublicUrl>https://jf.example.com</PublicUrl>"
            + $"<Message><ShowPoster>{B(poster)}</ShowPoster><ShowOverview>{B(overview)}</ShowOverview>"
            + $"<ShowRatingGenres>{B(ratingGenres)}</ShowRatingGenres><ShowOpenInJellyfin>{B(open)}</ShowOpenInJellyfin>"
            + $"<ShowProviderLinks>{B(providers)}</ShowProviderLinks><AnnounceUpgrades>{B(upgrades)}</AnnounceUpgrades></Message>"
            + "<Batching><QuietWindowSeconds>60</QuietWindowSeconds></Batching>"
            + "<Destinations><Destination><Id>abcd1234</Id><ChatId>-100123</ChatId><Events><string>media.movie</string></Events></Destination></Destinations>"
            + "<Templates><TemplateOverride><EventKey>media.movie</EventKey><Enabled>true</Enabled><Text>{{ title }}</Text></TemplateOverride></Templates>"
            + "</PluginConfiguration>";
    }

    private static PluginConfiguration Load(string xml)
    {
        using var reader = new StringReader(xml);
        return (PluginConfiguration)Serializer.Deserialize(reader)!;
    }

    private static string Serialize(PluginConfiguration c)
    {
        var sb = new StringBuilder();
        using (var writer = new StringWriter(sb, CultureInfo.InvariantCulture))
        {
            Serializer.Serialize(writer, c);
        }

        return sb.ToString();
    }
}
