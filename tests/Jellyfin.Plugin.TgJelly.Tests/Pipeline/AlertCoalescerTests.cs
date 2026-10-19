using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Intake;
using Jellyfin.Plugin.TgJelly.Pipeline;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Pipeline;

public sealed class AlertCoalescerTests : IDisposable
{
    private readonly BotTestHarness _h = new();
    private readonly AlertCoalescer _coalescer;

    public AlertCoalescerTests() => _coalescer = new AlertCoalescer(_h.State, _h.Strings, _h.Time);

    private DateTimeOffset Now => _h.Time.GetUtcNow();

    [Fact]
    public void LoginFailedKey_IsPrefixed() => Assert.Equal("login_failed:ALICE", AlertCoalescer.LoginFailedKey("ALICE"));

    [Fact]
    public void LockoutBeforeLoginFailed_IsHeld_ThenRepliesAndMarksLocked()
    {
        Assert.Empty(_coalescer.Process(Lockout("alice", 3)));
        Advance(0.1);
        var first = Assert.Single(_coalescer.Process(Failed("alice", "203.0.113.5")));
        Assert.Equal((OutboxOp.Send, "login_failed:ALICE"), (first.Op, first.SentKey));

        Advance(4.8);
        Assert.Empty(_coalescer.Tick());
        Advance(0.1);
        var outputs = _coalescer.Tick();

        AssertLockedReply(outputs, attempts: "1");
        Assert.Empty(_coalescer.Tick());
    }

    [Fact]
    public void LockoutAfterLoginFailed_SameResult()
    {
        _coalescer.Process(Failed("alice", "203.0.113.5"));
        Advance(1);
        Assert.Empty(_coalescer.Process(Lockout("alice", 3)));
        Advance(5);

        AssertLockedReply(_coalescer.Tick(), attempts: "1");
    }

    [Fact]
    public void LockoutWithoutWindow_IsItsOwnMessage_NoReply()
    {
        _coalescer.Process(Lockout("bob", 5));
        Advance(5);

        var output = Assert.Single(_coalescer.Tick());

        Assert.Equal((OutboxOp.Send, "security.lockout", (string?)null), (output.Op, output.Model.EventKey, output.ReplyToKey));
        Assert.Equal("5", output.Model.Values[EventFields.Attempts]);
        Assert.Equal("bob", output.Model.Values[EventFields.User]);
    }

    [Fact]
    public void CredentialStuffing_50Ips_OneSendThenEdits()
    {
        var outputs = new List<AlertOutput>();
        for (var i = 0; i < 50; i++)
        {
            outputs.AddRange(_coalescer.Process(Failed("alice", $"198.51.100.{i}")));
            Advance(10);
        }

        Assert.Equal(1, outputs.Count(o => o.Op == OutboxOp.Send));
        Assert.Equal(49, outputs.Count(o => o.Op == OutboxOp.Edit));
        Assert.All(outputs.Skip(1), o => Assert.Equal(("login_failed:ALICE", "login_failed:ALICE"), (o.TargetKey, o.SentKey)));
        var last = outputs[^1].Model;
        Assert.Equal(("50", "50", "198.51.100.49", "false"), (last.Values[EventFields.Attempts], last.Values[EventFields.IpCount], last.Values[EventFields.Ip], last.Values[EventFields.Locked]));
        Assert.Equal((RenderKind.Alert, "security.login_failed"), (last.Kind, last.EventKey));
        Assert.Equal(Now - TimeSpan.FromSeconds(10), last.At);
    }

    [Fact]
    public void SameIp_IsCountedOnce()
    {
        _coalescer.Process(Failed("alice", "203.0.113.5"));
        var output = Assert.Single(_coalescer.Process(Failed("alice", "203.0.113.5")));

        Assert.Equal(("2", "1"), (output.Model.Values[EventFields.Attempts], output.Model.Values[EventFields.IpCount]));
    }

