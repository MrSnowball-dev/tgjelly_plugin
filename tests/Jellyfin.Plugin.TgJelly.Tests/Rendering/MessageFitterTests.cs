using System.Linq;
using Jellyfin.Plugin.TgJelly.Rendering;
using Telegram.Bot.Extensions;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Rendering;

public class MessageFitterTests
{
    private const int Budget = MessageFitter.CaptionLimit - MessageFitter.DelayedReserve;

    private static string More(int n) => $"+{n} more";

    [Fact]
    public void Fits_Unchanged_SegmentsJoinedInOrder()
    {
        var parts = new MessageParts("<b>Head</b>", ["E01 a", "E02 b"], "Over & view", "📁 Movies");

        Assert.Equal("<b>Head</b>\n\nE01 a\nE02 b\n\nOver &amp; view\n\n📁 Movies", MessageFitter.Fit(parts, MessageFitter.CaptionLimit, More));
        Assert.Equal("<b>Head</b>\n\n📁 Movies", MessageFitter.Fit(new MessageParts("<b>Head</b>", [], null, "📁 Movies"), MessageFitter.CaptionLimit, More));
    }

    [Fact]
    public void LongOverview_IsShortenedFirst_WithEllipsis()
    {
        var result = MessageFitter.Fit(new MessageParts("<b>Head</b>", [], new string('a', 3000), "📁 Movies"), MessageFitter.CaptionLimit, More);

        Assert.True(HtmlText.PlainLength(result) <= Budget);
        Assert.StartsWith("<b>Head</b>\n\naaa", result, System.StringComparison.Ordinal);
        Assert.EndsWith("a…\n\n📁 Movies", result, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ThenTheList_WithMore()
    {
        var lines = Enumerable.Range(1, 200).Select(i => $"E{i:000} An episode title").ToArray();

        var result = MessageFitter.Fit(new MessageParts("<b>Show</b>", lines, null, "📁 Shows"), MessageFitter.CaptionLimit, More);

        Assert.True(HtmlText.PlainLength(result) <= Budget);
        var listed = result.Split('\n').Count(l => l.StartsWith('E'));
        Assert.Contains($"\n+{200 - listed} more\n", result, System.StringComparison.Ordinal);
        Assert.EndsWith("📁 Shows", result, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ThenTruncate()
    {
        var result = MessageFitter.Fit(new MessageParts(new string('h', 2000), [], null, string.Empty), MessageFitter.CaptionLimit, More);

        Assert.True(HtmlText.PlainLength(result) <= Budget);
        Assert.EndsWith("…", result, System.StringComparison.Ordinal);
    }

    [Fact]
    public void NonBmpOverview_IsCutOnCodePoints_AndFits()
    {
        var result = MessageFitter.Fit(new MessageParts("<b>Head</b>", [], string.Concat(Enumerable.Repeat("𝕏", 2000)), "📁 Movies"), MessageFitter.CaptionLimit, More);

        Assert.True(HtmlText.PlainLength(result) <= Budget);
        var overview = result.Split("\n\n")[1];
        Assert.EndsWith("𝕏…", overview, System.StringComparison.Ordinal);
        Assert.DoesNotContain(overview.Select((c, i) => (c, i)), p => char.IsHighSurrogate(p.c) && (p.i + 1 >= overview.Length || !char.IsLowSurrogate(overview[p.i + 1])));
    }

    [Fact]
    public void ExactlyAtTheLimit_IsUntouched()
    {
        var head = string.Concat(Enumerable.Repeat("𝕏", Budget));

        Assert.Equal(head, MessageFitter.Fit(new MessageParts(head, [], null, string.Empty), MessageFitter.CaptionLimit, More));
        Assert.NotEqual(head + "𝕏", MessageFitter.Fit(new MessageParts(head + "𝕏", [], null, string.Empty), MessageFitter.CaptionLimit, More));
    }
}
