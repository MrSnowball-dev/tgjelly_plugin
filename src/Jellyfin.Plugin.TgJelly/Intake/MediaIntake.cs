using System;
using Jellyfin.Plugin.TgJelly.Library;

namespace Jellyfin.Plugin.TgJelly.Intake;

/// <summary>
/// One event on the media intake queue.
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="Item">The item; <c>null</c> for <see cref="MediaIntakeKind.ScanFinished"/>.</param>
/// <param name="At">When.</param>
public sealed record MediaIntake(MediaIntakeKind Kind, ItemFacts? Item, DateTimeOffset At);
