using System;
using System.Collections.Generic;
using Jellyfin.Plugin.TgJelly.Library;

namespace Jellyfin.Plugin.TgJelly.Pipeline;

/// <summary>
/// An item that is ready to announce.
/// </summary>
/// <param name="Item">The item, as read when it became ready.</param>
/// <param name="Libraries">The libraries it belongs to.</param>
/// <param name="Upgraded">Whether it is an upgrade that <c>AnnounceUpgrades</c> lets through.</param>
/// <param name="NewSeries">Whether its series was added in the last 24 h.</param>
/// <param name="At">When it became ready.</param>
public sealed record MediaReady(MediaItem Item, IReadOnlyList<LibraryRef> Libraries, bool Upgraded, bool NewSeries, DateTimeOffset At);
