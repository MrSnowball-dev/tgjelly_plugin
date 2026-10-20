using Scriban;
using Scriban.Parsing;
using Telegram.Bot.Extensions;

namespace Jellyfin.Plugin.TgJelly.Templates;

/// <summary>
/// A Scriban context that escapes every expression result for Telegram HTML (spec §8.5). Literal template text
/// is written unescaped, <see cref="RawHtml"/> as-is.
/// </summary>
public sealed class TelegramTemplateContext : TemplateContext
{
    /// <inheritdoc />
    public override TemplateContext Write(SourceSpan span, object? textAsObject)
    {
        switch (textAsObject)
        {
            case null:
                return this;
            case RawHtml raw:
                return base.Write(span, raw.Html);
            default:
                return base.Write(span, HtmlText.Escape(ObjectToString(textAsObject)));
        }
    }
}
