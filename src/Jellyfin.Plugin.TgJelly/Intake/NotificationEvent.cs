using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TgJelly.Intake;

/// <summary>
/// A security or server event as intake saw it: immutable, plain data.
/// </summary>
/// <param name="EventKey">The event key.</param>
/// <param name="At">When it happened.</param>
/// <param name="Values">The payload, keyed by <see cref="EventFields"/>.</param>
public sealed record NotificationEvent(string EventKey, DateTimeOffset At, IReadOnlyDictionary<string, string> Values);
