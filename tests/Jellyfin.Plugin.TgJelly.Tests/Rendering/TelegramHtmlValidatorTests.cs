using Jellyfin.Plugin.TgJelly.Rendering;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Rendering;

public sealed class TelegramHtmlValidatorTests
{
    [Theory]
    [InlineData("plain text")]
    [InlineData("<b>x</b>")]
    [InlineData("<strong>x</strong><em>y</em><i>z</i><u>a</u><ins>b</ins><s>c</s><strike>d</strike><del>e</del>")]
    [InlineData("<span class=\"tg-spoiler\">x</span>")]
    [InlineData("<tg-spoiler>x</tg-spoiler>")]
    [InlineData("<a href=\"https://example.com/?a=1&amp;b=2\">x</a>")]
    [InlineData("<code>x</code><pre>y</pre>")]
    [InlineData("<blockquote>x</blockquote>")]
    [InlineData("<blockquote expandable>x</blockquote>")]
    [InlineData("<tg-emoji emoji-id=\"1\">x</tg-emoji>")]
    [InlineData("<tg-time unix=\"1\" format=\"r\">x</tg-time>")]
    [InlineData("<b>a <i>b</i></b>")]
    public void Allowed_HasNoProblems(string html) => Assert.Empty(TelegramHtmlValidator.Validate(html));

    [Theory]
    [InlineData("<div>x</div>")]
    [InlineData("<span>x</span>")]
    [InlineData("<blockquote foo>x</blockquote>")]
    [InlineData("<b><i>x</b></i>")]
    [InlineData("<b>x")]
    [InlineData("x</b>")]
    [InlineData("x</div>")]
    [InlineData("</script>")]
    public void Forbidden_HasOneProblem(string html) => Assert.Single(TelegramHtmlValidator.Validate(html));

    [Fact]
    public void Entities_Pass() => Assert.Empty(TelegramHtmlValidator.Validate("a &lt; b &amp; c &gt; d &quot;x&quot; &#39;"));
}
