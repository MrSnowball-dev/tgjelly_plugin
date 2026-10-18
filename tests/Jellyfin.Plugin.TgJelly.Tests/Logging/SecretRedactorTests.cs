using Jellyfin.Plugin.TgJelly.Logging;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Logging;

public class SecretRedactorTests
{
    [Theory]
    [InlineData("https://api.telegram.org/bot123456789:AAEhBP0av28KrBd9xL4Zq3Wm7Tn5Ys1Cv6QpZ/getMe", "https://api.telegram.org/bot<token>/getMe")]
    [InlineData("token 123456789:AAEhBP0av28KrBd9xL4Zq3Wm7Tn5Ys1Cv6QpZ, again 123456789:AAEhBP0av28KrBd9xL4Zq3Wm7Tn5Ys1Cv6QpZ", "token <token>, again <token>")]
    [InlineData("chat -1001234567890 and time 12:30", "chat -1001234567890 and time 12:30")]
    [InlineData(null, "")]
    public void Redact_ReplacesTokens_AndLeavesOtherNumbersAlone(string? input, string expected)
        => Assert.Equal(expected, SecretRedactor.Redact(input));
}
