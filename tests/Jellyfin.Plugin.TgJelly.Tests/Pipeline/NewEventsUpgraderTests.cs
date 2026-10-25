using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Serialization;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Pipeline;
using Jellyfin.Plugin.TgJelly.State;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Pipeline;

public class NewEventsUpgraderTests
{
    private static readonly string[] AllButOne = [.. EventCatalog.AllKeys.Where(k => k != "server.restart_pending")];

    [Fact]
    public void AFollowingDestination_GetsEventsItLacks()
    {
        var c = Config(follows: true, AllButOne);

        Assert.True(NewEventsUpgrader.Apply(c));
        Assert.Equal(EventCatalog.AllKeys, c.Destinations[0].Events);
        Assert.False(NewEventsUpgrader.Apply(c));
    }

    [Fact]
    public void ANonFollowingDestination_IsLeftAlone()
    {
        var c = Config(follows: false, AllButOne);

        Assert.False(NewEventsUpgrader.Apply(c));
        Assert.Equal(AllButOne, c.Destinations[0].Events);
    }

    [Theory]
    [InlineData("Everything", true)]
    [InlineData("Custom", false)]
    [InlineData("NewMedia", false)]
    [InlineData("bogus", false)]
    public void PresetText_ReadsAsTheFlag(string text, bool follows)
    {
        var xml = $"<Destination><Id>a</Id><Preset>{text}</Preset></Destination>";
        var fromXml = (Destination)new XmlSerializer(typeof(Destination)).Deserialize(new StringReader(xml))!;
        var fromJson = JsonSerializer.Deserialize<Destination>($$"""{"id":"a","preset":"{{text}}"}""", JsonDefaults.Options)!;

        Assert.Equal(follows, fromXml.FollowsNewEvents);
        Assert.Equal(follows, fromJson.FollowsNewEvents);
    }

    [Theory]
    [InlineData(true, "Everything")]
    [InlineData(false, "Custom")]
    public void TheFlag_IsWrittenAsPresetText(bool follows, string text)
    {
        var d = new Destination { Id = "a", FollowsNewEvents = follows };
        var xml = new StringWriter();
        new XmlSerializer(typeof(Destination)).Serialize(xml, d);

        Assert.Contains($"<Preset>{text}</Preset>", xml.ToString(), System.StringComparison.Ordinal);
        Assert.Contains($"\"preset\": \"{text}\"", JsonSerializer.Serialize(d, JsonDefaults.Options), System.StringComparison.Ordinal);
    }

    private static PluginConfiguration Config(bool follows, string[] events)
        => new() { Destinations = [new Destination { Id = "abcd1234", FollowsNewEvents = follows, Events = events }] };
}
