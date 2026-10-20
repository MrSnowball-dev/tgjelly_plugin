using System;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Strings;
using Jellyfin.Plugin.TgJelly.Templates;
using Telegram.Bot.Extensions;

namespace Jellyfin.Plugin.TgJelly.Rendering;

/// <summary>
/// Renders a model for one destination (spec §8): picks the layout by <see cref="RenderModel.Kind"/> and applies the
/// destination's silent groups (§8.4). A custom template (§8.5) replaces the caption and text when it renders cleanly
/// to some text; otherwise the default layout is kept and the template is flagged.
/// </summary>
public sealed class NotificationRenderer
{
    private readonly MediaLayouts _media;
    private readonly AlertLayouts _alerts;
    private readonly TemplateEngine _engine;
    private readonly TemplateModelBuilder _models;
    private readonly TemplateFlags _flags;
    private readonly SettingsStore _settings;
    private readonly StringTable _strings;

    /// <summary>
    /// Initializes a new instance of the <see cref="NotificationRenderer"/> class.
    /// </summary>
    /// <param name="media">The media layouts.</param>
    /// <param name="alerts">The security and server layouts.</param>
    /// <param name="engine">The template engine.</param>
    /// <param name="models">The template model builder.</param>
    /// <param name="flags">The template flags.</param>
    /// <param name="settings">The settings, for the template overrides.</param>
    /// <param name="strings">The string table, for the flag messages.</param>
    public NotificationRenderer(MediaLayouts media, AlertLayouts alerts, TemplateEngine engine, TemplateModelBuilder models, TemplateFlags flags, SettingsStore settings, StringTable strings)
    {
        _media = media;
        _alerts = alerts;
        _engine = engine;
        _models = models;
        _flags = flags;
        _settings = settings;
        _strings = strings;
    }

    /// <summary>
    /// Renders a message.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="destination">The destination it goes to.</param>
    /// <returns>The message, or <c>null</c> when a media group's items are all gone.</returns>
    public OutboxMessage? Render(RenderModel model, Destination destination)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(destination);
        var message = model.Kind switch
        {
            RenderKind.Media => _media.Render(model),
            RenderKind.Digest => _media.RenderDigest(model),
            _ => _alerts.Render(model),
        };
        if (message is not null)
        {
            if (model.Kind != RenderKind.Digest)
            {
                ApplyTemplate(model, message);
            }

            message.Silent = destination.SilentGroups.Contains(EventCatalog.GroupOf(model.EventKey), StringComparer.Ordinal);
        }

        return message;
    }

    private static string Fit(string html, int budget)
        => HtmlText.PlainLength(html) > budget ? HtmlText.Truncate(html, budget, "…") : html;

    private void ApplyTemplate(RenderModel model, OutboxMessage message)
    {
        var key = model.EventKey;
        var template = _settings.Current.Templates.FirstOrDefault(t => string.Equals(t.EventKey, key, StringComparison.Ordinal));
        if (template is not { Enabled: true } || string.IsNullOrWhiteSpace(template.Text))
        {
            return;
        }

        var hash = TemplateText.Hash(template.Text);
        try
        {
            var result = _engine.Render(key, template.Text, _models.Build(model), strict: false);
            if (!result.Ok)
            {
                _flags.Flag(key, "error", Describe(result.Error!), hash);
                return;
            }

            var output = result.Output!;
            if (TelegramHtmlValidator.Validate(output) is { Count: > 0 } problems)
            {
                _flags.Flag(key, "error", problems[0].Describe(_strings), hash);
                return;
            }

            // Telegram refuses an empty message, which would lose the notification: keep the default instead.
            if (string.IsNullOrWhiteSpace(HtmlText.ToPlain(output)))
            {
                _flags.Flag(key, "error", _strings.Get("templates.empty"), hash);
                return;
            }

            var captionBudget = MessageFitter.CaptionLimit - MessageFitter.DelayedReserve;
            var textBudget = MessageFitter.TextLimit - MessageFitter.DelayedReserve;
            message.Caption = Fit(output, captionBudget);
            message.Text = Fit(output, textBudget);
            message.TemplateKey = key;
            var budget = message.PosterPath is null ? textBudget : captionBudget;
            if (HtmlText.PlainLength(output) > budget)
            {
                _flags.Flag(key, "truncated", string.Empty, hash);
            }
        }
        catch (Exception ex)
        {
            // Rendering must never fail the message: keep the default, and a failing flag write changes nothing.
            try
            {
                message.TemplateKey = null;
                _flags.Flag(key, "error", ex.Message, hash);
            }
            catch (Exception)
            {
                // Ignored on purpose.
            }
        }
    }

    private static string Describe(TemplateError error)
        => error.Line is { } line && error.Column is { } column
            ? string.Create(CultureInfo.InvariantCulture, $"line {line}, column {column}: {error.Message}")
            : error.Message;
}
