using System;
using Jellyfin.Plugin.TgJelly.Api;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Api;

public sealed class SettingsControllerTests : IAsyncLifetime
{
    private readonly BotTestHarness _h = new();
    private readonly FakeSupervisorControl _supervisor = new();
    private SettingsController _controller = null!;

    public async ValueTask InitializeAsync()
        => _controller = new SettingsController(_h.Settings, _h.Secrets, _h.State, _supervisor, await _h.StartOwnedGateAsync());

    [Fact]
    public void Get_ReturnsSettings_ProxyForDisplay_AndPublicUrlValidity()
    {
        _h.Settings.Update(null, c =>
        {
            c.PublicUrl = "http://jellyfin:8096";
            c.Proxy.Mode = ProxyMode.Socks5;
            c.Proxy.Url = "socks5://10.0.0.2:1080";
        });
        _h.Secrets.Save(new BotSecrets { ProxyPassword = "p" });

        var dto = _controller.GetSettings().Value!;

        Assert.Equal(("http://jellyfin:8096", false), (dto.PublicUrl, dto.PublicUrlValid));
        Assert.Equal(("Socks5", "socks5://10.0.0.2:1080"), (dto.ProxyMode, dto.ProxyUrl));
        Assert.Equal((false, true), (dto.HasProxyUsername, dto.HasProxyPassword));
        Assert.Equal(1, dto.Revision);
    }

    [Fact]
    public void Put_SavesTheSections_AndBumpsTheRevision()
    {
        var request = Request(0);
        request.PublicUrl = " https://jf.example.com ";
        request.Message.ShowPoster = false;
        request.Batching.DigestThreshold = 20;

        var dto = Assert.IsType<SettingsDto>(_controller.PutSettings(request).Value);

        Assert.Equal((1L, "https://jf.example.com", true), (dto.Revision, dto.PublicUrl, dto.PublicUrlValid));
        Assert.False(_h.Settings.Current.Message.ShowPoster);
        Assert.Equal(20, _h.Settings.Current.Batching.DigestThreshold);
    }

    [Fact]
    public void Put_ChangingThePublicUrl_ClearsItsFlag()
    {
        _h.State.Update(s => s.Status.PublicUrlFlag = new PublicUrlFlag { Url = "http://jellyfin" });
        var request = Request(0);
        request.PublicUrl = "https://jf.example.com";

        _controller.PutSettings(request);

        Assert.Null(_h.State.Read(s => s.Status.PublicUrlFlag));
    }

    [Fact]
    public void Put_OutOfRange_Is400_WithFieldErrors()
    {
        var request = Request(0);
        request.Batching.DigestThreshold = 1;

        var bad = Assert.IsType<BadRequestObjectResult>(_controller.PutSettings(request).Result);

        Assert.Equal("Batching.DigestThreshold", Assert.Single(Assert.IsType<ErrorsDto>(bad.Value).Errors).Field);
        Assert.Equal(0, _h.Settings.Current.Revision);
    }

    [Fact]
    public void Put_StaleRevision_Is409_WithCurrentData()
    {
        _h.Settings.Update(null, c => c.PublicUrl = "https://a.example.com");

        var conflict = Assert.IsType<ConflictObjectResult>(_controller.PutSettings(Request(0)).Result);

        var current = Assert.IsType<ConflictDto<SettingsDto>>(conflict.Value).Current;
        Assert.Equal(("https://a.example.com", 1L), (current.PublicUrl, current.Revision));
    }

    [Fact]
    public void Put_IgnoresProxy()
    {
        _h.Settings.Update(null, c => c.Proxy.Mode = ProxyMode.None);

        _controller.PutSettings(Request(1));

        Assert.Equal(ProxyMode.None, _h.Settings.Current.Proxy.Mode);
        Assert.Equal(0, _supervisor.Restarts);
    }