    [Fact]
    public void Window_Slides()
    {
        Assert.Equal(OutboxOp.Send, Assert.Single(_coalescer.Process(Failed("alice", "1.1.1.1"))).Op);
        Advance(9 * 60);
        Assert.Equal(OutboxOp.Edit, Assert.Single(_coalescer.Process(Failed("alice", "1.1.1.1"))).Op);
        Advance(9 * 60);
        Assert.Equal(OutboxOp.Edit, Assert.Single(_coalescer.Process(Failed("alice", "1.1.1.1"))).Op);
        Advance(11 * 60);

        var fresh = Assert.Single(_coalescer.Process(Failed("alice", "1.1.1.1")));

        Assert.Equal((OutboxOp.Send, "1"), (fresh.Op, fresh.Model.Values[EventFields.Attempts]));
    }

    [Fact]
    public void UsernameCase_IsOneWindow()
    {
        _coalescer.Process(Failed("Alice", "1.1.1.1"));

        var output = Assert.Single(_coalescer.Process(Failed("ALICE", "1.1.1.2")));

        Assert.Equal((OutboxOp.Edit, "2"), (output.Op, output.Model.Values[EventFields.Attempts]));
    }

    [Fact]
    public void UserCreatedThenPasswordChanged_Within10s_OnlyCreation()
    {
        Assert.Equal("security.user_created", Assert.Single(_coalescer.Process(User("security.user_created", "u1"))).Model.EventKey);
        Advance(10);

        Assert.Empty(_coalescer.Process(User("security.password_changed", "u1")));
        Assert.Single(_coalescer.Process(User("security.password_changed", "u2")));
    }

    [Fact]
    public void PasswordChanged_After10s_IsSent()
    {
        _coalescer.Process(User("security.user_created", "u1"));
        Advance(10.5);

        var output = Assert.Single(_coalescer.Process(User("security.password_changed", "u1")));

        Assert.Equal((OutboxOp.Send, "security.password_changed"), (output.Op, output.Model.EventKey));
    }

    [Fact]
    public void LoginSucceeded_DedupedPerUserAndDevice_For12h()
    {
        Assert.Single(_coalescer.Process(Succeeded("u1", "phone")));
        Advance(3600);
        Assert.Empty(_coalescer.Process(Succeeded("u1", "phone")));
        Assert.Single(_coalescer.Process(Succeeded("u1", "tv")));
        Assert.Single(_coalescer.Process(Succeeded("u2", "phone")));

        Advance(11 * 3600);

        Assert.Single(_coalescer.Process(Succeeded("u1", "phone")));
    }

    [Fact]
    public void RestartPending_ListsPluginEventsFromTheLast10Minutes()
    {
        _coalescer.Process(Plugin("server.plugin_installed", "Old", "1.0"));
        Advance(60);
        var installed = Assert.Single(_coalescer.Process(Plugin("server.plugin_installed", "Trakt", "26.0")));
        Assert.Equal((OutboxOp.Send, "Trakt"), (installed.Op, installed.Model.Values[EventFields.Plugin]));
        _coalescer.Process(Plugin("server.plugin_updated", "Fanart", "12.1"));
        _coalescer.Process(Plugin("server.plugin_uninstalled", "Bookshelf", "3.0"));
        Advance(9.5 * 60);
        _coalescer.Process(Plugin("server.plugin_install_failed", "Broken", "0.1"));

        var restart = Assert.Single(_coalescer.Process(Event("server.restart_pending")));

        Assert.Equal(
            ["Installed Trakt 26.0", "Updated Fanart to 12.1", "Removed Bookshelf 3.0", "Failed to install Broken 0.1"],
            restart.Model.Lines);
    }

    [Fact]
    public void OtherKeys_AreSent()
    {
        var output = Assert.Single(_coalescer.Process(Event("server.started", (EventFields.Version, "12.2.0"))));

        Assert.Equal((OutboxOp.Send, RenderKind.Alert, "12.2.0", (string?)null), (output.Op, output.Model.Kind, output.Model.Values[EventFields.Version], output.SentKey));
    }

    [Fact]
    public void Drain_ReleasesHeldLockoutsAtOnce()
    {
        _coalescer.Process(Failed("alice", "1.1.1.1"));
        _coalescer.Process(Lockout("alice", 3));
        _coalescer.Process(Lockout("bob", 5));

        var outputs = _coalescer.Drain();

        Assert.Equal(3, outputs.Count);
        Assert.Equal(2, outputs.Count(o => o.Model.EventKey == "security.lockout"));
        Assert.Empty(_coalescer.Drain());
        Advance(10);
        Assert.Empty(_coalescer.Tick());
    }

