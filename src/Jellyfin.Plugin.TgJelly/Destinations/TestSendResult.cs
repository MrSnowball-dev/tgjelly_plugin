using Jellyfin.Plugin.TgJelly.State.Documents;

namespace Jellyfin.Plugin.TgJelly.Destinations;

/// <summary>
/// The outcome of a test send.
/// </summary>
/// <param name="Sent">Whether the test message was sent.</param>
/// <param name="Error">What went wrong, when not sent.</param>
/// <param name="Health">The destination's health afterwards.</param>
public sealed record TestSendResult(bool Sent, string? Error, DestinationHealth Health);
