using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Bot.Screens;
using Jellyfin.Plugin.TgJelly.Configuration;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Bot.Screens;

public class CallbackDataTests
{
    private const string Id = "abcd1234";
    private const string Page = "99";
    private const string ChatId = "-1009999999999999";
    private static readonly string LongestEvent = EventCatalog.AllKeys.MaxBy(k => k.Length)!;
    private static readonly string LibraryId = Guid.NewGuid().ToString("N");

    /// <summary>Every action FlowEngine.Actions names, with the longest arguments that action ever carries.</summary>
    public static TheoryData<string, string[]> MaxLengthShapes
    {
        get
        {
            var data = new TheoryData<string, string[]>();
            foreach (var (action, args) in Shapes())
            {
                data.Add(action, args);
            }

            return data;
        }
    }

    private static Dictionary<string, string[]> Shapes()
    {
        {
            var rows = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var a in new[] { "m", "st", "noop", "s", "sm", "su", "sut", "sum", "sp", "spu", "spt", "spc", "oa", "ot", "oh", "on", "ox", "ol", "od", "tg", "tc", "te", "tb", "tk" })
            {
                rows[a] = [];
            }

            foreach (var a in new[] { "dl", "tp", "xl" })
            {
                rows[a] = [Page];
            }

            foreach (var a in new[] { "d", "dt", "dc", "dp", "dr", "dsg" })
            {
                rows[a] = [Id];
            }

            rows["de"] = [Id];
            rows["dlb"] = [Id, Page];
            rows["deg"] = [Id, "security"];

            rows["det"] = [Id, LongestEvent];
            rows["dlt"] = [Id, LibraryId];
            rows["dsgt"] = [Id, "security"];
            rows["drx"] = [Id, "1"];
            rows["dtp"] = [Id];
            rows["dts"] = [Id, "media.musicvideo"];
            rows["dtc"] = [Id, "media.musicvideo", "2147483647"];
            rows["smt"] = ["ShowRatingGenres"];
            rows["xs"] = [ChatId];
            rows["xd"] = [ChatId];

            return rows;
        }
    }

    [Fact]
    public void RoundTrips()
    {
        var data = CallbackData.Format("dt", "abcd1234", "media.movie");

        Assert.Equal("1|dt|abcd1234|media.movie", data);
        Assert.True(CallbackData.TryParse(data, out var action, out var args));
        Assert.Equal("dt", action);
        Assert.Equal(["abcd1234", "media.movie"], args);
    }

    [Fact]
    public void ActionWithoutArgs_RoundTrips()
    {
        Assert.True(CallbackData.TryParse(CallbackData.Format("m"), out var action, out var args));
        Assert.Equal("m", action);
        Assert.Empty(args);
    }

    [Theory]
    [InlineData("2|m")]
    [InlineData("m")]
    [InlineData("1|")]
    [InlineData("1")]
    [InlineData("")]
    [InlineData(null)]
    public void RejectsOtherVersionsAndEmptyActions(string? data)
        => Assert.False(CallbackData.TryParse(data, out _, out _));

    [Fact]
    public void RejectsOtherVersions()
        => Assert.False(CallbackData.TryParse("0|m", out _, out _));

    [Fact]
    public void Over64Bytes_Throws()
    {
        Assert.Equal(64, CallbackData.Format("x", new string('a', 60)).Length);
        Assert.Throws<ArgumentException>(() => CallbackData.Format("x", new string('a', 61)));
        Assert.Throws<ArgumentException>(() => CallbackData.Format("x", new string('é', 31))); // 62 bytes + prefix
    }

    [Fact]
    public void SeparatorInAPart_Throws()
    {
        Assert.Throws<ArgumentException>(() => CallbackData.Format("a|b"));
        Assert.Throws<ArgumentException>(() => CallbackData.Format("a", "b|c"));
    }

    [Fact]
    public void TheTable_CoversExactlyTheEngineActions()
        => Assert.Equal(
            [.. Jellyfin.Plugin.TgJelly.Bot.Screens.FlowEngine.Actions.Order(StringComparer.Ordinal)],
            [.. Shapes().Keys.Order(StringComparer.Ordinal)]);

    [Theory]
    [MemberData(nameof(MaxLengthShapes))]
    public void EveryActionThisPlanUses_FitsWithMaxLengthArgs(string action, string[] args)
    {
        var data = CallbackData.Format(action, args);

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(data) <= CallbackData.MaxBytes);
        Assert.True(CallbackData.TryParse(data, out var parsed, out var parsedArgs));
        Assert.Equal(action, parsed);
        Assert.Equal(args, parsedArgs);
    }
}
