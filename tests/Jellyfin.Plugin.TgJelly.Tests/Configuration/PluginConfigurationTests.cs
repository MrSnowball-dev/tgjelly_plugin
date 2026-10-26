using Jellyfin.Plugin.TgJelly.Configuration;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Configuration;

public class PluginConfigurationTests
{
    [Fact]
    public void Normalize_DropsEmptyOverrides()
    {
        var c = new PluginConfiguration
        {
            Templates =
            [
                new TemplateOverride { EventKey = "media.movie", Text = "kept" },
                new TemplateOverride { EventKey = "server.started", Text = "  \n" },
                new TemplateOverride { EventKey = "security.lockout", Text = string.Empty },
            ],
        };

        c.Normalize();

        var kept = Assert.Single(c.Templates);
        Assert.Equal(("media.movie", "kept"), (kept.EventKey, kept.Text));
    }
}
