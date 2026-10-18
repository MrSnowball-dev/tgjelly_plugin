using System;
using Jellyfin.Plugin.TgJelly.Library;

namespace Jellyfin.Plugin.TgJelly.Tests.Fakes;

public sealed class FakeServerInfo : IServerInfo
{
    public string SystemId { get; set; } = "a1b2c3";

    public string ServerName { get; set; } = "Home";

    public string VersionString { get; set; } = "12.2.0";

    public Version Version { get; set; } = new(12, 2, 0, 0);

    public string BaseUrl { get; set; } = string.Empty;

    public string? Suggestion { get; set; }

    public string? SuggestPublicUrl() => Suggestion;
}
