using System.IO;
using System.Text;
using System.Xml.Serialization;
using Jellyfin.Plugin.TgJelly.Configuration;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Configuration;

public class PluginConfigurationXmlTests
{
    // Jellyfin's IXmlSerializer is a thin wrapper over System.Xml.Serialization.XmlSerializer (F9).
    private static readonly XmlSerializer Serializer = new(typeof(PluginConfiguration));

    [Fact]
    public void Defaults_MatchSpec()
    {
        var c = new PluginConfiguration();

        Assert.Equal(1, c.SchemaVersion);
        Assert.Equal(0, c.Revision);
        Assert.Equal("en", c.Language);
        Assert.Equal(ProxyMode.System, c.Proxy.Mode);
        Assert.True(c.Message.For("media.movie").ShowPoster);
        Assert.Empty(c.Message.PerMessage);
        Assert.False(c.Message.AnnounceUpgrades);
        Assert.Equal(120, c.Batching.QuietWindowSeconds);
        Assert.Equal(30, c.Batching.MaxHoldMinutes);
        Assert.Equal(20, c.Batching.ReleaseQuietSeconds);
        Assert.Equal(10, c.Batching.DigestThreshold);
        Assert.Equal(6, c.Batching.LateEditHours);
        Assert.Equal(15, c.Batching.ReadyTimeoutMinutes);
        Assert.True(c.UpdateCheck.Enabled);
        c.Normalize();
        Assert.Equal((true, true, false), (c.UpdateCheck.Fixes, c.UpdateCheck.NewReleases, c.UpdateCheck.NewMajor));
        Assert.Empty(c.Destinations);
        Assert.Empty(c.Templates);
    }

    [Fact]
    public void RoundTrip_KeepsEverything()
    {
        var original = new PluginConfiguration
        {
            Revision = 7,
            PublicUrl = "https://jf.example.com",
            Proxy = new ProxySettings { Mode = ProxyMode.Socks5, Url = "socks5://10.0.0.2:1080" },
            Destinations =
            [
                new Destination
                {
                    Id = "abcd1234",
                    ChatId = -1001234567890123,
                    ThreadId = 42,
                    Title = "Movie Club",
                    Kind = ChatKind.Supergroup,
                    Preset = PresetKind.Custom,
                    Events = ["media.movie", "server.started"],
                    LibraryIds = ["0123456789abcdef0123456789abcdef"],
                    SilentGroups = ["media"],
                },
            ],
            Templates = [new TemplateOverride { EventKey = "media.movie", Enabled = true, Text = "{{ title }}" }],
        };

        var copy = RoundTrip(original);

        Assert.Equal(7, copy.Revision);
        Assert.Equal(ProxyMode.Socks5, copy.Proxy.Mode);
        var d = Assert.Single(copy.Destinations);
        Assert.Equal(-1001234567890123, d.ChatId);
        Assert.Equal(42, d.ThreadId);
        Assert.Equal(ChatKind.Supergroup, d.Kind);
        Assert.Equal(PresetKind.Custom, d.Preset);
        Assert.Equal(["media.movie", "server.started"], d.Events);
        Assert.Equal(["media"], d.SilentGroups);
        Assert.Equal("{{ title }}", Assert.Single(copy.Templates).Text);
    }

    [Fact]
    public void Enums_AreWrittenByName()
    {
        var xml = Serialize(new PluginConfiguration { Proxy = new ProxySettings { Mode = ProxyMode.Socks5 } });

        Assert.Contains("<Mode>Socks5</Mode>", xml, System.StringComparison.Ordinal);
    }

    [Fact]
    public void OldXml_WithMissingFields_LoadsDefaults()
    {
        const string Xml = "<?xml version=\"1.0\"?><PluginConfiguration><Revision>5</Revision></PluginConfiguration>";

        using var reader = new StringReader(Xml);
        var c = (PluginConfiguration)Serializer.Deserialize(reader)!;

        Assert.Equal(5, c.Revision);
        Assert.Equal(120, c.Batching.QuietWindowSeconds);
        Assert.Empty(c.Destinations);
    }

    [Fact]
    public void XmlFromANewerVersion_WithUnknownElements_StillLoads()
    {
        // After a downgrade the XML may contain fields this version does not know. If loading threw,
        // Jellyfin would replace the whole config with defaults (F9).
        const string Xml = "<?xml version=\"1.0\"?><PluginConfiguration><Revision>9</Revision><FutureSetting>x</FutureSetting>"
            + "<Batching><QuietWindowSeconds>60</QuietWindowSeconds><FutureNested>1</FutureNested></Batching></PluginConfiguration>";

        using var reader = new StringReader(Xml);
        var c = (PluginConfiguration)Serializer.Deserialize(reader)!;

        Assert.Equal(9, c.Revision);
        Assert.Equal(60, c.Batching.QuietWindowSeconds);
    }

    [Fact]
    public void XmlFromANewerVersion_WithUnknownEnumNames_ReadsSafeFallbacks()
    {
        // An enum member added by a newer version must not make the whole XML fail to load (F9).
        const string Xml = "<?xml version=\"1.0\"?><PluginConfiguration><Revision>3</Revision>"
            + "<Proxy><Mode>Mtproto</Mode></Proxy><UpdateCheck><Mode>Nightly</Mode></UpdateCheck>"
            + "<Destinations><Destination><Id>abcd1234</Id><ChatId>42</ChatId><Kind>Bogus</Kind><Preset>Playback</Preset></Destination></Destinations>"
            + "</PluginConfiguration>";

        using var reader = new StringReader(Xml);
        var c = (PluginConfiguration)Serializer.Deserialize(reader)!;

        Assert.Equal(3, c.Revision);
        Assert.Equal(ProxyMode.System, c.Proxy.Mode);
        c.Normalize();
        Assert.Equal((true, true, false), (c.UpdateCheck.Fixes, c.UpdateCheck.NewReleases, c.UpdateCheck.NewMajor));
        var d = Assert.Single(c.Destinations);
        Assert.Equal(ChatKind.Private, d.Kind);
        Assert.Equal(PresetKind.Custom, d.Preset); // never silently "Everything"
    }

    private static PluginConfiguration RoundTrip(PluginConfiguration c)
    {
        using var reader = new StringReader(Serialize(c));
        return (PluginConfiguration)Serializer.Deserialize(reader)!;
    }

    private static string Serialize(PluginConfiguration c)
    {
        var sb = new StringBuilder();
        using (var writer = new StringWriter(sb, System.Globalization.CultureInfo.InvariantCulture))
        {
            Serializer.Serialize(writer, c);
        }

        return sb.ToString();
    }
}