    [Fact]
    public void Restart_KeepsWindows()
    {
        _coalescer.Process(Failed("alice", "1.1.1.1"));
        _h.State.FlushAll();

        var state = new StateStore(_h.Paths, _h.Time, NullLogger<StateStore>.Instance);
        state.Load();
        var restarted = new AlertCoalescer(state, _h.Strings, _h.Time);
        Advance(60);

        var output = Assert.Single(restarted.Process(Failed("alice", "1.1.1.2")));
        Assert.Equal((OutboxOp.Edit, "2"), (output.Op, output.Model.Values[EventFields.Attempts]));
    }

    [Fact]
    public void Tick_RemovesEndedWindowsAndExpiredEntries()
    {
        _coalescer.Process(Failed("alice", "1.1.1.1"));
        _coalescer.Process(Succeeded("u1", "phone"));
        _coalescer.Process(User("security.user_created", "u2"));
        _coalescer.Process(Plugin("server.plugin_installed", "Trakt", "26.0"));
        Advance(12 * 3600);

        _coalescer.Tick();

        Assert.Equal((0, 0, 0, 0), _h.State.ReadQueue(q => (q.SecurityWindows.Count, q.Dedupe.Logins.Count, q.Dedupe.UserCreated.Count, q.RecentPluginEvents.Count)));
        Assert.Equal(0, _h.State.Read(s => s.Status.Counters.Evictions));
    }

    [Fact]
    public void WindowsCap_EvictsOldest()
    {
        for (var i = 0; i <= AlertCoalescer.WindowsCap; i++)
        {
            _coalescer.Process(Failed($"user{i}", "1.1.1.1"));
            Advance(0.5);
        }

        _coalescer.Tick();

        Assert.Equal(AlertCoalescer.WindowsCap, _h.State.ReadQueue(q => q.SecurityWindows.Count));
        Assert.False(_h.State.ReadQueue(q => q.SecurityWindows.ContainsKey("USER0")));
        Assert.Equal(1, _h.State.Read(s => s.Status.Counters.Evictions));
    }

    public void Dispose() => _h.Dispose();

    private static void AssertLockedReply(IReadOnlyList<AlertOutput> outputs, string attempts)
    {
        Assert.Equal(2, outputs.Count);
        var lockout = outputs[0];
        Assert.Equal((OutboxOp.Send, "security.lockout", "login_failed:ALICE"), (lockout.Op, lockout.Model.EventKey, lockout.ReplyToKey));
        Assert.Equal(attempts, lockout.Model.Values[EventFields.Attempts]);
        var edit = outputs[1];
        Assert.Equal((OutboxOp.Edit, "login_failed:ALICE", "login_failed:ALICE"), (edit.Op, edit.TargetKey, edit.SentKey));
        Assert.Equal("true", edit.Model.Values[EventFields.Locked]);
    }

    private NotificationEvent Event(string key, params (string Name, string Value)[] values)
        => new(key, Now, values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal));

    private NotificationEvent Failed(string user, string ip) => Event(
        "security.login_failed",
        (EventFields.User, user), (EventFields.UserKey, user.ToUpperInvariant()), (EventFields.Ip, ip),
        (EventFields.Client, "Infuse"), (EventFields.Device, "iPhone"), (EventFields.DeviceId, "dev1"));

    private NotificationEvent Lockout(string user, int attempts) => Event(
        "security.lockout",
        (EventFields.User, user), (EventFields.UserKey, user.ToUpperInvariant()), (EventFields.UserId, user + "-id"),
        (EventFields.Attempts, attempts.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private NotificationEvent User(string key, string userId) => Event(key, (EventFields.User, "carol"), (EventFields.UserId, userId));

    private NotificationEvent Succeeded(string userId, string deviceId) => Event(
        "security.login_succeeded", (EventFields.User, "carol"), (EventFields.UserId, userId), (EventFields.DeviceId, deviceId));

    private NotificationEvent Plugin(string key, string plugin, string version)
        => Event(key, (EventFields.Plugin, plugin), (EventFields.Version, version));

    private void Advance(double seconds) => _h.Time.Advance(TimeSpan.FromSeconds(seconds));
}
