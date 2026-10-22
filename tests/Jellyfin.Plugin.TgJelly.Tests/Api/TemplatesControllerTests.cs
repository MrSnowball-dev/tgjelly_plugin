using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Api;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Templates;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Api;

public sealed class TemplatesControllerTests : IAsyncLifetime
{
    private readonly BotTestHarness _h = new();
    private TemplateFlags _flags = null!;
    private TemplatesController _controller = null!;
    private HealthService _health = null!;

    public async ValueTask InitializeAsync()
    {
        _flags = new TemplateFlags(_h.State, _h.Settings, _h.Time);
        _h.Connect();
        var gate = await _h.StartOwnedGateAsync();
        _health = new HealthService(gate, _h.Connection, new DestinationChecker(_h.Connection, _h.Strings), _h.Settings, _h.State, _h.Time, NullLogger<HealthService>.Instance);
        _h.Track(_health);
        var engine = new TemplateEngine(_h.Time);
        var models = new TemplateModelBuilder(new FakeLibrary(), new LinkBuilder(new FakeServerInfo()), _h.Settings, new Formatter(_h.Strings), _h.Time);
        var renderer = new TemplateRenderer(engine, models, _flags, _h.Settings, _h.Strings, NullLogger<TemplateRenderer>.Instance);
        var tester = new DestinationTester(_h.Connection, _health, _h.Strings, NullLogger<DestinationTester>.Instance);
        _controller = new TemplatesController(_h.Settings, _h.State, _flags, engine, _h.Strings, gate, new TemplateSample(renderer, engine, models, _h.Settings, _h.Strings), tester, _health);
    }

    [Fact]
    public void Get_ListsEveryKey_WithFieldsAndSamples()
    {
        var dto = _controller.Get().Value!;

        Assert.Equal(TemplateKeys.All, dto.Items.Select(i => i.EventKey));
        var episodes = dto.Items.Single(i => i.EventKey == "media.episodes");
        Assert.True(episodes.Media);
        Assert.Equal(_h.Strings.Get("dest.event.media.episodes"), episodes.Label);
        Assert.False(episodes.Edited);
        Assert.Contains(episodes.Fields, f => f.Name == "episodes" && f.Type == "episode[]");
        Assert.True(episodes.SampleFull.Count > 0);
        Assert.Null(episodes.Flag);
        Assert.False(dto.Items.Single(i => i.EventKey == "server.started").Media);
    }

    [Fact]
    public void Put_SavesAndBumpsRevision()
    {
        var dto = Assert.IsType<TemplatesDto>(_controller.Put("media.movie", Req(0, "Hi {{ title }}")).Value);

        Assert.Equal(1, dto.Revision);
        var item = dto.Items.Single(i => i.EventKey == "media.movie");
        Assert.Equal((true, "Hi {{ title }}"), (item.Edited, item.Text));
        Assert.Equal("Hi {{ title }}", _h.Settings.Current.Templates.Single().Text);
    }

    [Fact]
    public void Put_SameValues_NoRevisionBump()
    {
        _controller.Put("media.movie", Req(0, "x"));

        var dto = Assert.IsType<TemplatesDto>(_controller.Put("media.movie", Req(1, "x")).Value);

        Assert.Equal(1, dto.Revision);
    }

    [Fact]
    public void Put_ClearsFlag()
    {
        _controller.Put("media.movie", Req(0, "x"));
        _flags.Flag("media.movie", "error", "boom", TemplateText.Hash("x"));
        Assert.NotNull(_flags.Current("media.movie"));

        _controller.Put("media.movie", Req(1, "y"));

        Assert.False(_h.State.Read(s => s.Status.TemplateFlags.ContainsKey("media.movie")));
    }

    [Fact]
    public void Put_Empty_RemovesEntry()
    {
        _controller.Put("media.movie", Req(0, "x"));

        var dto = Assert.IsType<TemplatesDto>(_controller.Put("media.movie", Req(1, string.Empty)).Value);

        Assert.Empty(_h.Settings.Current.Templates);
        Assert.False(dto.Items.Single(i => i.EventKey == "media.movie").Edited);
    }

