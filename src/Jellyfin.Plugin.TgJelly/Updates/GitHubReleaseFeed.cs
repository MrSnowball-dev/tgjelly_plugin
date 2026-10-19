using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.State;

namespace Jellyfin.Plugin.TgJelly.Updates;

/// <summary>
/// Reads Jellyfin's releases from the GitHub API (spec §7.6), through the bot's proxy settings.
/// </summary>
public sealed class GitHubReleaseFeed : IReleaseFeed
{
    /// <summary>The releases endpoint.</summary>
    public const string Url = "https://api.github.com/repos/jellyfin/jellyfin/releases?per_page=20";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly SettingsStore _settings;
    private readonly SecretsStore _secrets;

    /// <summary>
    /// Initializes a new instance of the <see cref="GitHubReleaseFeed"/> class.
    /// </summary>
    /// <param name="settings">The settings (proxy).</param>
    /// <param name="secrets">The secrets (proxy credentials).</param>
    public GitHubReleaseFeed(SettingsStore settings, SecretsStore secrets)
    {
        _settings = settings;
        _secrets = secrets;
    }

    /// <summary>Gets or sets a handler used instead of a proxied one (tests).</summary>
    internal Func<HttpMessageHandler>? HandlerOverride { get; set; }

    /// <summary>Gets or sets how the proxied handler is built (tests capture its arguments).</summary>
    internal Func<ProxySettings, BotSecrets, HttpMessageHandler> HandlerBuilder { get; set; } = TelegramClientFactory.CreateHandler;

    /// <inheritdoc />
    /// <exception cref="HttpRequestException">GitHub answered with a non-success status, or the network failed.</exception>
    public async Task<IReadOnlyList<GitHubRelease>> GetReleasesAsync(CancellationToken ct)
    {
        var handler = HandlerOverride?.Invoke() ?? HandlerBuilder(_settings.Current.Proxy, _secrets.Load());
        using var client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout };
        using var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("TgJelly", typeof(Plugin).Assembly.GetName().Version?.ToString()));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException("GitHub answered " + (int)response.StatusCode + " " + response.ReasonPhrase, null, response.StatusCode);
        }

        var releases = await response.Content.ReadFromJsonAsync<List<ReleaseDto?>>(ct).ConfigureAwait(false) ?? [];
        return releases.Where(r => r is not null).Select(r => new GitHubRelease(r!.TagName ?? string.Empty, r.Draft, r.Prerelease, r.HtmlUrl ?? string.Empty)).ToList();
    }

    private sealed class ReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }
    }
}
