using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.State;
using Telegram.Bot;

namespace Jellyfin.Plugin.TgJelly.Bot;

/// <summary>
/// Builds clients over one <see cref="SocketsHttpHandler"/> per proxy configuration. Not created through
/// <c>IHttpClientFactory</c>, whose logging handlers would log request URLs, which carry the token.
/// </summary>
public sealed class TelegramClientFactory : ITelegramClientFactory, IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(100);

    private readonly SettingsStore _settings;
    private readonly SecretsStore _secrets;
    private readonly BotApiEnvironment _environment;
    private readonly Lock _gate = new();
    private readonly List<SocketsHttpHandler> _retired = [];
    private SocketsHttpHandler? _handler;
    private (ProxyMode Mode, string Url, string User, string Password) _handlerKey;

    /// <summary>
    /// Initializes a new instance of the <see cref="TelegramClientFactory"/> class.
    /// </summary>
    /// <param name="settings">The settings, for the proxy mode and URL.</param>
    /// <param name="secrets">The secrets, for the proxy credentials.</param>
    /// <param name="environment">The base-URL override.</param>
    public TelegramClientFactory(SettingsStore settings, SecretsStore secrets, BotApiEnvironment environment)
    {
        _settings = settings;
        _secrets = secrets;
        _environment = environment;
    }

    /// <inheritdoc />
    public ITelegramBotClient Create(string token)
    {
        var options = CreateOptions(token, _environment);
        var http = new HttpClient(CurrentHandler(), disposeHandler: false) { Timeout = RequestTimeout };
        return new TelegramBotClient(options, http);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _handler?.Dispose();
            _handler = null;
            foreach (var handler in _retired)
            {
                handler.Dispose();
            }

            _retired.Clear();
        }
    }

    /// <summary>
    /// Builds the client options: the base-URL override only when set, and Telegram.Bot's own 429 retries off,
    /// because the Outbox and the supervisor handle <c>retry_after</c> themselves.
    /// </summary>
    /// <param name="token">The bot token.</param>
    /// <param name="environment">The base-URL override.</param>
    /// <returns>The options.</returns>
    internal static TelegramBotClientOptions CreateOptions(string token, BotApiEnvironment environment)
        => new(token, environment.BaseUrl) { RetryThreshold = 0 };

    /// <summary>
    /// Builds the HTTP handler for a proxy configuration.
    /// </summary>
    /// <param name="proxy">The proxy settings.</param>
    /// <param name="secrets">The secrets holding the proxy credentials.</param>
    /// <returns>The handler.</returns>
    internal static SocketsHttpHandler CreateHandler(ProxySettings proxy, BotSecrets secrets)
    {
        // ConnectTimeout: a black-holed network or proxy shows as unreachable in seconds, not after the 100 s timeout.
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(3), ConnectTimeout = TimeSpan.FromSeconds(15) };
        switch (proxy.Mode)
        {
            case ProxyMode.None:
                handler.UseProxy = false;
                break;
            case ProxyMode.Http:
            case ProxyMode.Socks5:
                var scheme = proxy.Mode == ProxyMode.Http ? "http://" : "socks5://";
                var url = proxy.Url.Contains("://", StringComparison.Ordinal) ? proxy.Url : scheme + proxy.Url;
                var webProxy = new WebProxy(url);
                if (!string.IsNullOrEmpty(secrets.ProxyUsername))
                {
                    webProxy.Credentials = new NetworkCredential(secrets.ProxyUsername, secrets.ProxyPassword);
                }

                handler.Proxy = webProxy;
                handler.UseProxy = true;
                break;
            default:
                // System: .NET's default, which follows HTTP_PROXY / HTTPS_PROXY / ALL_PROXY.
                break;
        }

        return handler;
    }

    private SocketsHttpHandler CurrentHandler()
    {
        var proxy = _settings.Current.Proxy;
        var secrets = _secrets.Load();
        var key = (proxy.Mode, proxy.Url, secrets.ProxyUsername, secrets.ProxyPassword);
        lock (_gate)
        {
            if (_handler is null || key != _handlerKey)
            {
                // Clients of the previous configuration may still be finishing a request; dispose them at shutdown.
                if (_handler is not null)
                {
                    _retired.Add(_handler);
                }

                _handler = CreateHandler(proxy, secrets);
                _handlerKey = key;
            }

            return _handler;
        }
    }
}
