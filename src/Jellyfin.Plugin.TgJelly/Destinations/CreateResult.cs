using System.Collections.Generic;
using Jellyfin.Plugin.TgJelly.Configuration;

namespace Jellyfin.Plugin.TgJelly.Destinations;

/// <summary>
/// The result of creating a destination.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Destination">The added or already existing destination; null for <see cref="CreateOutcome.Conflict"/> and <see cref="CreateOutcome.Invalid"/>.</param>
/// <param name="Test">The test message result; only for <see cref="CreateOutcome.Added"/>.</param>
/// <param name="Errors">The validation errors; only for <see cref="CreateOutcome.Invalid"/>.</param>
/// <param name="Configuration">The configuration the outcome was decided against (the current one on a conflict).</param>
public sealed record CreateResult(CreateOutcome Outcome, Destination? Destination, TestSendResult? Test, IReadOnlyList<SettingsError> Errors, PluginConfiguration Configuration);
