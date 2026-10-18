using System;
using Jellyfin.Plugin.TgJelly.Bot;
using Telegram.Bot;

namespace Jellyfin.Plugin.TgJelly.Tests.Fakes;

/// <summary>
/// Returns clients over a <see cref="FakeBotApi"/>, or throws what <see cref="Throw"/> says.
/// </summary>
public sealed class FakeClientFactory(FakeBotApi api) : ITelegramClientFactory
{
    public Func<string, Exception?> Throw { get; set; } = _ => null;

    public int Created { get; private set; }

    public ITelegramBotClient Create(string token)
    {
        if (Throw(token) is { } ex)
        {
            throw ex;
        }

        Created++;
        return api.CreateClient();
    }
}
