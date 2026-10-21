using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Strings;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Extensions;

namespace Jellyfin.Plugin.TgJelly.Templates;

/// <summary>
/// Renders a message's caption and text from its template (design §5): the edited template when there is one,
/// otherwise the built-in one, each fitted to the caption and text limits. An edited template that fails is flagged
/// and the built-in one is used; a built-in one that fails is logged and a short last-resort text is sent (§5.4).
/// </summary>
public sealed class TemplateRenderer
{
    private const string BuiltInLanguage = "en";
    private static readonly string[] LastResortFields = ["title", "series", "album", "user", "plugin", "task"];

    private readonly TemplateEngine _engine;
    private readonly TemplateModelBuilder _models;
    private readonly TemplateFlags _flags;
    private readonly SettingsStore _settings;
    private readonly StringTable _strings;
    private readonly ILogger<TemplateRenderer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TemplateRenderer"/> class.
    /// </summary>
    /// <param name="engine">The template engine.</param>
    /// <param name="models">The template model builder.</param>
    /// <param name="flags">The template flags.</param>
    /// <param name="settings">The settings: the edited templates and the language.</param>
    /// <param name="strings">The string table, for the flag messages and the last-resort text.</param>
    /// <param name="logger">Where a failing built-in template is reported.</param>
    public TemplateRenderer(TemplateEngine engine, TemplateModelBuilder models, TemplateFlags flags, SettingsStore settings, StringTable strings, ILogger<TemplateRenderer> logger)
    {
        _engine = engine;
        _models = models;
        _flags = flags;
        _settings = settings;
        _strings = strings;
        _logger = logger;
    }

    /// <summary>
    /// Returns whether a key has a built-in template, so its messages come from templates.
    /// </summary>
    /// <param name="eventKey">The template key.</param>
    /// <returns><c>true</c> when an English built-in template exists for the key.</returns>
    public bool Covers(string eventKey) => HasBuiltIn(eventKey);

    /// <summary>
    /// Renders a message's caption and text.
    /// </summary>
    /// <param name="model">What the message is rendered from.</param>
    /// <param name="hasPoster">True when the message goes out as a photo, so the caption decides the "truncated" flag.</param>
    /// <returns>The text, or <c>null</c> when the key has no built-in template.</returns>
    public RenderedText? Render(RenderModel model, bool hasPoster)
    {
        ArgumentNullException.ThrowIfNull(model);
        var key = model.EventKey;
        if (!Covers(key))
        {
            return null;
        }

        var config = _settings.Current;
        var language = config.Language;
        TemplateModel values;
        try
        {
            values = _models.Build(model);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning("The message model for {EventKey} could not be built: {Error}", key, ex.Message);
            return LastResort(key, null);
        }

        // Design §5.1: Enabled is ignored; an override with text is the template in use.
        var edited = config.Templates.FirstOrDefault(t => string.Equals(t.EventKey, key, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(t.Text));
        if (edited is not null && RenderEdited(key, edited.Text, values, language, hasPoster) is { } fromEdited)
        {
            return fromEdited;
        }

        var builtIn = BuiltInTemplates.Get(key, language)!;
        string? failure;
        try
        {
            var (caption, text, problem) = FitBoth(key, builtIn, values, language);
            if (problem is null)
            {
                return new RenderedText(caption!.Html!, text!.Html!, null);
            }

            failure = problem;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failure = ex.Message;
        }

        _logger.LogWarning("The built-in template for {EventKey} failed: {Error}", key, failure);
        return LastResort(key, values);
    }

    /// <summary>
    /// Returns whether an English built-in template exists for a key.
    /// </summary>
    /// <param name="eventKey">The template key.</param>
    /// <returns><c>true</c> when it exists.</returns>
    internal static bool HasBuiltIn(string eventKey)
    {
        ArgumentNullException.ThrowIfNull(eventKey);
        return BuiltInTemplates.Get(eventKey, BuiltInLanguage) is not null;
    }

    /// <summary>
    /// Describes a template error as the flags show it.
    /// </summary>
    /// <param name="error">The error.</param>
    /// <returns><c>line L, column C: message</c>, or the message alone when the position is unknown.</returns>
    internal static string Describe(TemplateError error)
        => error.Line is { } line && error.Column is { } column
            ? string.Create(CultureInfo.InvariantCulture, $"line {line}, column {column}: {error.Message}")
            : error.Message;

    private RenderedText? RenderEdited(string key, string text, TemplateModel values, string language, bool hasPoster)
    {
        var hash = TemplateText.Hash(text);
        try
        {
            var (caption, full, problem) = FitBoth(key, text, values, language);
            if (problem is not null)
            {
                SafeFlag(key, "error", problem, hash);
                return null;
            }

            if (hasPoster ? caption!.Truncated : full!.Truncated)
            {
                SafeFlag(key, "truncated", string.Empty, hash);
            }

            return new RenderedText(caption!.Html!, full!.Html!, key);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Rendering must never fail the message: the built-in template takes over.
            SafeFlag(key, "error", ex.Message, hash);
            return null;
        }
    }

    // Fits a template to both limits. The problem is the flag text of the first error, whitelist problem or empty part.
    // The digest is sent as text only, so its caption stays empty, as in v0.2.
    private (FitResult? Caption, FitResult? Text, string? Problem) FitBoth(string key, string text, TemplateModel values, string language)
    {
        TemplateRenderResult Render(IReadOnlyDictionary<string, object?> variant)
            => _engine.Render(key, text, variant, strict: false, language);

        var caption = new FitResult(string.Empty, false, null);
        if (key != TemplateKeys.Digest)
        {
            caption = TemplateFitter.Fit(values, Render, TemplateFitter.CaptionLimit);
            var captionProblem = Problem(caption);
            if (captionProblem is not null)
            {
                return (null, null, captionProblem);
            }
        }

        var full = TemplateFitter.Fit(values, Render, TemplateFitter.TextLimit);
        var problem = Problem(full);
        return problem is null ? (caption, full, null) : (null, null, problem);
    }

    private string? Problem(FitResult result)
    {
        if (result.Error is { } error)
        {
            return Describe(error);
        }

        var html = result.Html ?? string.Empty;
        if (TelegramHtmlValidator.Validate(html) is { Count: > 0 } problems)
        {
            return problems[0].Describe(_strings);
        }

        // Telegram refuses an empty message, which would lose the notification.
        return string.IsNullOrWhiteSpace(HtmlText.ToPlain(html)) ? _strings.Get("templates.empty") : null;
    }

    private RenderedText LastResort(string key, TemplateModel? values)
    {
        var label = _strings.Get(key == TemplateKeys.Digest ? "templates.digestLabel" : "dest.event." + key);
        var html = "<b>" + HtmlText.Escape(label) + "</b>";
        var name = values is null
            ? null
            : LastResortFields.Select(f => values.Values.TryGetValue(f, out var v) ? v as string : null).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        if (name is not null)
        {
            html += "\n" + HtmlText.Escape(name);
        }

        return new RenderedText(key == TemplateKeys.Digest ? string.Empty : html, html, null);
    }

    private void SafeFlag(string key, string kind, string error, string hash)
    {
        try
        {
            _flags.Flag(key, kind, error, hash);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A failing flag write must not change the message.
            _logger.LogWarning("The template flag for {EventKey} could not be saved: {Error}", key, ex.Message);
        }
    }
}
