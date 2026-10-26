using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.TgJelly.Intake;
using Jellyfin.Plugin.TgJelly.Intake.Consumers;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using MediaBrowser.Common.Updates;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Events.Authentication;
using MediaBrowser.Controller.Events.Updates;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Updates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Intake;

public class ConsumerTests
{
    private readonly IntakeQueues _queues = new(new FakeTimeProvider());

    [Fact]
    public async Task LoginFailed_MapsEveryField()
    {
        var args = new AuthenticationRequestEventArgs(new AuthenticationRequest { Username = "Alice", App = "Infuse", DeviceName = "iPhone", DeviceId = "d1", RemoteEndPoint = "203.0.113.5" });

        await new LoginFailedConsumer(_queues, new ListLogger<LoginFailedConsumer>()).OnEvent(args);

        var e = Single("security.login_failed");
        Assert.Equal(
            new Dictionary<string, string> { ["user"] = "Alice", ["user_key"] = "ALICE", ["ip"] = "203.0.113.5", ["client"] = "Infuse", ["device"] = "iPhone", ["device_id"] = "d1" },
            e.Values);
    }

    [Fact]
    public async Task LoginSucceeded_MapsUserAndSession()
    {
        var user = Guid.NewGuid();
        var args = new AuthenticationResultEventArgs(new AuthenticationResult
        {
            User = new UserDto { Name = "alice", Id = user },
            SessionInfo = new SessionInfoDto { Client = "Jellyfin Web", DeviceName = "Firefox", DeviceId = "d2", RemoteEndPoint = "10.0.0.2" },
        });

        await new LoginSucceededConsumer(_queues, new ListLogger<LoginSucceededConsumer>()).OnEvent(args);

        var e = Single("security.login_succeeded");
        Assert.Equal(("alice", user.ToString("N"), "10.0.0.2", "Jellyfin Web", "Firefox", "d2"), (e.Values["user"], e.Values["user_id"], e.Values["ip"], e.Values["client"], e.Values["device"], e.Values["device_id"]));
    }

    [Fact]
    public async Task Lockout_MapsAttempts()
    {
        var user = new User("Alice", "p", "r") { InvalidLoginAttemptCount = 5 };

        await new LockoutConsumer(_queues, new ListLogger<LockoutConsumer>()).OnEvent(new UserLockedOutEventArgs(user));

        var e = Single("security.lockout");
        Assert.Equal(("Alice", "ALICE", user.Id.ToString("N"), "5"), (e.Values["user"], e.Values["user_key"], e.Values["user_id"], e.Values["attempts"]));
    }

    [Fact]
    public async Task UserEvents_MapUserAndId()
    {
        var user = new User("bob", "p", "r");
        await new UserCreatedConsumer(_queues, new ListLogger<UserCreatedConsumer>()).OnEvent(new UserCreatedEventArgs(user));
        await new UserDeletedConsumer(_queues, new ListLogger<UserDeletedConsumer>()).OnEvent(new UserDeletedEventArgs(user));
        await new PasswordChangedConsumer(_queues, new ListLogger<PasswordChangedConsumer>()).OnEvent(new UserPasswordChangedEventArgs(user));

        var events = _queues.TakeOther(10);
        Assert.Equal(["security.user_created", "security.user_deleted", "security.password_changed"], events.Select(e => e.EventKey));
        Assert.All(events, e => Assert.Equal(("bob", user.Id.ToString("N")), (e.Values["user"], e.Values["user_id"])));
    }

    [Fact]
    public async Task PluginEvents_MapNameVersionChangelogAndError()
    {
        var info = new InstallationInfo { Name = "Webhook", Version = new Version(18, 0, 0, 0), Changelog = "Fixes" };
        await new PluginInstalledConsumer(_queues, new ListLogger<PluginInstalledConsumer>()).OnEvent(new PluginInstalledEventArgs(info));
        await new PluginUpdatedConsumer(_queues, new ListLogger<PluginUpdatedConsumer>()).OnEvent(new PluginUpdatedEventArgs(info));
        await new PluginUninstalledConsumer(_queues, new ListLogger<PluginUninstalledConsumer>()).OnEvent(new PluginUninstalledEventArgs(new PluginInfo("Webhook", new Version(18, 0), "d", Guid.NewGuid(), true)));
        await new PluginInstallFailedConsumer(_queues, new ListLogger<PluginInstallFailedConsumer>()).OnEvent(new InstallationFailedEventArgs { InstallationInfo = info, Exception = new InvalidOperationException("checksum mismatch") });

        var events = _queues.TakeOther(10);
        Assert.Equal(["server.plugin_installed", "server.plugin_updated", "server.plugin_uninstalled", "server.plugin_install_failed"], events.Select(e => e.EventKey));
        Assert.Equal(("Webhook", "18.0.0.0"), (events[0].Values["plugin"], events[0].Values["version"]));
        Assert.False(events[0].Values.ContainsKey("changelog"));
        Assert.Equal("Fixes", events[1].Values["changelog"]);
        Assert.Equal(("Webhook", "18.0"), (events[2].Values["plugin"], events[2].Values["version"]));
        Assert.Equal("checksum mismatch", events[3].Values["error"]);
    }

    [Fact]
    public async Task PartialPayloads_PublishWithEmptyFields_AndNeverThrow()
    {
        await new LoginSucceededConsumer(_queues, new ListLogger<LoginSucceededConsumer>()).OnEvent(new AuthenticationResultEventArgs(new AuthenticationResult()));
        await new PluginInstallFailedConsumer(_queues, new ListLogger<PluginInstallFailedConsumer>()).OnEvent(new InstallationFailedEventArgs());
        await new PluginUpdatedConsumer(_queues, new ListLogger<PluginUpdatedConsumer>()).OnEvent(new PluginUpdatedEventArgs(new InstallationInfo { Name = "X" }));

        var events = _queues.TakeOther(10);
        Assert.Equal(3, events.Count);
        Assert.All(events[0].Values.Values, v => Assert.Equal(string.Empty, v));
        Assert.Equal((string.Empty, string.Empty, string.Empty), (events[1].Values["plugin"], events[1].Values["version"], events[1].Values["error"]));
        Assert.Equal(("X", string.Empty), (events[2].Values["plugin"], events[2].Values["version"]));
    }

    [Fact]
    public async Task QueuesThrow_IsLogged_NotRethrown()
    {
        var queues = new IntakeQueues(new ThrowingTime());
        var logger = new ListLogger<UserCreatedConsumer>();

        var task = new UserCreatedConsumer(queues, logger).OnEvent(new UserCreatedEventArgs(new User("bob", "p", "r")));

        Assert.True(task.IsCompletedSuccessfully);
        await task;
        Assert.Equal(1, logger.Count(LogLevel.Error));
    }

    private NotificationEvent Single(string key)
    {
        var e = Assert.Single(_queues.TakeOther(10));
        Assert.Equal(key, e.EventKey);
        return e;
    }

    private sealed class ThrowingTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException("clock");
    }
}
