using System;
using System.IO;
using Jellyfin.Plugin.TgJelly.State;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.State;

public class AtomicFileTests
{
    [Fact]
    public void WriteAllText_CreatesFolderAndFile_AndLeavesNoTempFiles()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "sub", "file.json");

        AtomicFile.WriteAllText(path, "one");
        AtomicFile.WriteAllText(path, "two");

        Assert.Equal("two", File.ReadAllText(path));
        Assert.Equal(["file.json"], Array.ConvertAll(Directory.GetFiles(Path.GetDirectoryName(path)!), Path.GetFileName));
    }

    [Fact]
    public void WriteAllText_OwnerOnly_UsesMode600OnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "secrets.json");

        AtomicFile.WriteAllText(path, "{}", ownerOnly: true);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void Quarantine_MovesFileAsideWithTimestamp()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "state.json");
        File.WriteAllText(path, "{not json");

        var moved = AtomicFile.Quarantine(path, new DateTimeOffset(2026, 10, 16, 12, 30, 5, TimeSpan.Zero));

        Assert.False(File.Exists(path));
        Assert.Equal(Path.Combine(dir.Path, "state.bad-20261016123005.json"), moved);
        Assert.Equal("{not json", File.ReadAllText(moved));
    }

    [Fact]
    public void TwoQuarantinesInTheSameSecond_KeepBothFiles()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "state.json");
        var now = new DateTimeOffset(2026, 10, 18, 10, 0, 0, TimeSpan.Zero);

        File.WriteAllText(path, "first");
        var a = AtomicFile.Quarantine(path, now);
        File.WriteAllText(path, "second");
        var b = AtomicFile.Quarantine(path, now);

        Assert.NotEqual(a, b);
        Assert.Equal("first", File.ReadAllText(a));
        Assert.Equal("second", File.ReadAllText(b));
        Assert.EndsWith("-1.json", b, StringComparison.Ordinal);
    }
}
