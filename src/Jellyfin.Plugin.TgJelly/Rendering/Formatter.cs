using System;
using System.Globalization;
using Jellyfin.Plugin.TgJelly.Strings;
using Telegram.Bot.Extensions;

namespace Jellyfin.Plugin.TgJelly.Rendering;

/// <summary>
/// Formats values for messages: escaping, <c>&lt;tg-time&gt;</c>, numbers, ratings, runtimes and versions,
/// with the text from the string table.
/// </summary>
public sealed class Formatter
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");

    /// <summary>
    /// Initializes a new instance of the <see cref="Formatter"/> class.
    /// </summary>
    /// <param name="strings">The string table.</param>
    public Formatter(StringTable strings) => Strings = strings;

    /// <summary>Gets the string table.</summary>
    public StringTable Strings { get; }

    /// <summary>
    /// Escapes text for Telegram HTML.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The escaped text; empty for <c>null</c>.</returns>
    public string Escape(string? text) => HtmlText.Escape(text ?? string.Empty);

    /// <summary>
    /// Writes a time that each reader sees in their own time zone, with a UTC fallback text.
    /// </summary>
    /// <param name="at">The time.</param>
    /// <param name="now">The current time, which decides whether the date is shown.</param>
    /// <returns>The <c>tg-time</c> element.</returns>
    public string Time(DateTimeOffset at, DateTimeOffset now)
    {
        var today = at.UtcDateTime.Date == now.UtcDateTime.Date;
        var text = at.UtcDateTime.ToString(today ? "HH:mm" : "ddd d MMM HH:mm", Culture);
        return string.Create(Culture, $"<tg-time unix=\"{at.ToUnixTimeSeconds()}\" format=\"{(today ? "t" : "wDT")}\">{Strings.Get("time.utc", ("time", text))}</tg-time>");
    }

    /// <summary>
    /// Formats a whole number with digit grouping.
    /// </summary>
    /// <param name="n">The number.</param>
    /// <returns>The text.</returns>
    public string Number(long n) => n.ToString("N0", Culture);

    /// <summary>
    /// Formats a rating with one decimal.
    /// </summary>
    /// <param name="rating">The rating.</param>
    /// <returns>The text, e.g. <c>7.9</c>.</returns>
    public string Rating(float rating)
        => Math.Round((decimal)rating, 1, MidpointRounding.AwayFromZero).ToString("0.0", Culture);

    /// <summary>
    /// Formats a runtime as hours and minutes.
    /// </summary>
    /// <param name="runtime">The runtime.</param>
    /// <returns>The text, e.g. <c>1h 56m</c>.</returns>
    public string Runtime(TimeSpan runtime)
    {
        var hours = (int)runtime.TotalHours;
        return hours > 0
            ? Strings.Get("media.runtime.hours", ("hours", hours), ("minutes", runtime.Minutes))
            : Strings.Get("media.runtime.minutes", ("minutes", runtime.Minutes));
    }

    /// <summary>
    /// Formats a version without trailing zero parts beyond the minor one (<c>12.2.0.0</c> → <c>12.2</c>).
    /// </summary>
    /// <param name="version">The version.</param>
    /// <returns>The text.</returns>
    public string Version(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var build = Math.Max(version.Build, 0);
        var revision = Math.Max(version.Revision, 0);
        return revision > 0 ? string.Create(Culture, $"{version.Major}.{version.Minor}.{build}.{revision}")
            : build > 0 ? string.Create(Culture, $"{version.Major}.{version.Minor}.{build}")
            : string.Create(Culture, $"{version.Major}.{version.Minor}");
    }

    /// <summary>
    /// The line added to a message that leaves the Outbox late.
    /// </summary>
    /// <param name="queuedAt">When the message was queued.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The line.</returns>
    public string DelayedLine(DateTimeOffset queuedAt, DateTimeOffset now)
        => Strings.Get("delivery.delayed", ("time", Time(queuedAt, now)));
}
