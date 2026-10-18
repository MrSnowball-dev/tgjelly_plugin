using System;
using System.Threading;

namespace Jellyfin.Plugin.TgJelly.Bot;

/// <summary>
/// The current bot: its sender, identity and connection state. <c>BotSupervisor</c> swaps it on token or
/// proxy changes; everything else reads it.
/// </summary>
public sealed class BotConnection
{
    private readonly Lock _gate = new();
    private ConnectionState _state = ConnectionState.NoToken;
    private string? _detail;
    private ITelegramSender? _sender;
    private long _botId;
    private string _botUsername = string.Empty;

    /// <summary>
    /// Raised after the state, the sender or the identity changes.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Gets the connection state.
    /// </summary>
    public ConnectionState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>
    /// Gets extra detail for the state, e.g. an error description.
    /// </summary>
    public string? Detail
    {
        get
        {
            lock (_gate)
            {
                return _detail;
            }
        }
    }

    /// <summary>
    /// Gets the sender of the current bot, or <c>null</c> when none is attached.
    /// </summary>
    public ITelegramSender? Sender
    {
        get
        {
            lock (_gate)
            {
                return _sender;
            }
        }
    }

    /// <summary>
    /// Gets the current bot's user ID, or 0.
    /// </summary>
    public long BotId
    {
        get
        {
            lock (_gate)
            {
                return _botId;
            }
        }
    }

    /// <summary>
    /// Gets the current bot's username, or an empty string.
    /// </summary>
    public string BotUsername
    {
        get
        {
            lock (_gate)
            {
                return _botUsername;
            }
        }
    }

    /// <summary>
    /// Sets the state.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="detail">Extra detail, if any.</param>
    public void SetState(ConnectionState state, string? detail = null)
    {
        lock (_gate)
        {
            if (_state == state && _detail == detail)
            {
                return;
            }

            _state = state;
            _detail = detail;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Attaches a bot.
    /// </summary>
    /// <param name="sender">Its sender.</param>
    /// <param name="botId">Its user ID.</param>
    /// <param name="botUsername">Its username.</param>
    public void Attach(ITelegramSender sender, long botId, string botUsername)
    {
        ArgumentNullException.ThrowIfNull(sender);
        lock (_gate)
        {
            _sender = sender;
            _botId = botId;
            _botUsername = botUsername ?? string.Empty;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Detaches the bot; the identity stays known for display.
    /// </summary>
    public void Detach()
    {
        lock (_gate)
        {
            if (_sender is null)
            {
                return;
            }

            _sender = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Returns the sender, or throws when no bot is attached.
    /// </summary>
    /// <returns>The sender.</returns>
    /// <exception cref="BotNotConnectedException">No bot is attached.</exception>
    public ITelegramSender RequireSender() => Sender ?? throw new BotNotConnectedException();
}
