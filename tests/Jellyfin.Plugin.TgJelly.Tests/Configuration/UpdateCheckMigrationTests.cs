using System.Globalization;
using System.IO;
using System.Text;
using System.Xml.Serialization;
using Jellyfin.Plugin.TgJelly.Configuration;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Configuration;

/// <summary>
/// Plan 9 (design §7): v0.4's update mode becomes three switches, and nothing a user sees changes.
/// </summary>
public sealed class UpdateCheckMigrationTests : System.IDisposable
{
    private readonly Fakes.BotTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static readonly XmlSerializer Serializer = new(typeof(PluginConfiguration));

    [Fact]
    public void UpdateMigration_SameMajor_FixesAndNewReleases()
    {
        var c = Load(Xml("<Enabled>true</Enabled><Mode>SameMajor</Mode>"));

        c.Normalize();

        Assert.True(c.UpdateCheck.Enabled);
        Assert.Equal((true, true, false), (c.UpdateCheck.Fixes, c.UpdateCheck.NewReleases, c.UpdateCheck.NewMajor));
    }

    [Fact]
    public void UpdateMigration_AnyNewer_AllThree()
    {
        var c = Load(Xml("<Enabled>false</Enabled><Mode>AnyNewer</Mode>"));

        c.Normalize();

        Assert.False(c.UpdateCheck.Enabled);
        Assert.Equal((true, true, true), (c.UpdateCheck.Fixes, c.UpdateCheck.NewReleases, c.UpdateCheck.NewMajor));
    }

    [Fact]
    public void UpdateMigration_NoMode_UsesTheSameMajorDefaults()
    {
        var c = Load(Xml("<Enabled>true</Enabled>"));

        c.Normalize();

        Assert.Equal((true, true, false), (c.UpdateCheck.Fixes, c.UpdateCheck.NewReleases, c.UpdateCheck.NewMajor));
    }

    [Fact]
    public void UpdateMigration_ModeIsReadOnce_ThenIgnored()
    {
        var c = Load(Xml("<Mode>AnyNewer</Mode>"));
        c.Normalize();
        c.UpdateCheck.NewMajor = false;
        c.UpdateCheck.Fixes = false;

        var xml = Serialize(c);
        var reloaded = Load(xml);
        reloaded.Normalize();

        Assert.DoesNotContain("<Mode>", xml[xml.IndexOf("<UpdateCheck>", System.StringComparison.Ordinal)..xml.IndexOf("</UpdateCheck>", System.StringComparison.Ordinal)], System.StringComparison.Ordinal);
        Assert.Equal((false, true, false), (reloaded.UpdateCheck.Fixes, reloaded.UpdateCheck.NewReleases, reloaded.UpdateCheck.NewMajor));
    }

    [Fact]
    public void UpdateMigration_ThreeSwitchesRoundTrip()
    {
        var c = new PluginConfiguration();
        c.UpdateCheck.Fixes = false;
        c.UpdateCheck.NewReleases = false;
        c.UpdateCheck.NewMajor = true;

        var copy = Load(Serialize(c));
        copy.Normalize();

        Assert.Equal((false, false, true), (copy.UpdateCheck.Fixes, copy.UpdateCheck.NewReleases, copy.UpdateCheck.NewMajor));
    }

    [Fact]
    public void UpdateMigration_ThroughStore_AnyNewerHasNewMajorOn()
    {
        _h.Host.SaveFromStore(Load(Xml("<Enabled>true</Enabled><Mode>AnyNewer</Mode>")));

        var u = _h.Settings.Current.UpdateCheck;

        Assert.Equal((true, true, true), (u.Fixes, u.NewReleases, u.NewMajor));
    }

    [Fact]
    public void UpdateMigration_FirstToggleAfterUpgrade_Sticks()
    {
        _h.Host.SaveFromStore(Load(Xml("<Mode>AnyNewer</Mode>")));

        Assert.Equal(SettingsUpdateStatus.Saved, _h.Settings.Update(null, c => c.UpdateCheck.Fixes = false).Status);

        Assert.Equal((false, true, true), Kinds(_h.Settings.Current.UpdateCheck));
        var reloaded = Load(Serialize(_h.Host.Configuration));
        _h.Host.SaveFromStore(reloaded);
        Assert.Equal((false, true, true), Kinds(_h.Settings.Current.UpdateCheck));
    }

    private static (bool, bool, bool) Kinds(UpdateCheckOptions u) => (u.Fixes, u.NewReleases, u.NewMajor);

    private static string Xml(string updateCheck)
        => "<?xml version=\"1.0\"?><PluginConfiguration><SchemaVersion>1</SchemaVersion><Revision>4</Revision>"
            + "<UpdateCheck>" + updateCheck + "</UpdateCheck></PluginConfiguration>";

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
