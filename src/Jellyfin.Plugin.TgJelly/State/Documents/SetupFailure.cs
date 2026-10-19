using System;

namespace Jellyfin.Plugin.TgJelly.State.Documents;

/// <summary>
/// A recent setup failure, kept so the dashboard and the bot can point at it.
/// </summary>
public sealed class SetupFailure
{
    /// <summary>Gets or sets what went wrong.</summary>
    public SetupFailureKind Kind { get; set; }

    /// <summary>Gets or sets when it happened.</summary>
    public DateTimeOffset At { get; set; }
}
