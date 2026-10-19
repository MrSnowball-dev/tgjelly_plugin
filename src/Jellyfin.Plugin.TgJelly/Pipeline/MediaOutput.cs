using System.Collections.Generic;
using Jellyfin.Plugin.TgJelly.State.Documents;

namespace Jellyfin.Plugin.TgJelly.Pipeline;

/// <summary>
/// Groups released for one destination. The groups are copies, not the stored state.
/// </summary>
/// <param name="DestinationId">The destination.</param>
/// <param name="Kind">How they are announced.</param>
/// <param name="Groups">The groups: one for <see cref="MediaOutputKind.Group"/> and <see cref="MediaOutputKind.LateEdit"/>.</param>
public sealed record MediaOutput(string DestinationId, MediaOutputKind Kind, IReadOnlyList<MediaGroupState> Groups);