    [Fact]
    public void Put_UnknownKey_404()
        => Assert.IsType<NotFoundResult>(_controller.Put("nope", Req(0, "x")).Result);

    [Fact]
    public void Put_TooLong_400()
    {
        var result = Assert.IsType<BadRequestObjectResult>(_controller.Put("media.movie", Req(0, new string('a', 16385))).Result);

        Assert.Equal("Text", Assert.IsType<ErrorsDto>(result.Value).Errors.Single().Field);
        Assert.Empty(_h.Settings.Current.Templates);
    }

    [Fact]
    public void Put_Stale_409_WithCurrent()
    {
        _controller.Put("media.movie", Req(0, "x"));

        var result = Assert.IsType<ConflictObjectResult>(_controller.Put("media.movie", Req(0, "z")).Result);

        var current = Assert.IsType<ConflictDto<TemplatesDto>>(result.Value).Current;
        Assert.Equal(1, current.Revision);
    }

    [Fact]
    public void Get_ItemHasBuiltInAndTextInUse()
    {
        var builtIn = BuiltInTemplates.Get("media.movie", "en")!;

        var before = _controller.Get().Value!.Items.Single(i => i.EventKey == "media.movie");
        Assert.Equal((builtIn, builtIn, false), (before.Text, before.BuiltIn, before.Edited));

        _controller.Put("media.movie", Req(0, "Hi {{ title }}"));
        var after = _controller.Get().Value!.Items.Single(i => i.EventKey == "media.movie");
        Assert.Equal(("Hi {{ title }}", builtIn, true), (after.Text, after.BuiltIn, after.Edited));
    }

    [Fact]
    public void Get_IncludesDigest()
    {
        var digest = _controller.Get().Value!.Items.Single(i => i.EventKey == "media.digest");

        Assert.Equal("Digest", digest.Label);
        Assert.False(digest.Media);
        Assert.Equal(BuiltInTemplates.Get("media.digest", "en"), digest.Text);
    }

    [Fact]
    public void Get_SampleValues_AreFlat()
    {
        var item = _controller.Get().Value!.Items.Single(i => i.EventKey == "media.movie");

        Assert.IsType<string>(item.SampleFull["at_html"]);
    }

    [Fact]
    public void Put_TextEqualToBuiltIn_RemovesOverride()
    {
        _controller.Put("media.movie", Req(0, "x"));

        var dto = Assert.IsType<TemplatesDto>(_controller.Put("media.movie", Req(1, BuiltInTemplates.Get("media.movie", "en")!)).Value);

        Assert.Empty(_h.Settings.Current.Templates);
        Assert.False(dto.Items.Single(i => i.EventKey == "media.movie").Edited);
    }

    [Fact]
    public void Put_CrLfEqualToBuiltIn_RemovesOverride()
    {
        _controller.Put("media.movie", Req(0, "x"));

        _controller.Put("media.movie", Req(1, BuiltInTemplates.Get("media.movie", "en")!.Replace("\n", "\r\n", System.StringComparison.Ordinal)));

        Assert.Empty(_h.Settings.Current.Templates);
    }

    [Fact]
    public void Put_Digest_Saves()
    {
        var dto = Assert.IsType<TemplatesDto>(_controller.Put("media.digest", Req(0, "{{ title }}")).Value);

        Assert.True(dto.Items.Single(i => i.EventKey == "media.digest").Edited);
    }

    [Fact]
    public void Delete_ResetsAndClearsFlag()
    {
        _controller.Put("media.movie", Req(0, "x"));
        _flags.Flag("media.movie", "error", "boom", TemplateText.Hash("x"));

        var dto = Assert.IsType<TemplatesDto>(_controller.Delete("media.movie", 1).Value);

        Assert.Empty(_h.Settings.Current.Templates);
        Assert.False(_h.State.Read(s => s.Status.TemplateFlags.ContainsKey("media.movie")));
        Assert.False(dto.Items.Single(i => i.EventKey == "media.movie").Edited);
        Assert.Equal(2, dto.Revision);
    }

