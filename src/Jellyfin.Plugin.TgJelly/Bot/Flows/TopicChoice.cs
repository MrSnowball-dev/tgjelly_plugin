using Jellyfin.Plugin.TgJelly.Destinations;

namespace Jellyfin.Plugin.TgJelly.Bot.Flows;

/// <summary>
/// The outcome of the topic step: where in a forum the destination posts (spec §6.2 step 3).
/// </summary>
/// <param name="Check">The check of the forum chat.</param>
/// <param name="ThreadId">The topic's thread ID, or 0 for the General topic.</param>
/// <param name="WizardMessageId">The wizard message to edit next.</param>
public sealed record TopicChoice(ChatCheckResult Check, int ThreadId, int? WizardMessageId);
