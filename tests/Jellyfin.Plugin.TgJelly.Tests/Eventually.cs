using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests;

/// <summary>
/// Waits (in real time) for background loops driven by a FakeTimeProvider to catch up.
/// </summary>
public static class Eventually
{
    public static async Task TrueAsync(Func<bool> condition, string because)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5))
            {
                Assert.Fail("Timed out waiting: " + because);
            }

            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Gives background continuations a moment to run, for asserting that something did NOT happen.
    /// </summary>
    public static Task SettleAsync() => Task.Delay(100);

    /// <summary>
    /// Moves fake time forward in steps until <paramref name="condition"/> holds. A loop registers its next timer
    /// only after it has run, so each step first lets it settle; a single big Advance could happen before the timer
    /// exists and then never fire.
    /// </summary>
    public static async Task AdvanceUntilAsync(FakeTimeProvider time, TimeSpan step, Func<bool> condition, string because, int maxSteps = 40)
    {
        ArgumentNullException.ThrowIfNull(time);
        for (var i = 0; i < maxSteps && !condition(); i++)
        {
            await SettleAsync();
            time.Advance(step);
        }

        await TrueAsync(condition, because);
    }
}
