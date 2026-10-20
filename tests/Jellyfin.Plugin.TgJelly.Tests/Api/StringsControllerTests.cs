using Jellyfin.Plugin.TgJelly.Api;
using Jellyfin.Plugin.TgJelly.Strings;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Api;

public class StringsControllerTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    [InlineData(null)]
    public void StringsController_ReturnsKeysUnchanged(string? lang)
    {
        var table = new StringsController().Get(lang).Value!;

        Assert.Equal("Connected as @{bot}", table["connection.Connected"]);
        Assert.Equal(StringTable.LoadEmbedded().Snapshot().Count, table.Count);
    }
}
