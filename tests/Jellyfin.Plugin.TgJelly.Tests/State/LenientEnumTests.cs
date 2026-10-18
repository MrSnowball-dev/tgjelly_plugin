using System.Text.Json;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.State;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.State;

public class LenientEnumTests
{
    [Theory]
    [InlineData("""{"kind":{"x":1},"title":"t"}""")]
    [InlineData("""{"kind":[1,2],"title":"t"}""")]
    [InlineData("""{"kind":7,"title":"t"}""")]
    public void ObjectOrArrayValue_ForAnEnum_ReadsTheFallback_AndTheRestOfTheFileLoads(string json)
    {
        var holder = JsonSerializer.Deserialize<EnumHolder>(json, JsonDefaults.Options)!;

        Assert.Equal(ChatKind.Private, holder.Kind);
        Assert.Equal("t", holder.Title);
    }

    public sealed class EnumHolder
    {
        public ChatKind Kind { get; set; }

        public string Title { get; set; } = string.Empty;
    }
}
