using System;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Intake;
using Jellyfin.Plugin.TgJelly.Library;
using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Rendering;

public sealed class NotificationRendererTests : IDisposable
{
    private readonly BotTestHarness _h = new();
    private readonly FakeLibrary _library = new();
    private readonly NotificationRenderer _renderer;
    private readonly MediaItem _movie;

    public NotificationRendererTests()
    {
        var formatter = new Formatter(_h.Strings);
        _renderer = new NotificationRenderer(
            new MediaLayouts(formatter, new LinkBuilder(new FakeServerInfo()), _library, _h.Settings),
            new AlertLayouts(formatter, new FakeTimeProvider(_h.Time.GetUtcNow())));
        _movie = _library.Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Movie, Name = "Arrival" });
    }

    [Fact]
    public void SilentGroups_MakeTheirEventsSilent()
    {
        var security = Dest("security");

        Assert.True(_renderer.Render(Lockout(), security)!.Silent);
        Assert.False(_renderer.Render(AlertLayoutsTests.Alert("server.started", new()), security)!.Silent);
        Assert.False(_renderer.Render(Media(), security)!.Silent);
        Assert.False(_renderer.Render(Lockout(), Dest())!.Silent);
    }

    [Fact]
    public void Digest_FollowsTheMediaGroup()
    {
        Assert.True(_renderer.Render(Digest(), Dest("media"))!.Silent);
        Assert.False(_renderer.Render(Digest(), Dest("security", "server"))!.Silent);
    }

    [Fact]
    public void DispatchesOnKind()
    {
        Assert.StartsWith("🔒 <b>bob</b>", _renderer.Render(Lockout(), Dest())!.Text, StringComparison.Ordinal);
        Assert.Equal("<b>Arrival</b>", _renderer.Render(Media(), Dest())!.Text);
        Assert.Equal("🆕 <b>1 new item in Jellyfin</b>\n\n<b>Other</b>: Arrival", _renderer.Render(Digest(), Dest())!.Text);

        _library.Items.Clear();
        Assert.Null(_renderer.Render(Media(), Dest()));
    }

    public void Dispose() => _h.Dispose();

    private static Destination Dest(params string[] silent) => new() { Id = "d1", SilentGroups = silent };

    private static RenderModel Lockout()
        => AlertLayoutsTests.Alert("security.lockout", new() { [EventFields.User] = "bob", [EventFields.Attempts] = "3" });

    private RenderModel Media() => new()
    {
        Kind = RenderKind.Media,
        EventKey = "media.movie",
        ItemIds = [_movie.Id.ToString("N")],
    };

    private RenderModel Digest() => new() { Kind = RenderKind.Digest, EventKey = "media.digest", Groups = [Media()] };
}
