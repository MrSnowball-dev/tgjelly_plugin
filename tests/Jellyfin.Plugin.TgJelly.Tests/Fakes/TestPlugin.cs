using System.IO;
using MediaBrowser.Common.Configuration;
using NSubstitute;

namespace Jellyfin.Plugin.TgJelly.Tests.Fakes;

/// <summary>
/// Builds a real <see cref="Plugin"/> whose files live in a temporary folder.
/// </summary>
public static class TestPlugin
{
    public static Plugin Create(string root)
    {
        var paths = Substitute.For<IApplicationPaths>();
        paths.PluginsPath.Returns(Path.Combine(root, "plugins"));
        paths.PluginConfigurationsPath.Returns(Path.Combine(root, "plugins", "configurations"));
        return new Plugin(paths, new TestXmlSerializer());
    }
}
