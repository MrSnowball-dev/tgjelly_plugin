using System;
using Jellyfin.Plugin.TgJelly.State.Documents;

namespace Jellyfin.Plugin.TgJelly.Delivery;

/// <summary>
/// The latest model of a message sent (or queued) under a key, from <see cref="Outbox.FindModel"/>.
/// </summary>
/// <param name="Model">The latest model.</param>
/// <param name="SentAt">When it was sent; when it was queued, for a queued one.</param>
/// <param name="Queued">Whether the message hasn't been sent yet.</param>
public sealed record SentRecord(RenderModel Model, DateTimeOffset SentAt, bool Queued);
