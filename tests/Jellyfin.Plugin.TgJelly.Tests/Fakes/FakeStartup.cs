using Jellyfin.Plugin.TgJelly.Bot;

namespace Jellyfin.Plugin.TgJelly.Tests.Fakes;

public sealed class FakeStartup : IServerStartup
{
    public bool HasCompleted { get; set; } = true;
}
