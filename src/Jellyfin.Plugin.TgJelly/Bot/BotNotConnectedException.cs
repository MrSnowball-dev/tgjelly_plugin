using System;

namespace Jellyfin.Plugin.TgJelly.Bot;

/// <summary>
/// Thrown when Telegram is needed but no bot is connected.
/// </summary>
public sealed class BotNotConnectedException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BotNotConnectedException"/> class.
    /// </summary>
    public BotNotConnectedException()
        : base("No Telegram bot is connected.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BotNotConnectedException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public BotNotConnectedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BotNotConnectedException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public BotNotConnectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
