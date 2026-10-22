using Jellyfin.Plugin.TgJelly.Configuration;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Configuration;

public class PluginConfigurationTests
{
    [Fact]
    public void Migrate_DropsDisabledAndEmptyOverrides()
    {
        var c = new PluginConfiguration
        {
            Templates =
            [
                new TemplateOverride { EventKey = "media.movie", Enabled = true, Text = "kept" },
                new TemplateOverride { EventKey = "media.album", Enabled = false, Text = "was switched off" },
                new TemplateOverride { EventKey = "server.started", Enabled = true, Text = "  \n" },
                new TemplateOverride { EventKey = "security.lockout", Enabled = true, Text = string.Empty },
            ],
        };

        c.Normalize();

        var kept = Assert.Single(c.Templates);
        Assert.Equal(("media.movie", true, "kept"), (kept.EventKey, kept.Enabled, kept.Text));
    }
}
