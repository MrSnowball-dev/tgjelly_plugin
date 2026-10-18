using System.Threading;
using Jellyfin.Plugin.TgJelly.Bot;

namespace Jellyfin.Plugin.TgJelly.Tests.Fakes;

public sealed class FakeSupervisorControl : ISupervisorControl
{
    private int _restarts;

    public int Restarts => Volatile.Read(ref _restarts);

    public void Restart() => Interlocked.Increment(ref _restarts);
}