    [Fact]
    public void Delete_NoOverride_ReturnsUnchanged()
    {
        _flags.Flag("media.movie", "error", "stale", TemplateText.Hash("gone"));
        Assert.Contains("media.movie", _flags.Keys());

        var dto = Assert.IsType<TemplatesDto>(_controller.Delete("media.movie", 0).Value);

        Assert.Equal(0, dto.Revision);
        Assert.DoesNotContain("media.movie", _flags.Keys());
        Assert.False(_h.State.Read(s => s.Status.TemplateFlags.ContainsKey("media.movie")));
    }

    [Fact]
    public void Delete_StaleRevision_Conflict()
    {
        _controller.Put("media.movie", Req(0, "x"));

        var result = Assert.IsType<ConflictObjectResult>(_controller.Delete("media.movie", 0).Result);

        Assert.Equal(1, Assert.IsType<ConflictDto<TemplatesDto>>(result.Value).Current.Revision);
        Assert.Single(_h.Settings.Current.Templates);
    }

    [Fact]
    public void Delete_UnknownKey_404()
        => Assert.IsType<NotFoundResult>(_controller.Delete("nope", 0).Result);

    [Fact]
    public void Validate_ReturnsFittedHtmlButtonsAndPosterFlag()
    {
        _h.Settings.Update(null, c => c.PublicUrl = "https://jf.example.com");

        var dto = Validate("media.movie", new string('a', 1500));

        Assert.Empty(dto.Errors);
        Assert.True(dto.Caption);
        Assert.True(dto.Poster);
        Assert.Equal(1024, dto.Limit);
        Assert.Equal(1500, dto.Length);
        Assert.True(HtmlTextLength(dto.Html) <= 1024);
        Assert.NotEmpty(dto.Html);
        Assert.Equal(["Open in Jellyfin"], dto.Buttons[0].Select(b => b.Text));
        Assert.Equal(["IMDb", "TMDb"], dto.Buttons[1].Select(b => b.Text));
    }

    [Fact]
    public void Validate_Length_IsTheToggledRender()
    {
        // The length line describes the preview, which the message toggles shape.
        _h.Settings.Update(null, c =>
        {
            c.Message.ShowOverview = false;
            c.Message.ShowRatingGenres = false;
        });
        const string Text = "<b>{{ title }}</b> {{ overview }} {{ rating_text }} {{ genres | array.join \", \" }}";
        var toggled = new System.Collections.Generic.Dictionary<string, object?>(TemplateFieldCatalog.SampleFull("media.movie"))
        {
            ["overview"] = null,
            ["rating"] = null,
            ["rating_text"] = null,
            ["genres"] = new System.Collections.Generic.List<object?>(),
        };
        var engine = new TemplateEngine(_h.Time);
        var expected = HtmlTextLength(engine.Render("media.movie", Text, toggled, strict: false).Output!);
        var untoggled = HtmlTextLength(engine.Render("media.movie", Text, TemplateFieldCatalog.SampleFull("media.movie"), strict: false).Output!);

        var dto = Validate("media.movie", Text);

        Assert.Empty(dto.Errors);
        Assert.NotEqual(untoggled, expected);
        Assert.Equal(expected, dto.Length);
        Assert.Equal(expected, HtmlTextLength(dto.Html));
    }

    [Fact]
    public void Validate_PosterWarning_UsesTheToggledLength()
    {
        // Untoggled the overview pushes the text over the caption limit; with Show overview off it fits.
        _h.Settings.Update(null, c => c.Message.ShowOverview = false);

        var dto = Validate("media.movie", "{{ title }} {{ overview }}" + new string('a', 990));

        Assert.Empty(dto.Errors);
        Assert.Empty(dto.Warnings);
        Assert.True(dto.Length <= 1024);
    }

    [Fact]
    public void Validate_ShowPosterOff_TextLimit()
    {
        _h.Settings.Update(null, c => c.Message.ShowPoster = false);

        var dto = Validate("media.movie", new string('a', 1500));

        Assert.False(dto.Caption);
        Assert.False(dto.Poster);
        Assert.Equal(4096, dto.Limit);
        Assert.Empty(dto.Warnings);
        Assert.Equal(1500, HtmlTextLength(dto.Html));
    }

