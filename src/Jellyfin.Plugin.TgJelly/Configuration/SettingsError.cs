namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// One validation problem in a <see cref="PluginConfiguration"/>.
/// </summary>
/// <param name="Field">The field path, e.g. "Batching.QuietWindowSeconds" or "Destinations[1].Id".</param>
/// <param name="Message">What is wrong.</param>
public sealed record SettingsError(string Field, string Message);
