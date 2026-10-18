using System.Collections.Generic;

namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// The result of <see cref="SettingsStore.Update"/>.
/// </summary>
/// <param name="Status">What happened.</param>
/// <param name="Configuration">A copy of the settings now in effect (the saved ones, or the unchanged current ones).</param>
/// <param name="Errors">Validation errors when <paramref name="Status"/> is <see cref="SettingsUpdateStatus.Invalid"/>.</param>
public sealed record SettingsUpdateResult(
    SettingsUpdateStatus Status,
    PluginConfiguration Configuration,
    IReadOnlyList<SettingsError> Errors);
