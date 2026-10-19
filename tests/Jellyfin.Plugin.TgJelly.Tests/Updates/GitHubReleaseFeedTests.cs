using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Jellyfin.Plugin.TgJelly.Updates;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Updates;

public sealed class GitHubReleaseFeedTests : IDisposable
{
    private const string Json = """
        [
          {"tag_name":"v12.2.0","draft":false,"prerelease":false,"html_url":"https://github.com/jellyfin/jellyfin/releases/tag/v12.2.0","name":"12.2.0"},
          {"tag_name":"v12.3.0-rc1","draft":false,"prerelease":true,"html_url":"https://example.com/rc"},
          {"tag_name":null,"draft":true}
        ]
        """;

    private readonly BotTestHarness _h = new();
    private readonly GitHubReleaseFeed _feed;

    public GitHubReleaseFeedTests() => _feed = new GitHubReleaseFeed(_h.Settings, _h.Secrets);

    [Fact]
    public async Task SendsUserAgent_AndParses()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Json);
        _feed.HandlerOverride = () => handler;

        var releases = await _feed.GetReleasesAsync(TestContext.Current.CancellationToken);

        var request = handler.Request!;
        Assert.Equal(GitHubReleaseFeed.Url, request.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("TgJelly/" + typeof(Plugin).Assembly.GetName().Version, request.Headers.UserAgent.ToString());
        Assert.Equal("application/vnd.github+json", request.Headers.Accept.Single().MediaType);
        Assert.Equal(
            [
                new GitHubRelease("v12.2.0", false, false, "https://github.com/jellyfin/jellyfin/releases/tag/v12.2.0"),
                new GitHubRelease("v12.3.0-rc1", false, true, "https://example.com/rc"),
                new GitHubRelease(string.Empty, true, false, string.Empty),
            ],
            releases);
        Assert.True(handler.Disposed);
    }

    [Fact]
    public async Task UsesTheBotsProxySettings()
    {
        Assert.Equal(SettingsUpdateStatus.Saved, _h.Settings.Update(null, c => c.Proxy = new ProxySettings { Mode = ProxyMode.Http, Url = "proxy.example.com:3128" }).Status);
        HttpMessageHandler? built = null;
        _feed.HandlerBuilder = (proxy, secrets) =>
        {
            built = TelegramClientFactory.CreateHandler(proxy, secrets);
            return new StubHandler(HttpStatusCode.OK, "[]");
        };

        Assert.Empty(await _feed.GetReleasesAsync(TestContext.Current.CancellationToken));

        using var handler = Assert.IsType<SocketsHttpHandler>(built);
        Assert.True(handler.UseProxy);
        Assert.Equal(new Uri("http://proxy.example.com:3128/"), handler.Proxy!.GetProxy(new Uri(GitHubReleaseFeed.Url)));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Non200_Throws(HttpStatusCode status)
    {
        _feed.HandlerOverride = () => new StubHandler(status, "{\"message\":\"API rate limit exceeded\"}");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => _feed.GetReleasesAsync(TestContext.Current.CancellationToken));

        Assert.Equal(status, ex.StatusCode);
    }

    public void Dispose() => _h.Dispose();

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public bool Disposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
