using System.Runtime.CompilerServices;
using Jellyfin.Plugin.TgJelly.Configuration;

namespace Jellyfin.Plugin.TgJelly.Tests;

internal static class SnapshotCheck
{
    // Every test checks that no code changes the shared settings copy in place.
    [ModuleInitializer]
    internal static void TurnOn() => SettingsStore.VerifySnapshots = true;
}