    [Fact]
    public async Task Test_InvalidTemplate_NotSent()
    {
        var d = _h.AddDestination(-1001);

        var result = (await _controller.Test(new TemplateTestRequest { EventKey = "media.movie", Text = "{{ nope }}", DestinationId = d.Id }, CancellationToken.None)).Value!;

        Assert.False(result.Sent);
        Assert.NotNull(result.Error);
        Assert.Empty(_h.Api.CallsTo("sendPhoto"));
        Assert.Empty(_h.Api.CallsTo("sendMessage"));
    }

    [Fact]
    public async Task Test_UnknownDestination_404()
        => Assert.IsType<NotFoundResult>((await _controller.Test(new TemplateTestRequest { EventKey = "media.movie", Text = "x", DestinationId = "zzzzzzzz" }, CancellationToken.None)).Result);

    [Fact]
    public async Task Test_UnknownKey_404()
    {
        var d = _h.AddDestination(-1001);

        Assert.IsType<NotFoundResult>((await _controller.Test(new TemplateTestRequest { EventKey = "nope", Text = "x", DestinationId = d.Id }, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task Test_ValidTemplate_SendsThePhoto()
    {
        var d = _h.AddDestination(-1001);

        var result = (await _controller.Test(new TemplateTestRequest { EventKey = "media.movie", Text = "<b>{{ title }}</b>", DestinationId = d.Id }, CancellationToken.None)).Value!;

        Assert.True(result.Sent);
        Assert.Equal("<b>Arrival</b>", (string)Assert.Single(_h.Api.CallsTo("sendPhoto")).Body["caption"]!);
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
        => Assert.Contains(Validate("media.movie", "<script>x</script>").Errors, e => e.Message.Contains("script", System.StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Validate_ForbiddenTag_MessageFromTheStringTable()
        => Assert.Contains(Validate("server.started", "<div>x</div>").Errors, e => e.Message == _h.Strings.Get("templates.html.notAllowed", ("tag", "div")));

    [Fact]
    public void Validate_EmptyFullRender_Warns()
    {
        var dto = Validate("security.lockout", "{{ if false }}Locked: {{ user }}{{ end }}");

        Assert.Empty(dto.Errors);
        Assert.Equal([_h.Strings.Get("templates.emptyWarning")], dto.Warnings);
    }

    [Fact]
    public void Validate_EmptySparseRender_Warns()
    {
        var dto = Validate("media.movie", "<i>{{ overview }}</i> ");

        Assert.Empty(dto.Errors);
        Assert.NotEmpty(dto.Preview);
        Assert.Equal([_h.Strings.Get("templates.emptyWarning")], dto.Warnings);
    }

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
        Assert.Equal(_h.Strings.Get("templates.tooLong", ("length", 4100), ("limit", 4096)), Assert.Single(dto.Errors).Message);
        Assert.Empty(dto.Warnings);
        Assert.Equal(4096, dto.Limit);
    }

    [Fact]
    public void Validate_ExactLimits_AreClean()
    {
        var media = Validate("media.movie", new string('a', 1024));
        var plain = Validate("server.started", new string('a', 4096));

        Assert.Empty(media.Errors);
        Assert.Empty(media.Warnings);
        Assert.Empty(plain.Errors);
        Assert.Empty(plain.Warnings);
    }

    [Fact]
    public void Validate_ReturnsPreview()
    {
        var dto = Validate("media.movie", "<b>{{ title }}</b>");

        Assert.Empty(dto.Errors);
        Assert.StartsWith("<b>", dto.Preview, System.StringComparison.Ordinal);
        Assert.Equal(dto.Preview.Length - 7, dto.Length);
    }

    private static TemplateUpdateRequest Req(long revision, string text)
        => new() { ExpectedRevision = revision, Text = text };

    private TemplateValidationDto Validate(string key, string text)
        => _controller.Validate(new TemplateValidateRequest { EventKey = key, Text = text }).Value!;

    private static int HtmlTextLength(string html) => Telegram.Bot.Extensions.HtmlText.PlainLength(html);

    public ValueTask DisposeAsync()
    {
        _h.Dispose();
        return ValueTask.CompletedTask;
    }
}
