using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Api;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Templates;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Api;

public sealed class TemplatesControllerTests : IAsyncLifetime
{
    private readonly BotTestHarness _h = new();
    private TemplateFlags _flags = null!;
    private TemplatesController _controller = null!;

    public async ValueTask InitializeAsync()
    {
        _flags = new TemplateFlags(_h.State, _h.Settings, _h.Time);
        _controller = new TemplatesController(_h.Settings, _h.State, _flags, new TemplateEngine(_h.Time), _h.Strings, await _h.StartOwnedGateAsync());
    }

    [Fact]
    public void Get_ListsEveryKey_WithFieldsAndSamples()
    {
        var dto = _controller.Get().Value!;

        Assert.Equal(EventCatalog.AllKeys, dto.Items.Select(i => i.EventKey));
        var episodes = dto.Items.Single(i => i.EventKey == "media.episodes");
        Assert.True(episodes.Media);
        Assert.Equal(_h.Strings.Get("dest.event.media.episodes"), episodes.Label);
        Assert.False(episodes.Enabled);
        Assert.Equal(string.Empty, episodes.Text);
        Assert.Contains(episodes.Fields, f => f.Name == "episodes" && f.Type == "episode[]");
        Assert.True(episodes.SampleFull.Count > 0);
        Assert.Null(episodes.Flag);
        Assert.False(dto.Items.Single(i => i.EventKey == "server.started").Media);
    }

    [Fact]
    public void Put_SavesAndBumpsRevision()
    {
        var dto = Assert.IsType<TemplatesDto>(_controller.Put("media.movie", Req(0, true, "Hi {{ title }}")).Value);

        Assert.Equal(1, dto.Revision);
        var item = dto.Items.Single(i => i.EventKey == "media.movie");
        Assert.Equal((true, "Hi {{ title }}"), (item.Enabled, item.Text));
        Assert.Equal("Hi {{ title }}", _h.Settings.Current.Templates.Single().Text);
    }

    [Fact]
    public void Put_SameValues_NoRevisionBump()
    {
        _controller.Put("media.movie", Req(0, true, "x"));

        var dto = Assert.IsType<TemplatesDto>(_controller.Put("media.movie", Req(1, true, "x")).Value);

        Assert.Equal(1, dto.Revision);
    }

    [Fact]
    public void Put_ClearsFlag()
    {
        _controller.Put("media.movie", Req(0, true, "x"));
        _flags.Flag("media.movie", "error", "boom", TemplateText.Hash("x"));
        Assert.NotNull(_flags.Current("media.movie"));

        _controller.Put("media.movie", Req(1, true, "y"));

        Assert.False(_h.State.Read(s => s.Status.TemplateFlags.ContainsKey("media.movie")));
    }

    [Fact]
    public void Put_DisableEmpty_RemovesEntry()
    {
        _controller.Put("media.movie", Req(0, true, "x"));

        var dto = Assert.IsType<TemplatesDto>(_controller.Put("media.movie", Req(1, false, string.Empty)).Value);

        Assert.Empty(_h.Settings.Current.Templates);
        Assert.False(dto.Items.Single(i => i.EventKey == "media.movie").Enabled);
    }

    [Fact]
    public void Put_UnknownKey_404()
        => Assert.IsType<NotFoundResult>(_controller.Put("nope", Req(0, true, "x")).Result);

    [Fact]
    public void Put_TooLong_400()
    {
        var result = Assert.IsType<BadRequestObjectResult>(_controller.Put("media.movie", Req(0, true, new string('a', 16385))).Result);

        Assert.Equal("Text", Assert.IsType<ErrorsDto>(result.Value).Errors.Single().Field);
        Assert.Empty(_h.Settings.Current.Templates);
    }

    [Fact]
    public void Put_Stale_409_WithCurrent()
    {
        _controller.Put("media.movie", Req(0, true, "x"));

        var result = Assert.IsType<ConflictObjectResult>(_controller.Put("media.movie", Req(0, true, "z")).Result);

        var current = Assert.IsType<ConflictDto<TemplatesDto>>(result.Value).Current;
        Assert.Equal(1, current.Revision);
    }

    [Fact]
    public void Validate_ParseErrorsOnly()
    {
        var dto = Validate("media.movie", "{{ if }}");

        Assert.NotEmpty(dto.Errors);
        Assert.Equal(string.Empty, dto.Preview);
    }

    [Fact]
    public void Validate_UnknownField_WithPosition()
    {
        var dto = Validate("media.movie", "a\n  {{ nope }}");

        var error = Assert.Single(dto.Errors);
        Assert.Equal((2, 6), (error.Line, error.Column));
    }

    [Fact]
    public void Validate_SparseModel_CatchesNullUse()
    {
        Assert.Empty(Validate("media.movie", "{{ overview | string.size }}").Errors);

        var engine = new TemplateEngine(_h.Time);
        Assert.True(engine.Render("media.episodes", "{{ episodes[0].title }}", TemplateFieldCatalog.SampleFull("media.episodes"), strict: true).Ok);
        Assert.False(engine.Render("media.episodes", "{{ episodes[0].title }}", TemplateFieldCatalog.SampleSparse("media.episodes"), strict: true).Ok);

        var dto = Validate("media.episodes", "{{ episodes[0].title }}");

        Assert.NotEmpty(dto.Errors);
        Assert.Equal(dto.Errors.Count, dto.Errors.Select(e => (e.Message, e.Line, e.Column)).Distinct().Count());
    }

    [Fact]
    public void Validate_ForbiddenTag()
        => Assert.NotEmpty(Validate("media.movie", "<script>x</script>").Errors);

    [Fact]
    public void Validate_MediaOver1024_Warns()
    {
        var dto = Validate("media.movie", new string('a', 1500));

        Assert.Empty(dto.Errors);
        Assert.Equal([_h.Strings.Get("templates.willShorten")], dto.Warnings);
        Assert.Equal((1500, 1024), (dto.Length, dto.Limit));
    }

    [Fact]
    public void Validate_TextOver4096_Errors()
    {
        Assert.NotEmpty(Validate("media.movie", new string('a', 4100)).Errors);
        var dto = Validate("server.started", new string('a', 4100));
        Assert.NotEmpty(dto.Errors);
        Assert.Empty(dto.Warnings);
        Assert.Equal(4096, dto.Limit);
    }

    [Fact]
    public void Validate_ReturnsPreview()
    {
        var dto = Validate("media.movie", "<b>{{ title }}</b>");

        Assert.Empty(dto.Errors);
        Assert.StartsWith("<b>", dto.Preview, System.StringComparison.Ordinal);
        Assert.Equal(dto.Preview.Length - 7, dto.Length);
    }

    private static TemplateUpdateRequest Req(long revision, bool enabled, string text)
        => new() { ExpectedRevision = revision, Enabled = enabled, Text = text };

    private TemplateValidationDto Validate(string key, string text)
        => _controller.Validate(new TemplateValidateRequest { EventKey = key, Text = text }).Value!;

    public ValueTask DisposeAsync()
    {
        _h.Dispose();
        return ValueTask.CompletedTask;
    }
}
