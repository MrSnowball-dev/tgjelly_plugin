using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.Tests.Fakes;

public sealed record CapturedLog(string Category, LogLevel Level, string Message, string Exception);
