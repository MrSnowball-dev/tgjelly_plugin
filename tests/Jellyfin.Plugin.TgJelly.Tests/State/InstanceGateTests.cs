using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.State;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.State;

public class InstanceGateTests
{
    [Fact]
    public void Lock_IsExclusive_EvenInsideOneProcess()
    {
        using var dir = new TempDir();
        var path = new DataPaths(dir.Path).Lock;

        using (var first = InstanceLock.TryAcquire(path))
        {
            Assert.NotNull(first);
            Assert.Null(InstanceLock.TryAcquire(path));
        }

        using var again = InstanceLock.TryAcquire(path);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task FreeLock_IsOwnedRightAway_AndReleasedOnStop()
    {
        using var dir = new TempDir();
        var paths = new DataPaths(dir.Path);
        using var gate = new InstanceGate(paths, new FakeTimeProvider(), NullLogger<InstanceGate>.Instance);

        await gate.StartAsync(CancellationToken.None);
        await gate.WaitForOwnershipAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(gate.IsOwner);
        Assert.Null(gate.WaitingMessage);
        Assert.Null(InstanceLock.TryAcquire(paths.Lock));

        await gate.StopAsync(CancellationToken.None);
        using var after = InstanceLock.TryAcquire(paths.Lock);
        Assert.NotNull(after);
    }

    [Fact]
    public async Task BusyLock_ReportsWaiting_ThenForeign_ThenAcquiresWhenFreed()
    {
        using var dir = new TempDir();
        var paths = new DataPaths(dir.Path);
        var time = new FakeTimeProvider();
        var other = InstanceLock.TryAcquire(paths.Lock);
        using var gate = new InstanceGate(paths, time, NullLogger<InstanceGate>.Instance);

        await gate.StartAsync(CancellationToken.None);
        await Eventually.SettleAsync();
        Assert.False(gate.IsOwner);
        Assert.Equal(InstanceGate.WaitingForPreviousInstance, gate.WaitingMessage);

        await Eventually.AdvanceUntilAsync(
            time,
            TimeSpan.FromSeconds(5),
            () => gate.WaitingMessage == InstanceGate.AnotherProcessUsesFolder,
            "foreign message after 2 min");

        other!.Dispose();
        await Eventually.AdvanceUntilAsync(time, TimeSpan.FromSeconds(5), () => gate.IsOwner, "acquired after the other holder released");

        await gate.StopAsync(CancellationToken.None);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task UnwritableFolder_ReportsAPermissionProblem_ThenAcquiresOnceFixed()
    {
        // Review Focus 1: e.g. a Docker volume with the wrong owner. This is not "another instance".
        Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.UserName == "root", "needs Unix permissions as a normal user");
        using var dir = new TempDir();
        var paths = new DataPaths(dir.Path);
        Directory.CreateDirectory(paths.Root);
        File.SetUnixFileMode(paths.Root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var time = new FakeTimeProvider();
        using var gate = new InstanceGate(paths, time, NullLogger<InstanceGate>.Instance);
        try
        {
            await gate.StartAsync(CancellationToken.None);
            await Eventually.TrueAsync(
                () => gate.WaitingMessage?.StartsWith(InstanceGate.CannotWriteFolderPrefix, StringComparison.Ordinal) == true,
                "permission problem reported");
            Assert.Contains(paths.Root, gate.WaitingMessage, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(paths.Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        await Eventually.AdvanceUntilAsync(time, TimeSpan.FromSeconds(5), () => gate.IsOwner, "acquired once the folder became writable");
        await gate.StopAsync(CancellationToken.None);
    }
}
