using System;
using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.Strings;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Rendering;

public class FormatterTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 18, 14, 5, 9, TimeSpan.Zero);
    private readonly Formatter _f = new(StringTable.LoadEmbedded());

    [Fact]
    public void Time_Today()
        => Assert.Equal($"<tg-time unix=\"{At.ToUnixTimeSeconds()}\" format=\"t\">14:05 UTC</tg-time>", _f.Time(At, At.AddHours(6)));

    [Fact]
    public void Time_AnotherDay()
    {
        var at = At.AddDays(-1);
        Assert.Equal($"<tg-time unix=\"{at.ToUnixTimeSeconds()}\" format=\"wDT\">Sat 17 Oct 14:05 UTC</tg-time>", _f.Time(at, At));
    }

    [Fact]
    public void Escape()
    {
        Assert.Equal("&amp;&lt;&gt;", _f.Escape("&<>"));
        Assert.Equal(string.Empty, _f.Escape(null));
    }

    [Theory]
    [InlineData(116, "1h 56m")]
    [InlineData(45, "45m")]
    [InlineData(60, "1h 0m")]
    public void Runtime(int minutes, string expected) => Assert.Equal(expected, _f.Runtime(TimeSpan.FromMinutes(minutes)));

    [Theory]
    [InlineData(7.85f, "7.9")]
    [InlineData(10f, "10.0")]
    public void Rating(float rating, string expected) => Assert.Equal(expected, _f.Rating(rating));

    [Fact]
    public void Number() => Assert.Equal("12,345", _f.Number(12345));

    [Theory]
    [InlineData("12.2.0.0", "12.2")]
    [InlineData("10.11.11.0", "10.11.11")]
    [InlineData("12.0.0.0", "12.0")]
    [InlineData("12.0", "12.0")]
    [InlineData("1.2.0.4", "1.2.0.4")]
    public void Version(string version, string expected) => Assert.Equal(expected, _f.Version(System.Version.Parse(version)));

    [Fact]
    public void DelayedLine() => Assert.Equal("⏱ " + _f.Time(At, At), _f.DelayedLine(At, At));
}
