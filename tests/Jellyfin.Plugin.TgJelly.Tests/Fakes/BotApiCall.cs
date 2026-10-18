using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.TgJelly.Tests.Fakes;

/// <summary>
/// One request the fake Bot API received: the method and its parameters (multipart file parts as <c>"file:&lt;name&gt;"</c>).
/// </summary>
public sealed record BotApiCall(string Method, JsonObject Body);
