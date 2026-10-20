using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.Destinations;

/// <summary>
/// Adds a checked chat as a destination (spec §6.2), shared by the API and the bot: the duplicate rule, the save, the
/// health record and the automatic test message.
/// </summary>
public sealed class DestinationCreator
{
    private readonly SettingsStore _settings;
    private readonly HealthService _health;
    private readonly DestinationTester _tester;
    private readonly BotMemory _memory;
    private readonly ILogger<DestinationCreator> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DestinationCreator"/> class.
    /// </summary>
    /// <param name="settings">The settings store.</param>
    /// <param name="health">The health service.</param>
    /// <param name="tester">The test sender.</param>
    /// <param name="memory">The bot's memory; a chat that becomes a destination stops being a detected chat.</param>
    /// <param name="logger">The logger.</param>
    public DestinationCreator(SettingsStore settings, HealthService health, DestinationTester tester, BotMemory memory, ILogger<DestinationCreator> logger)
    {
        _settings = settings;
        _health = health;
        _tester = tester;
        _memory = memory;
        _logger = logger;
    }

    /// <summary>
    /// Finds the destination for a chat and topic.
    /// </summary>
    /// <param name="c">The configuration.</param>
    /// <param name="chatId">The chat.</param>
    /// <param name="threadId">The topic, or 0.</param>
    /// <returns>The destination, or null.</returns>
    public static Destination? Find(PluginConfiguration c, long chatId, int threadId)
    {
        ArgumentNullException.ThrowIfNull(c);
        return c.Destinations.FirstOrDefault(d => d.ChatId == chatId && d.ThreadId == threadId);
    }

    /// <summary>
    /// Adds a destination for a chat that passed its check, or re-checks the existing one.
    /// </summary>
    /// <param name="check">The passed check.</param>
    /// <param name="threadId">The topic, or 0.</param>
    /// <param name="events">The events to subscribe to.</param>
    /// <param name="preset">The preset.</param>
    /// <param name="expectedRevision">The settings revision the caller saw, or null to skip the check.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>What happened.</returns>
    public async Task<CreateResult> CreateAsync(ChatCheckResult check, int threadId, string[] events, PresetKind preset, long? expectedRevision, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(events);

        // The duplicate rule (spec §6.2): an existing destination for this chat and topic is re-checked, not added.
        if (Find(_settings.Current, check.ChatId, threadId) is { } existing)
        {
            try
            {
                await _health.CheckNowAsync(existing.Id, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "TgJelly could not re-check destination {Destination}", existing.Title);
            }

            _memory.RemoveDetected(check.ChatId);
            return new CreateResult(CreateOutcome.AlreadyExists, existing, null, [], _settings.Current);
        }

        string? newId = null;
        var result = _settings.UpdateIf(expectedRevision, c =>
        {
            if (Find(c, check.ChatId, threadId) is not null)
            {
                return false;
            }

            newId = DestinationIds.New(c.Destinations.Select(d => d.Id));
            c.Destinations =
            [
                .. c.Destinations,
                new Destination
                {
                    Id = newId,
                    ChatId = check.ChatId,
                    ThreadId = threadId,
                    Title = check.Title,
                    Kind = check.Kind,
                    Preset = preset,
                    Events = events,
                },
            ];
            return true;
        });

        switch (result.Status)
        {
            case SettingsUpdateStatus.Unchanged:
            case SettingsUpdateStatus.Conflict when Find(result.Configuration, check.ChatId, threadId) is not null:
                // Another request added the same chat between our check and our save.
                _memory.RemoveDetected(check.ChatId);
                return new CreateResult(CreateOutcome.AlreadyExists, Find(result.Configuration, check.ChatId, threadId)!, null, [], result.Configuration);
            case SettingsUpdateStatus.Conflict:
                return new CreateResult(CreateOutcome.Conflict, null, null, [], result.Configuration);
            case SettingsUpdateStatus.Invalid:
                return new CreateResult(CreateOutcome.Invalid, null, null, result.Errors, result.Configuration);
        }

        var added = result.Configuration.Destinations.First(d => d.Id == newId);
        _memory.RemoveDetected(check.ChatId);
        _health.SetChecked(added.Id, check);
        var test = await _tester.SendTestAsync(added, ct).ConfigureAwait(false);
        return new CreateResult(CreateOutcome.Added, added, test, [], result.Configuration);
    }
}
