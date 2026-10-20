using System;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Templates;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Templates;

public sealed class TemplateFlagsTests : IDisposable
{
    private readonly BotTestHarness _h = new();
    private readonly TemplateFlags _flags;

    public TemplateFlagsTests()
    {
        _flags = new TemplateFlags(_h.State, _h.Settings, _h.Time);
        Enable("old text");
    }

    [Fact]
    public void SameFlag_NoSecondWrite()
    {
        var hash = TemplateText.Hash("old text");
        _flags.Flag("security.lockout", "error", "boom", hash);
        var first = _flags.Current("security.lockout")!.At;
        _h.Time.Advance(TimeSpan.FromMinutes(5));

        _flags.Flag("security.lockout", "error", "boom", hash);

        Assert.Equal(first, _flags.Current("security.lockout")!.At);

        _flags.Flag("security.lockout", "error", "other", hash);

        Assert.NotEqual(first, _flags.Current("security.lockout")!.At);
    }

    [Fact]
    public void TextChanged_FlagNotCurrent()
    {
        _flags.Flag("security.lockout", "error", "boom", TemplateText.Hash("old text"));
        Assert.NotNull(_flags.Current("security.lockout"));

        Enable("new text");

        Assert.Null(_flags.Current("security.lockout"));
    }

    [Fact]
    public void Disabled_FlagNotCurrent()
    {
        _flags.Flag("security.lockout", "truncated", string.Empty, TemplateText.Hash("old text"));

        Enable("old text", enabled: false);

        Assert.Null(_flags.Current("security.lockout"));
    }

    [Fact]
    public void Clear_RemovesIt()
    {
        _flags.Flag("security.lockout", "error", "boom", TemplateText.Hash("old text"));

        _flags.Clear("security.lockout");

        Assert.Null(_flags.Current("security.lockout"));
    }

    public void Dispose() => _h.Dispose();

    private void Enable(string text, bool enabled = true)
        => _h.Settings.Update(null, c => c.Templates = [new TemplateOverride { EventKey = "security.lockout", Enabled = enabled, Text = text }]);
}
