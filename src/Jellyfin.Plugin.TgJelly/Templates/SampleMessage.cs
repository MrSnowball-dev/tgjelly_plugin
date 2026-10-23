using System.Collections.Generic;
using Jellyfin.Plugin.TgJelly.State.Documents;

namespace Jellyfin.Plugin.TgJelly.Templates;

/// <summary>
/// A sample message as it is sent.
/// </summary>
/// <param name="Html">The fitted HTML: a caption when <paramref name="Caption"/> is true, otherwise a text; <c>null</c> on error.</param>
/// <param name="Caption">True when the message goes out as a photo caption.</param>
/// <param name="Poster">True when the message has a poster.</param>
/// <param name="Buttons">The button rows.</param>
/// <param name="Length">The plain length of the unfitted render.</param>
/// <param name="Limit">The limit <paramref name="Html"/> is fitted to.</param>
/// <param name="Error">What stops the text from being sent, if anything.</param>
/// <param name="EventKey">The template key; its group decides whether a test send is silent (the digest counts as media).</param>
/// <param name="TextHtml">The HTML fitted to the text limit, for when the poster cannot be sent; <c>null</c> falls back to <paramref name="Html"/>.</param>
/// <param name="MarkedHtml">The shown HTML with each template piece's output marked, for the dashboard's preview only; never sent.</param>
public sealed record SampleMessage(
    string? Html,
    bool Caption,
    bool Poster,
    IReadOnlyList<IReadOnlyList<OutboxButton>> Buttons,
    int Length,
    int Limit,
    TemplateError? Error,
    string? TextHtml = null,
    string EventKey = "",
    string? MarkedHtml = null);
