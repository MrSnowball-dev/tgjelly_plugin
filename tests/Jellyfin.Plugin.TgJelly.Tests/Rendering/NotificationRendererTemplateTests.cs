using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Intake;
using Jellyfin.Plugin.TgJelly.Library;
using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Templates;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Rendering;

public sealed class NotificationRendererTemplateTests : IDisposable
{
    private readonly BotTestHarness _h = new();
    private readonly FakeLibrary _library = new();
    private readonly TemplateFlags _flags;
    private readonly ListLogger<TemplateRenderer> _logger = new();
    private readonly NotificationRenderer _renderer;

    public NotificationRendererTemplateTests()
    {
        var formatter = new Formatter(_h.Strings);
        var links = new LinkBuilder(new FakeServerInfo());
        _flags = new TemplateFlags(_h.State, _h.Settings, _h.Time);
        var engine = new TemplateEngine(TimeProvider.System);
        var models = new TemplateModelBuilder(_library, links, _h.Settings, formatter, _h.Time);
        _renderer = new NotificationRenderer(
            new MediaLayouts(formatter, links, _library, _h.Settings),
            new AlertLayouts(formatter, _h.Time),
            new TemplateRenderer(engine, models, _flags, _h.Settings, _h.Strings, _logger),
            engine,
            models,
            _flags,
            _h.Settings,
            _h.Strings);
    }

    [Fact]
    public void EnabledTemplate_ReplacesCaptionAndText_KeepsPosterAndButtons()
    {
        var item = WithPoster();
        Set("media.movie", "<b>New: {{ title }}</b>");
        _h.Settings.Update(null, c => c.PublicUrl = "https://jf.example.com");

        var message = _renderer.Render(Media(item), Dest())!;

        Assert.Equal("<b>New: Arrival</b>", message.Text);
        Assert.Equal("<b>New: Arrival</b>", message.Caption);
        Assert.Equal("media.movie", message.TemplateKey);
        Assert.Equal(item.Primary!.Path, message.PosterPath);
        Assert.NotEmpty(message.Buttons);
        Assert.Null(_flags.Current("media.movie"));
    }

    [Fact]
    public void BlankOverride_UsesDefault()
    {
        Set("security.lockout", "   ");
        var message = _renderer.Render(Lockout(), Dest())!;

        // The built-in template renders it, not the blank override.
        Assert.Null(message.TemplateKey);
        Assert.StartsWith("🔒", message.Text, StringComparison.Ordinal);
        Assert.Null(_flags.Current("security.lockout"));
    }

