using System;
using System.IO;
using Jellyfin.Plugin.TgJelly.Library;
using Jellyfin.Plugin.TgJelly.Rendering;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Rendering;

public sealed class PosterPickerTests : IDisposable
{
    private static readonly DateTime Modified = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly TempDir _dir = new();

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".JPEG")]
    [InlineData(".png")]
    [InlineData(".webp")]
    public void PicksALocalSmallImage(string ext)
    {
        var id = Guid.NewGuid();
        var path = File(ext, 3);

        var poster = PosterPicker.Pick([(id, new ImageRef(path, true, Modified))]);

        Assert.Equal(new PosterRef(path, $"{id:N}:{Modified.Ticks}"), poster);
    }

    [Fact]
    public void SkipsIneligible_AndTakesTheFirstEligible()
    {
        var good = Guid.NewGuid();
        var path = File(".jpg", 3);
        var poster = PosterPicker.Pick([
            (Guid.NewGuid(), null),
            (Guid.NewGuid(), new ImageRef(File(".jpg", 3), false, Modified)),
            (Guid.NewGuid(), new ImageRef(Path.Combine(_dir.Path, "missing.jpg"), true, Modified)),
            (Guid.NewGuid(), new ImageRef(File(".jpg", PosterPicker.MaxBytes + 1), true, Modified)),
            (Guid.NewGuid(), new ImageRef(File(".gif", 3), true, Modified)),
            (good, new ImageRef(path, true, Modified)),
        ]);

        Assert.Equal(path, poster!.Path);
        Assert.StartsWith(good.ToString("N"), poster.CacheKey, StringComparison.Ordinal);
    }

    [Fact]
    public void ExactlyTenMegabytes_IsAllowed()
        => Assert.NotNull(PosterPicker.Pick([(Guid.NewGuid(), new ImageRef(File(".jpg", PosterPicker.MaxBytes), true, Modified))]));

    public void Dispose() => _dir.Dispose();

    private string File(string ext, long size)
    {
        var path = Path.Combine(_dir.Path, Guid.NewGuid().ToString("N") + ext);
        using var stream = System.IO.File.Create(path);
        stream.SetLength(size);
        return path;
    }
}
