namespace Jellyfin.Plugin.TgJelly.Updates;

/// <summary>
/// A Jellyfin release on GitHub.
/// </summary>
/// <param name="TagName">The tag, e.g. <c>v10.11.11</c>.</param>
/// <param name="Draft">Whether it is a draft.</param>
/// <param name="Prerelease">Whether it is a prerelease.</param>
/// <param name="HtmlUrl">The release page.</param>
public sealed record GitHubRelease(string TagName, bool Draft, bool Prerelease, string HtmlUrl);
