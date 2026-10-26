using System;
using System.Net;
using Jellyfin.Plugin.TgJelly.Library;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Library;

public class JellyfinServerInfoTests
{
    private readonly IServerApplicationHost _host = Substitute.For<IServerApplicationHost>();
    private readonly IServerConfigurationManager _config = Substitute.For<IServerConfigurationManager>();
    private readonly NetworkConfiguration _network = new() { BaseUrl = "/jf" };

    public JellyfinServerInfoTests()
    {
        _config.GetConfiguration("network").Returns(_network);
        _host.GetSmartApiUrl(Arg.Any<IPAddress>()).Returns("http://192.168.1.5:8096/jf");
        _host.SystemId.Returns("sys1");
        _host.FriendlyName.Returns("Home");
        _host.ApplicationVersionString.Returns("12.2.0");
        _host.ApplicationVersion.Returns(new Version(12, 2, 0, 0));
    }

    [Fact]
    public void Suggest_PrefersExternal_ThenAll_ThenSmartUrl()
    {
        var info = new JellyfinServerInfo(_host, _config);
        _network.PublishedServerUriBySubnet = ["192.168.1.0/24=http://lan", "all=https://all.example.com", "external=https://jf.example.com"];
        Assert.Equal("https://jf.example.com", info.SuggestPublicUrl());

        _network.PublishedServerUriBySubnet = ["all=https://all.example.com"];
        Assert.Equal("https://all.example.com", info.SuggestPublicUrl());

        _network.PublishedServerUriBySubnet = [];
        Assert.Equal("http://192.168.1.5:8096/jf", info.SuggestPublicUrl());
        _host.Received().GetSmartApiUrl(IPAddress.Parse("8.8.8.8"));
    }
}
