using System;
using Jellyfin.Plugin.TgJelly.State.Documents;

namespace Jellyfin.Plugin.TgJelly.Delivery;

/// <summary>
/// The result of <see cref="Outbox.Pick"/>.
/// </summary>
/// <param name="Entry">A copy of the entry to deliver now, or <c>null</c>.</param>
/// <param name="WakeAt">The earliest time a waiting entry could become deliverable, or <c>null</c>.</param>
public sealed record OutboxPick(OutboxEntry? Entry, DateTimeOffset? WakeAt);
