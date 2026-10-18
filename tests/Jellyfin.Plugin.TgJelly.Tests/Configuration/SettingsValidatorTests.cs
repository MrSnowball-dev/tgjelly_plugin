using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Configuration;

public class SettingsValidatorTests
{
    [Fact]
    public void Defaults_AreValid()
    {
        Assert.Empty(SettingsValidator.Validate(new PluginConfiguration()));
    }

    [Theory]
    [InlineData(nameof(BatchingOptions.QuietWindowSeconds), 4)]
    [InlineData(nameof(BatchingOptions.QuietWindowSeconds), 3601)]
    [InlineData(nameof(BatchingOptions.MaxHoldMinutes), 0)]
    [InlineData(nameof(BatchingOptions.MaxHoldMinutes), 241)]
    [InlineData(nameof(BatchingOptions.ReleaseQuietSeconds), 4)]
    [InlineData(nameof(BatchingOptions.ReleaseQuietSeconds), 601)]
    [InlineData(nameof(BatchingOptions.DigestThreshold), 1)]
    [InlineData(nameof(BatchingOptions.DigestThreshold), 101)]
    [InlineData(nameof(BatchingOptions.LateEditHours), -1)]
    [InlineData(nameof(BatchingOptions.LateEditHours), 25)]
    [InlineData(nameof(BatchingOptions.ReadyTimeoutMinutes), 0)]
    [InlineData(nameof(BatchingOptions.ReadyTimeoutMinutes), 121)]
    public void OutOfRange_IsReportedWithFieldPath(string field, int value)
    {
        var c = new PluginConfiguration();
        typeof(BatchingOptions).GetProperty(field)!.SetValue(c.Batching, value);

        var errors = SettingsValidator.Validate(c);

        Assert.Contains(errors, e => e.Field == "Batching." + field);
    }

    [Theory]
    [InlineData(nameof(BatchingOptions.QuietWindowSeconds), 5)]
    [InlineData(nameof(BatchingOptions.QuietWindowSeconds), 60)]
    [InlineData(nameof(BatchingOptions.LateEditHours), 0)]
    [InlineData(nameof(BatchingOptions.LateEditHours), 24)]
    [InlineData(nameof(BatchingOptions.DigestThreshold), 2)]
    public void Boundaries_AreAccepted(string field, int value)
    {
        var c = new PluginConfiguration();
        typeof(BatchingOptions).GetProperty(field)!.SetValue(c.Batching, value);

        Assert.Empty(SettingsValidator.Validate(c));
    }

    [Fact]
    public void MaxHold_ShorterThanQuietWindow_IsRejected()
    {
        var c = new PluginConfiguration();
        c.Batching.QuietWindowSeconds = 3600;
        c.Batching.MaxHoldMinutes = 30;

        Assert.Contains(SettingsValidator.Validate(c), e => e.Field == "Batching.MaxHoldMinutes");
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("ABCD1234")]
    [InlineData("abcd-234")]
    [InlineData("abcd12345")]
    public void BadDestinationId_IsRejected(string id)
    {
        var c = new PluginConfiguration { Destinations = [new Destination { Id = id, ChatId = 1 }] };

        Assert.Contains(SettingsValidator.Validate(c), e => e.Field == "Destinations[0].Id");
    }

    [Fact]
    public void DuplicateChatAndThread_IsRejected_ButSameChatOtherTopicIsFine()
    {
        var c = new PluginConfiguration
        {
            Destinations =
            [
                new Destination { Id = "aaaa0001", ChatId = -100, ThreadId = 0 },
                new Destination { Id = "aaaa0002", ChatId = -100, ThreadId = 7 },
                new Destination { Id = "aaaa0003", ChatId = -100, ThreadId = 0 },
            ],
        };

        var errors = SettingsValidator.Validate(c);

        Assert.Equal(["Destinations[2].ChatId"], errors.Select(e => e.Field));
    }

    [Fact]
    public void DuplicateId_IsRejected()
    {
        var c = new PluginConfiguration
        {
            Destinations =
            [
                new Destination { Id = "aaaa0001", ChatId = 1 },
                new Destination { Id = "aaaa0001", ChatId = 2 },
            ],
        };

        Assert.Equal(["Destinations[1].Id"], SettingsValidator.Validate(c).Select(e => e.Field));
    }

    [Fact]
    public void NullSectionsAndArrays_AreReported()
    {
        var c = new PluginConfiguration { Batching = null!, Destinations = [new Destination { Id = "abcd1234", Events = null! }] };

        var fields = SettingsValidator.Validate(c).Select(e => e.Field).ToList();

        Assert.Contains("Batching", fields);
        Assert.Contains("Destinations[0].Events", fields);
    }
}
