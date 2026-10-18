using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.Strings;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Types;

namespace Jellyfin.Plugin.TgJelly.Bot;

/// <summary>
/// The best-effort bot-profile calls (spec §6.1 step 6): commands, descriptions, default channel admin rights.
/// Pending calls live in <c>state.telegram.profile</c> and are retried at each supervisor start until they succeed.
/// </summary>
public sealed class ProfileCalls
{
    /// <summary>
    /// Every profile call, in the order they run.
    /// </summary>
    public static readonly string[] AllCalls = ["commands", "descriptions", "adminRights"];

    private static readonly string[] Commands = ["menu", "status", "test", "help"];

    private readonly StateStore _state;
    private readonly StringTable _strings;
    private readonly IServerApplicationHost _host;
    private readonly ILogger<ProfileCalls> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProfileCalls"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="strings">The string table.</param>
    /// <param name="host">The server host, for its name.</param>
    /// <param name="logger">The logger.</param>
    public ProfileCalls(StateStore state, StringTable strings, IServerApplicationHost host, ILogger<ProfileCalls> logger)
    {
        _state = state;
        _strings = strings;
        _host = host;
        _logger = logger;
    }

    /// <summary>
    /// Runs the pending calls, removing each that succeeds. Failures are recorded and stay pending.
    /// </summary>
    /// <param name="sender">The sender.</param>
    /// <param name="ct">Cancels the calls.</param>
    /// <returns>A task.</returns>
    public async Task RunPendingAsync(ITelegramSender sender, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sender);
        var pending = _state.Read(s => s.Telegram.Profile.SetProfile ? s.Telegram.Profile.PendingCalls.ToArray() : []);
        foreach (var call in AllCalls.Where(pending.Contains))
        {
            try
            {
                await RunAsync(sender, call, ct).ConfigureAwait(false);
                _state.Update(s => s.Telegram.Profile.PendingCalls.Remove(call));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "TgJelly could not set the bot profile ({Call})", call);
                _state.RecordError("profile", call + ": " + ex.Message);
            }
        }
    }

    private Task RunAsync(ITelegramSender sender, string call, CancellationToken ct)
    {
        var server = _host.FriendlyName;
        return call switch
        {
            "commands" => sender.SetCommandsAsync([.. Commands.Select(c => new BotCommand(c, _strings.Get("command." + c)))], ct),
            "descriptions" => sender.SetDescriptionsAsync(_strings.Get("profile.description", ("server", server)), _strings.Get("profile.shortDescription", ("server", server)), ct),
            "adminRights" => sender.SetChannelAdminRightsAsync(ct),
            _ => Task.CompletedTask,
        };
    }
}
