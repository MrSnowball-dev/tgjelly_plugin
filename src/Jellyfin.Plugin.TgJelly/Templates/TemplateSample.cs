using System;
using System.Collections.Generic;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Strings;
using Telegram.Bot.Extensions;

namespace Jellyfin.Plugin.TgJelly.Templates;

/// <summary>
/// A template as one of its sample messages looks when sent: fitted, with its buttons and its poster flag. The
/// dashboard's preview and the test send both come from it.
/// </summary>
public sealed class TemplateSample
{
    private readonly TemplateRenderer _renderer;
    private readonly TemplateEngine _engine;
    private readonly TemplateModelBuilder _models;
    private readonly SettingsStore _settings;
    private readonly StringTable _strings;

    /// <summary>
    /// Initializes a new instance of the <see cref="TemplateSample"/> class.
    /// </summary>
    /// <param name="renderer">Fits and checks template text.</param>
    /// <param name="engine">The template engine, for the unfitted length.</param>
    /// <param name="models">Builds the sample models.</param>
    /// <param name="settings">The settings: the message's switches, the public URL and the language.</param>
    /// <param name="strings">The string table, for the button labels.</param>
    public TemplateSample(TemplateRenderer renderer, TemplateEngine engine, TemplateModelBuilder models, SettingsStore settings, StringTable strings)
    {
        _renderer = renderer;
        _engine = engine;
        _models = models;
        _settings = settings;
        _strings = strings;
    }

    /// <summary>
    /// Renders a template with the event's sample model.
    /// </summary>
    /// <param name="eventKey">The template key.</param>
    /// <param name="text">The template text.</param>
    /// <returns>The sample message; <see cref="SampleMessage.Error"/> is set when the text cannot be sent.</returns>
    public SampleMessage Build(string eventKey, string text)
    {
        ArgumentNullException.ThrowIfNull(eventKey);
        ArgumentNullException.ThrowIfNull(text);
        var config = _settings.Current;
        var values = _models.Sample(eventKey);
        var poster = eventKey != TemplateKeys.Digest && config.Message.For(eventKey).ShowPoster;
        var limit = poster ? TemplateFitter.CaptionLimit : TemplateFitter.TextLimit;
        var buttons = Buttons(eventKey, config);
        var unfitted = _engine.Render(eventKey, text, values.Values, strict: false, config.Language).Output ?? string.Empty;
        var length = HtmlText.PlainLength(unfitted);

        var (caption, full, problem) = _renderer.FitBoth(eventKey, text, values, config.Language);
        return problem is not null
            ? new SampleMessage(null, poster, poster, buttons, length, limit, problem, null, eventKey)
            : new SampleMessage(poster ? caption!.Html : full!.Html, poster, poster, buttons, length, limit, null, full!.Html, eventKey);
    }

    private List<IReadOnlyList<OutboxButton>> Buttons(string eventKey, PluginConfiguration config)
    {
        var rows = new List<IReadOnlyList<OutboxButton>>();
        var options = config.Message.For(eventKey);

        // As sent: media messages carry the Open and provider rows; alerts and the digest have none of them.
        var media = TemplateFieldCatalog.IsMedia(eventKey) && eventKey != TemplateKeys.Digest;
        if (media && options.ShowOpenInJellyfin && LinkBuilder.Normalize(config.PublicUrl) is { } root)
        {
            rows.Add([new OutboxButton { Text = _strings.Get("button.open"), Url = root + "/web/" }]);
        }

        var sample = TemplateFieldCatalog.SampleFull(eventKey);
        if (media && options.ShowProviderLinks && eventKey is "media.movie" or "media.episodes")
        {
            var row = new List<OutboxButton>(2);
            if (sample.TryGetValue("imdb_id", out var imdb) && imdb is string imdbId && imdbId.Length > 0)
            {
                row.Add(new OutboxButton { Text = _strings.Get("button.imdb"), Url = LinkBuilder.Imdb(imdbId) });
            }

            if (sample.TryGetValue("tmdb_id", out var tmdb) && tmdb is string tmdbId && tmdbId.Length > 0)
            {
                row.Add(new OutboxButton { Text = _strings.Get("button.tmdb"), Url = LinkBuilder.Tmdb(eventKey == "media.episodes", tmdbId) });
            }

            if (row.Count > 0)
            {
                rows.Add(row);
            }
        }

        if (eventKey == "server.update_available" && sample.TryGetValue("url", out var url) && url is string notes && notes.Length > 0)
        {
            rows.Add([new OutboxButton { Text = _strings.Get("button.releaseNotes"), Url = notes }]);
        }

        return rows;
    }
}
