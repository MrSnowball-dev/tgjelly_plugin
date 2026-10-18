using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Bot;

public sealed class ProfileCallsTests : IDisposable
{
    private readonly BotTestHarness _h = new();
    private readonly ProfileCalls _profile;

    public ProfileCallsTests()
    {
        var host = Substitute.For<IServerApplicationHost>();
        host.FriendlyName.Returns("Home Server");
        _profile = new ProfileCalls(_h.State, _h.Strings, host, NullLogger<ProfileCalls>.Instance);
    }

    [Fact]
    public async Task AllPending_RunInOrder_AndAreCleared()
    {
        Pending(true, ProfileCalls.AllCalls);

        await _profile.RunPendingAsync(new TelegramSender(_h.Api.CreateClient()), TestContext.Current.CancellationToken);

        Assert.Equal(
            ["setMyCommands", "setMyCommands", "setMyDescription", "setMyShortDescription", "setMyDefaultAdministratorRights"],
            _h.Api.Calls.Select(c => c.Method).ToArray());
        Assert.Empty(_h.State.Read(s => s.Telegram.Profile.PendingCalls));
        Assert.Equal(["menu", "status", "test", "help"], _h.Api.CallsTo("setMyCommands").First().Body["commands"]!.AsArray().Select(c => (string)c!["command"]!).ToArray());
    }

    [Fact]
    public async Task AFailingCall_StaysPending_AndIsRecorded()
    {
        Pending(true, ProfileCalls.AllCalls);
        _h.Api.On("setMyDescription", _ => FakeBotApi.Error(400, "Bad Request: description is too long"));

        await _profile.RunPendingAsync(new TelegramSender(_h.Api.CreateClient()), TestContext.Current.CancellationToken);

        Assert.Equal(["descriptions"], _h.State.Read(s => s.Telegram.Profile.PendingCalls.ToArray()));
        Assert.Contains(_h.State.LastErrorsSnapshot(), e => e.Source == "profile");
    }

    [Fact]
    public async Task SetProfileOff_RunsNothing()
    {
        Pending(false, ProfileCalls.AllCalls);

        await _profile.RunPendingAsync(new TelegramSender(_h.Api.CreateClient()), TestContext.Current.CancellationToken);

        Assert.Empty(_h.Api.Calls);
    }

    [Fact]
    public async Task Descriptions_UseTheServerName()
    {
        Pending(true, ["descriptions"]);

        await _profile.RunPendingAsync(new TelegramSender(_h.Api.CreateClient()), TestContext.Current.CancellationToken);

        Assert.Equal("Jellyfin notifications from Home Server", (string)Assert.Single(_h.Api.CallsTo("setMyDescription")).Body["description"]!);
    }

    public void Dispose() => _h.Dispose();

    private void Pending(bool setProfile, string[] calls)
        => _h.State.Update(s => s.Telegram.Profile = new ProfileState { SetProfile = setProfile, PendingCalls = [.. calls] });
}
