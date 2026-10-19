using System;

namespace Jellyfin.Plugin.TgJelly.State.Documents;

/// <summary>
/// One queued message for one destination (spec §9.3).
/// </summary>
public sealed class OutboxEntry
{
    /// <summary>Gets or sets the entry ID.</summary>
    public string EntryId { get; set; } = string.Empty;

    /// <summary>Gets or sets the destination ID.</summary>
    public string DestinationId { get; set; } = string.Empty;

    /// <summary>Gets or sets the event key; its group decides priority and caps.</summary>
    public string EventKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the group key (e.g. a media group).</summary>
    public string GroupKey { get; set; } = string.Empty;

    /// <summary>Gets or sets whether this sends or edits.</summary>
    public OutboxOp Op { get; set; }

    /// <summary>Gets or sets, for edits, the <c>sentMessages</c> key of the message to edit.</summary>
    public string? TargetKey { get; set; }

    /// <summary>Gets or sets the <c>sentMessages</c> key of the message to reply to; the entry waits while that one is queued.</summary>
    public string? ReplyToKey { get; set; }

    /// <summary>Gets or sets the key under which the sent message is recorded.</summary>
    public string? SentKey { get; set; }

    /// <summary>Gets or sets the rendered message.</summary>
    public OutboxMessage Message { get; set; } = new();

    /// <summary>Gets or sets the model the message was rendered from, if any.</summary>
    public RenderModel? Model { get; set; }

    /// <summary>Gets or sets when it was queued; the 24 h TTL counts from here.</summary>
    public DateTimeOffset QueuedAt { get; set; }

    /// <summary>Gets or sets how many retries it took.</summary>
    public int Attempts { get; set; }

    /// <summary>Gets or sets how many photo uploads failed with a transport error.</summary>
    public int PhotoFailures { get; set; }

    /// <summary>Gets or sets a value indicating whether it was already resent after a chat migration.</summary>
    public bool Migrated { get; set; }

    /// <summary>Gets or sets the earliest time to try again.</summary>
    public DateTimeOffset? NotBefore { get; set; }

    /// <summary>Gets or sets, for the rolling summary entry, how many events it stands for; 0 otherwise.</summary>
    public int Summary { get; set; }
}