    [Fact]
    public void Digest_NeverUsesTemplate()
    {
        var item = _library.Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Movie, Name = "Arrival" });
        Set("media.digest", "custom");

        var message = _renderer.Render(new RenderModel { Kind = RenderKind.Digest, EventKey = "media.digest", Groups = [Media(item)] }, Dest())!;

        Assert.NotEqual("custom", message.Text);
        Assert.Null(message.TemplateKey);
    }

    [Fact]
    public void RenderError_FallsBackAndFlags()
    {
        Set("security.lockout", "line one\n{{ if }}");

        var message = _renderer.Render(Lockout(), Dest())!;

        Assert.Null(message.TemplateKey);
        Assert.StartsWith("🔒", message.Text, StringComparison.Ordinal);
        var flag = _flags.Current("security.lockout")!;
        Assert.Equal("error", flag.Kind);
        Assert.Contains("line 2", flag.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingField_RendersEmpty_NoFlag()
    {
        Set("security.lockout", "x{{ nope }}y");

        var message = _renderer.Render(Lockout(), Dest())!;

        Assert.Equal("xy", message.Text);
        Assert.Null(_flags.Current("security.lockout"));
    }

    [Fact]
    public void ForbiddenTag_FallsBackAndFlags()
    {
        Set("security.lockout", "<h1>hi</h1>");

        var message = _renderer.Render(Lockout(), Dest())!;

        Assert.Null(message.TemplateKey);
        Assert.Equal("error", _flags.Current("security.lockout")!.Kind);
    }

    [Fact]
    public void ForbiddenTag_FlagTextComesFromTheStringTable()
    {
        Set("security.lockout", "<h1>hi</h1>");

        _renderer.Render(Lockout(), Dest());

        Assert.Equal(_h.Strings.Get("templates.html.notAllowed", ("tag", "h1")), _flags.Current("security.lockout")!.Error);
    }

    [Theory]
    [InlineData("{{ if false }}Locked: {{ user }}{{ end }}")]
    [InlineData("{{ if false }}x{{ end }}  \n  ")]
    [InlineData("<b> </b>")]
    [InlineData("{{ nope }}")]
    public void EmptyOutput_KeepsTheDefault_AndFlags(string text)
    {
        Set("security.lockout", text);

        var message = _renderer.Render(Lockout(), Dest())!;

        Assert.Null(message.TemplateKey);
        Assert.StartsWith("🔒", message.Text, StringComparison.Ordinal);
        var flag = _flags.Current("security.lockout")!;
        Assert.Equal("error", flag.Kind);
        Assert.Equal(_h.Strings.Get("templates.empty"), flag.Error);
    }

    [Fact]
    public void OverCaption_TruncatesAndFlagsTruncated()
    {
        var item = WithPoster();
        Set("media.movie", new string('a', 2000));

        var message = _renderer.Render(Media(item), Dest())!;

        Assert.Equal("truncated", _flags.Current("media.movie")!.Kind);
        Assert.True(message.Caption.Length <= MessageFitter.CaptionLimit - MessageFitter.DelayedReserve);
        Assert.True(message.Text.Length > 1500);
    }

    [Fact]
    public void OverCaption_NoPoster_UnderTextLimit_NotFlagged()
    {
        var item = _library.Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Movie, Name = "Arrival" });
        Set("media.movie", new string('a', 2000));

        var message = _renderer.Render(Media(item), Dest())!;

        Assert.Null(message.PosterPath);
        Assert.Equal(2000, message.Text.Length);
        Assert.Null(_flags.Current("media.movie"));
    }

    [Fact]
    public void InfiniteLoop_FallsBackWithin3s()
    {
        Set("security.lockout", "{{ while true }}x{{ end }}");
        var clock = Stopwatch.StartNew();

        var message = _renderer.Render(Lockout(), Dest())!;

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3));
        Assert.Null(message.TemplateKey);
        Assert.Equal("error", _flags.Current("security.lockout")!.Kind);
    }

    [Fact]
    public void Coalesced_ReRender_UsesTemplate()
    {
        Set("security.login_failed", "×{{ attempts }}");

        var first = _renderer.Render(Failed("1"), Dest())!;
        var fifth = _renderer.Render(Failed("5"), Dest())!;

        Assert.Equal("×1", first.Text);
        Assert.Equal("×5", fifth.Text);
    }

    [Fact]
    public void SuccessfulRender_KeepsAnExistingFlag()
    {
        Set("security.lockout", "ok");
        _flags.Flag("security.lockout", "error", "Telegram said no", TemplateText.Hash("ok"));
        var at = _flags.Current("security.lockout")!.At;
        _h.Time.Advance(TimeSpan.FromMinutes(5));

        var message = _renderer.Render(Lockout(), Dest())!;

        Assert.Equal("ok", message.Text);
        var flag = _flags.Current("security.lockout")!;
        Assert.Equal("error", flag.Kind);
        Assert.Equal(at, flag.At);
    }

    [Fact]
    public void ThrowingModelBuilder_MediaGetsLastResortText_KeepsPosterAndButtons()
    {
        var item = WithPoster();
        _h.Settings.Update(null, c => c.PublicUrl = "https://jf.example.com");
        Set("media.movie", "custom {{ title }}");

        // The layout reads the item once; the model builder's read throws.
        var calls = 0;
        _library.GetItemOverride = id => ++calls > 1 ? throw new InvalidOperationException("boom") : _library.Items.GetValueOrDefault(id);

        var message = _renderer.Render(Media(item), Dest())!;

        Assert.Equal("<b>Media: Movies</b>", message.Text);
        Assert.Equal(message.Text, message.Caption);
        Assert.Null(message.TemplateKey);
        Assert.Equal(item.Primary!.Path, message.PosterPath);
        Assert.NotEmpty(message.Buttons);
        Assert.Equal(1, _logger.Count(Microsoft.Extensions.Logging.LogLevel.Warning));
    }

    [Fact]
    public void MediaKey_DisabledOverride_IsUsed()
    {
        Set("media.movie", "<b>New: {{ title }}</b>", enabled: false);
        var item = _library.Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Movie, Name = "Arrival" });

        var message = _renderer.Render(Media(item), Dest())!;

        Assert.Equal("<b>New: Arrival</b>", message.Text);
        Assert.Equal("media.movie", message.TemplateKey);
    }

    [Fact]
    public void MediaKey_NoOverride_UsesTheBuiltInTemplate()
    {
        var item = _library.Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Movie, Name = "Arrival", Year = 2016 });

        var message = _renderer.Render(Media(item), Dest())!;

        Assert.Equal("<b>Arrival</b> (2016)", message.Text);
        Assert.Null(message.TemplateKey);
    }

    [Fact]
    public void Silent_StillApplied()
    {
        Set("security.lockout", "hi");

        var message = _renderer.Render(Lockout(), new Destination { Id = "d1", SilentGroups = ["security"] })!;

        Assert.Equal("hi", message.Text);
        Assert.True(message.Silent);
    }

    public void Dispose() => _h.Dispose();

    private MediaItem WithPoster()
    {
        var path = System.IO.Path.Combine(_h.Dir.Path, "poster.jpg");
        System.IO.File.WriteAllBytes(path, [1, 2, 3]);
        return _library.Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Movie, Name = "Arrival", Primary = new ImageRef(path, true, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)) });
    }

    private static Destination Dest() => new() { Id = "d1" };

    private static RenderModel Lockout()
        => AlertLayoutsTests.Alert("security.lockout", new() { [EventFields.User] = "bob", [EventFields.Attempts] = "3" });

    private static RenderModel Failed(string attempts)
        => AlertLayoutsTests.Alert("security.login_failed", new() { [EventFields.User] = "bob", [EventFields.Attempts] = attempts });

    private static RenderModel Media(MediaItem item) => new()
    {
        Kind = RenderKind.Media,
        EventKey = "media.movie",
        ItemIds = [item.Id.ToString("N")],
    };

    private void Set(string key, string text, bool enabled = true)
        => _h.Settings.Update(null, c => c.Templates = [new TemplateOverride { EventKey = key, Enabled = enabled, Text = text }]);
}
