using Jellyfin.Plugin.TgJelly.Intake;

namespace Jellyfin.Plugin.TgJelly.Tests.Fakes;

public sealed class FakeScanState : IScanState
{
    public bool IsScanRunning { get; set; }
}