    [Fact]
    public void PutProxy_SavesModeAndUrl_KeepsOmittedCredentials_ClearsEmptyOnes_AndRestarts()
    {
        _h.Secrets.Save(new BotSecrets { BotToken = "t", ProxyUsername = "u", ProxyPassword = "p" });

        var dto = Assert.IsType<SettingsDto>(_controller.PutProxy(new ProxyRequest { ExpectedRevision = 0, Mode = "Http", Url = "proxy.lan:3128", Username = null, Password = string.Empty }).Value);

        Assert.Equal(("Http", "proxy.lan:3128"), (dto.ProxyMode, dto.ProxyUrl));
        var secrets = _h.Secrets.Load();
        Assert.Equal(("t", "u", string.Empty), (secrets.BotToken, secrets.ProxyUsername, secrets.ProxyPassword));
        Assert.Equal(1, _supervisor.Restarts);
    }

    [Fact]
    public void PutProxy_Socks5WithoutAUrl_Is400()
    {
        var bad = Assert.IsType<BadRequestObjectResult>(_controller.PutProxy(new ProxyRequest { ExpectedRevision = 0, Mode = "Socks5", Url = string.Empty }).Result);

        Assert.Equal("Proxy.Url", Assert.Single(Assert.IsType<ErrorsDto>(bad.Value).Errors).Field);
        Assert.Equal(0, _supervisor.Restarts);
    }

    [Fact]
    public void PutProxy_UnknownMode_Is400()
    {
        var bad = Assert.IsType<BadRequestObjectResult>(_controller.PutProxy(new ProxyRequest { ExpectedRevision = 0, Mode = "Carrier pigeon" }).Result);

        Assert.Equal("Proxy.Mode", Assert.Single(Assert.IsType<ErrorsDto>(bad.Value).Errors).Field);
    }

    [Fact]
    public void PutProxy_Unchanged_DoesNotRestart()
    {
        var result = _controller.PutProxy(new ProxyRequest { ExpectedRevision = 0, Mode = "System" });

        Assert.IsType<SettingsDto>(result.Value);
        Assert.Equal(0, _supervisor.Restarts);
        Assert.Equal(0, _h.Settings.Current.Revision);
    }

    [Fact]
    public void PutProxy_StaleRevision_Is409()
        => Assert.IsType<ConflictObjectResult>(_controller.PutProxy(new ProxyRequest { ExpectedRevision = 5, Mode = "None" }).Result);

    [Fact]
    public void PutProxy_WithCredentialsInTheUrl_Is400_AndStoresNothing()
    {
        var bad = Assert.IsType<BadRequestObjectResult>(_controller.PutProxy(new ProxyRequest { ExpectedRevision = 0, Mode = "Http", Url = "http://bob:secret@proxy.lan:3128" }).Result);

        Assert.Equal("Proxy.Url", Assert.Single(Assert.IsType<ErrorsDto>(bad.Value).Errors).Field);
        Assert.DoesNotContain("secret", _h.Settings.Current.Proxy.Url, StringComparison.Ordinal);
        Assert.Equal(0, _h.Settings.Current.Revision);
    }

    [Fact]
    public void Mutations_BeforeOwnership_Are503()
    {
        using var gate = new InstanceGate(_h.Paths, _h.Time, Microsoft.Extensions.Logging.Abstractions.NullLogger<InstanceGate>.Instance);
        var controller = new SettingsController(_h.Settings, _h.Secrets, _h.State, _supervisor, gate);

        Assert.Equal(503, Assert.IsType<ObjectResult>(controller.PutProxy(new ProxyRequest { ExpectedRevision = 0, Mode = "None" }).Result).StatusCode);
        Assert.Equal(503, Assert.IsType<ObjectResult>(controller.PutSettings(Request(0)).Result).StatusCode);
        Assert.Equal(0, _h.Settings.Current.Revision);
        Assert.NotNull(controller.GetSettings().Value);
    }

    public ValueTask DisposeAsync()
    {
        _h.Dispose();
        return ValueTask.CompletedTask;
    }

    private static SettingsUpdateRequest Request(long revision) => new()
    {
        ExpectedRevision = revision,
        PublicUrl = string.Empty,
        Message = new MessageOptions(),
        Batching = new BatchingOptions(),
        UpdateCheck = new UpdateCheckOptions(),
    };
}
