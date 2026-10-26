namespace Jellyfin.Plugin.TgJelly.Log;

/// <summary>
/// What happened to one message for one destination. When a send has several details, the row takes
/// the most serious: <see cref="Failed"/>, then <see cref="SentWithBuiltIn"/>, <see cref="SentWithoutPhoto"/> and
/// <see cref="SentLate"/>.
/// </summary>
public enum LogResult
{
    /// <summary>Held for batching, queued, or waiting to retry.</summary>
    Waiting,

    /// <summary>Not sent on purpose: the destination doesn't get it, or there was nothing to send.</summary>
    Skipped,

    /// <summary>Sent, with nothing to report.</summary>
    Sent,

    /// <summary>Sent more than a minute after it was ready, or after a failed try.</summary>
    SentLate,

    /// <summary>Sent from the built-in template because the edited one failed.</summary>
    SentWithBuiltIn,

    /// <summary>Sent as text alone although it had a poster.</summary>
    SentWithoutPhoto,

    /// <summary>Telegram refused it, or it couldn't be delivered in time.</summary>
    Failed,

    /// <summary>A test message (Send test, or the one sent when a destination is added) that went out.</summary>
    TestSent,

    /// <summary>A test message that didn't go out; it counts as a problem.</summary>
    TestFailed,
}
